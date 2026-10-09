using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using SignIt.Infrastructure.Email;
using SignIt.Infrastructure.Storage;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Letters.Services;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Services;
using SignIt.Modules.Templates.Services;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;
using Xunit;

namespace SignIt.Signatures.Tests;

[Collection("Preview PostgreSQL")]
public sealed class WorkflowPostgresTests(PreviewPostgresFixture fixture)
{
    private sealed record LetterFixture(Guid Owner, Guid Organization, Guid Committee, Guid Chair,
        Guid? Resource, SubmissionDto Submitted, Dictionary<string, string> Fields);

    private async Task<LetterFixture> SubmitAsync(IServiceProvider services, string type = "proposal", string? facilityCode = null)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var owner = await db.Users.SingleAsync(x => x.Email == "pengaju-ukki@demo.signit.example");
        var organization = await db.Organizations.SingleAsync(x => x.Scope == "ukki");
        var committee = await db.Users.SingleAsync(x => x.Email == "ketupel@demo.signit.example");
        var chair = await db.Users.SingleAsync(x => x.Email == "ketua@demo.signit.example");
        Guid? resource = facilityCode == null ? null : await db.FacilityResources
            .Where(x => x.IsBookable && db.Facilities.Any(f => f.Id == x.FacilityId && f.Code == facilityCode)).Select(x => x.Id).FirstAsync();
        var template = (await services.GetRequiredService<TemplateCatalog>().GetAllAsync(default)).Single(x => x.TypeId == type);
        var values = TemplateRendererTests.Fields(template);
        var fields = template.Fields.Where(x => x.ValueSource == "user").ToDictionary(x => x.Key, x => values[x.Key]);
        var draft = await services.GetRequiredService<LettersService>().CreateAsync(owner.Id, new(type, "Workflow QA", fields), default);
        var preview = await GenerateAsync(services, owner.Id, draft, organization.Id, committee.Id, chair.Id, resource);
        var submitted = await services.GetRequiredService<LetterSubmissionService>().SubmitAsync(owner.Id, draft.Id,
            new(draft.Version, draft.RevisionId, draft.ContentHash, organization.Id, committee.Id, chair.Id, resource,
                preview.ReviewDocumentId!.Value, preview.ReviewHash!, preview.Slots), Guid.NewGuid().ToString("N"), default);
        return new(owner.Id, organization.Id, committee.Id, chair.Id, resource, submitted, fields);
    }

    private async Task<LetterPreviewDto> GenerateAsync(IServiceProvider services, Guid owner, DraftDto draft, Guid organization, Guid committee, Guid chair, Guid? resource)
    {
        var previews = services.GetRequiredService<LetterPreviewService>();
        var queued = await previews.QueueAsync(owner, draft.Id, new(draft.Version, draft.RevisionId, draft.ContentHash, organization, committee, chair, resource), default);
        var state = queued;
        for (var i = 0; i < 100 && state.State is "Pending" or "Processing"; i++)
        {
            await services.GetRequiredService<LetterPreviewProcessor>().ProcessNextAsync(default);
            state = await previews.GetAsync(owner, draft.Id, queued.JobId, default);
        }
        Assert.Equal("Ready", state.State);
        return state;
    }

    private static SignTaskRequest SignRequest(WorkflowTask task, LetterRevision revision) => new(revision.Id, revision.ContentHash, "QA reviewed", task.RowVersion);
    private static WorkflowMutationRequest Mutation(WorkflowTask task, LetterRevision revision, string reason = "Catatan QA") => new(revision.Id, revision.ContentHash, task.RowVersion, reason);

    private static ISignatureWorkflowService IsolatedSignatureWorkflow(IServiceProvider services, bool fail = false) => new SignatureWorkflowService(
        services.GetRequiredService<AppDbContext>(), services.GetRequiredService<IUserSignatureQrService>(), services.GetRequiredService<IQrCodeGenerator>(),
        new WorkflowTestPdfAdapter(fail), services.GetRequiredService<IStorageService>(), TimeProvider.System,
        NullLogger<SignatureWorkflowService>.Instance, services.GetRequiredService<WorkflowTaskAccess>(),
        services.GetRequiredService<WorkflowEmailService>(), services.GetRequiredService<WorkflowOptions>());

    [PostgresPreviewTheory]
    [InlineData("proposal", null, 5)]
    [InlineData("lpj", null, 5)]
    [InlineData("peminjaman-barang", null, 6)]
    [InlineData("peminjaman-ruangan", "PS", 6)]
    [InlineData("peminjaman-ruangan", "LAPANGAN_MERAH", 7)]
    public async Task SequentialChain_OnlyActiveActor_CreatesExactlyOneEvidencePerStage(string type, string? facility, int count)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var submitted = await SubmitAsync(services, type, facility);
        var db = services.GetRequiredService<AppDbContext>();
        var signature = IsolatedSignatureWorkflow(services);
        var queries = services.GetRequiredService<WorkflowQueryService>();
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == submitted.Submitted.RevisionId);
        var frozen = revision.DataJson;
        var tasks = await db.WorkflowTasks.Where(x => x.RevisionId == revision.Id).OrderBy(x => x.Order).ToListAsync();
        Assert.Equal(count, tasks.Count);
        var pendingError = await Assert.ThrowsAsync<SignItDomainException>(() => signature.ExecuteTaskActionAsync(tasks[1].Id,
            tasks[1].AssignedUserId, tasks[1].ActionType, SignRequest(tasks[1], revision), "early", null, null, default));
        Assert.Equal("task_not_active", pendingError.Code);
        db.ChangeTracker.Clear();
        for (var order = 1; order <= count; order++)
        {
            db.ChangeTracker.Clear();
            var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == revision.Id && x.Order == order);
            var dto = await queries.GetAsync(task.AssignedUserId, task.Id, default);
            Assert.Contains(task.ActionType == WorkflowActionType.Sign ? "sign" : "approve", dto.AllowedActions);
            Assert.NotEmpty(await queries.DownloadAsync(task.AssignedUserId, task.Id, default));
            var request = SignRequest(task, revision);
            var key = "sign-" + order;
            var result = await signature.ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, request, key, null, null, default);
            var replay = await signature.ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, request, key, null, null, default);
            Assert.Equal(result, replay);
            Assert.Equal(order, await db.SignatureEvidences.CountAsync(x => x.RevisionId == revision.Id));
            Assert.Equal(order == count ? 0 : 1, await db.WorkflowTasks.CountAsync(x => x.RevisionId == revision.Id && x.Status == WorkflowTaskStatus.Active));
            if (order == count)
            {
                Assert.False(result.IsWorkflowCompleted);
                Assert.True(await signature.RetryFinalizationAsync(submitted.Submitted.LetterId, default));
            }
        }
        Assert.Equal(frozen, (await db.LetterRevisions.AsNoTracking().SingleAsync(x => x.Id == revision.Id)).DataJson);
        Assert.Equal(1, await db.Documents.CountAsync(x => x.RevisionId == revision.Id && x.Kind == DocumentKind.Final));
        var timeline = await queries.LetterAsync(submitted.Owner, submitted.Submitted.LetterId, default);
        Assert.Contains(timeline.Timeline, x => x.Action == "workflow.signed");
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task Revision_RepeatsAllStages_PreservesHistoricalEvidenceAndNumber(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var db = services.GetRequiredService<AppDbContext>();
        var original = await db.LetterRevisions.SingleAsync(x => x.Id == data.Submitted.RevisionId);
        var first = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == original.Id && x.Order == 1);
        await services.GetRequiredService<ISignatureWorkflowService>().ExecuteTaskActionAsync(first.Id, first.AssignedUserId, first.ActionType, SignRequest(first, original), "first", null, null, default);
        var second = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == original.Id && x.Order == 2);
        var decision = await services.GetRequiredService<WorkflowService>().MutateAsync(second.AssignedUserId, second.Id, "request-revision", Mutation(second, original, "Perbaiki lampiran"), "revision", default);
        Assert.Equal(LetterStatus.NeedsRevision, decision.LetterStatus);
        Assert.Equal(0, await db.WorkflowTasks.CountAsync(x => x.RevisionId == original.Id && x.Status == WorkflowTaskStatus.Active));
        var letters = services.GetRequiredService<LettersService>();
        var oldDraft = await letters.GetAsync(data.Owner, data.Submitted.LetterId, default);
        var fields = new Dictionary<string, string>(data.Fields) { ["nama_kegiatan"] = "Kegiatan revisi" };
        var edited = await letters.EditAsync(data.Owner, oldDraft.Id, new(oldDraft.Version, oldDraft.RevisionId, oldDraft.ContentHash, "Judul revisi", fields), "edit", default);
        Assert.NotEqual(original.Id, edited.RevisionId);
        var preview = await GenerateAsync(services, data.Owner, edited, data.Organization, data.Committee, data.Chair, data.Resource);
        var resubmit = await services.GetRequiredService<LetterSubmissionService>().SubmitAsync(data.Owner, edited.Id,
            new(edited.Version, edited.RevisionId, edited.ContentHash, data.Organization, data.Committee, data.Chair, data.Resource,
                preview.ReviewDocumentId!.Value, preview.ReviewHash!, preview.Slots), "resubmit", default);
        Assert.Equal(data.Submitted.Number, resubmit.Number);
        Assert.Equal(1, await db.SignatureEvidences.CountAsync(x => x.RevisionId == original.Id));
        Assert.Equal(0, await db.SignatureEvidences.CountAsync(x => x.RevisionId == resubmit.RevisionId));
        var fresh = await db.WorkflowTasks.Where(x => x.RevisionId == resubmit.RevisionId).OrderBy(x => x.Order).ToListAsync();
        Assert.Equal(5, fresh.Count);
        Assert.Equal(WorkflowTaskStatus.Active, fresh[0].Status);
        Assert.All(fresh.Skip(1), x => Assert.Equal(WorkflowTaskStatus.Pending, x.Status));
        var stale = await Assert.ThrowsAsync<SignItDomainException>(() => services.GetRequiredService<WorkflowService>()
            .MutateAsync(second.AssignedUserId, second.Id, "reject", Mutation(second, original), "stale-action", default));
        Assert.Equal("inactive_letter_revision", stale.Code);
    }

    [PostgresPreviewTheory]
    [InlineData("reject")]
    [InlineData("request-revision")]
    public async Task NegativeDecision_ValidatesReasonVersionAndPayload_StopsRemainingTasks(string action)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services);
        var db = services.GetRequiredService<AppDbContext>();
        var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 1);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId);
        var workflow = services.GetRequiredService<WorkflowService>();
        var request = Mutation(task, revision);
        Assert.Equal("reason_required", (await Assert.ThrowsAsync<SignItDomainException>(() => workflow.MutateAsync(task.AssignedUserId, task.Id, action, request with { Reason = " " }, "empty", default))).Code);
        Assert.Equal("stale_task", (await Assert.ThrowsAsync<SignItDomainException>(() => workflow.MutateAsync(task.AssignedUserId, task.Id, action, request with { ExpectedTaskVersion = Guid.NewGuid() }, "stale", default))).Code);
        Assert.Equal("revision_hash_mismatch", (await Assert.ThrowsAsync<SignItDomainException>(() => workflow.MutateAsync(task.AssignedUserId, task.Id, action, request with { ExpectedContentHash = new string('0', 64) }, "hash", default))).Code);
        var result = await workflow.MutateAsync(task.AssignedUserId, task.Id, action, request, "decision", default);
        Assert.Equal(result, await workflow.MutateAsync(task.AssignedUserId, task.Id, action, request, "decision", default));
        Assert.Equal("idempotency_payload_conflict", (await Assert.ThrowsAsync<SignItDomainException>(() => workflow.MutateAsync(task.AssignedUserId, task.Id, action, request with { Reason = "Berbeda" }, "decision", default))).Code);
        Assert.Equal(4, await db.WorkflowTasks.CountAsync(x => x.RevisionId == task.RevisionId && x.Status == WorkflowTaskStatus.Cancelled));
        Assert.False(await db.SignatureEvidences.AnyAsync(x => x.RevisionId == task.RevisionId));
    }

    [PostgresPreviewTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ConcurrentSigning_IsAtomicAndIdempotent(bool sameKey)
    {
        using var scope = fixture.Provider.CreateScope();
        var data = await SubmitAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 1);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId);
        var request = SignRequest(task, revision);
        async Task<object> Sign(string key)
        {
            using var concurrent = fixture.Provider.CreateScope();
            try { return await concurrent.ServiceProvider.GetRequiredService<ISignatureWorkflowService>().ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, request, key, null, null, default); }
            catch (SignItDomainException exception) { return exception; }
        }
        var results = await Task.WhenAll(Sign("concurrent-a"), Sign(sameKey ? "concurrent-a" : "concurrent-b"));
        Assert.Equal(sameKey ? 2 : 1, results.Count(x => x is SignTaskResultDto));
        if (!sameKey) Assert.Equal("task_not_active", Assert.IsType<SignItDomainException>(results.Single(x => x is SignItDomainException)).Code);
        Assert.Equal(1, await db.SignatureEvidences.CountAsync(x => x.TaskId == task.Id));
        Assert.Equal(1, await db.WorkflowTasks.CountAsync(x => x.RevisionId == revision.Id && x.Status == WorkflowTaskStatus.Active));
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task Deferral_DoesNotSkipRequiredStage_ResumeRequiresFreshVersion(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var db = services.GetRequiredService<AppDbContext>();
        var first = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 1);
        var second = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 2);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == first.RevisionId);
        var request = Mutation(first, revision) with { Until = DateTimeOffset.UtcNow.AddHours(3) };
        var workflow = services.GetRequiredService<WorkflowService>();
        var deferred = await workflow.MutateAsync(first.AssignedUserId, first.Id, "defer", request, "defer", default);
        Assert.Equal(WorkflowTaskStatus.Deferred, deferred.Status);
        Assert.Equal(WorkflowTaskStatus.Pending, second.Status);
        var detail = await services.GetRequiredService<WorkflowQueryService>().GetAsync(first.AssignedUserId, first.Id, default);
        Assert.Equal(new[] { "resume" }, detail.AllowedActions);
        var resume = request with { ExpectedTaskVersion = deferred.TaskVersion };
        await workflow.MutateAsync(first.AssignedUserId, first.Id, "resume", resume, "resume", default);
        Assert.Equal(WorkflowTaskStatus.Active, first.Status);
        Assert.Equal(WorkflowTaskStatus.Pending, second.Status);
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task Delegation_IsTaskScoped_Eligible_OneLevel_UsesActualActorQr(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var another = await SubmitAsync(services, type);
        var db = services.GetRequiredService<AppDbContext>();
        var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 1);
        var otherTask = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == another.Submitted.RevisionId && x.Order == 1);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId);
        var deputy = User.Provision(Guid.NewGuid(), "Deputy QA", $"deputy-{Guid.NewGuid():N}@test.example", null, UserCategory.StudentGeneral, "test-only-not-login", DateTimeOffset.UtcNow, true);
        db.Users.Add(deputy);
        db.Assignments.Add(UserAssignment.Provision(Guid.NewGuid(), deputy.Id, "Ketupel", "Ketua Pelaksana", "ukki", UserCapability.Signer, DateTimeOffset.UtcNow.AddDays(-1), null));
        await db.SaveChangesAsync();
        var workflow = services.GetRequiredService<WorkflowService>();
        var request = Mutation(task, revision) with { DelegateUserId = deputy.Id, Until = DateTimeOffset.UtcNow.AddHours(4) };
        var delegated = await workflow.MutateAsync(task.AssignedUserId, task.Id, "delegate", request, "delegate", default);
        var queries = services.GetRequiredService<WorkflowQueryService>();
        var inbox = await queries.QueueAsync(deputy.Id, 1, 100, default);
        Assert.Contains(await queries.CandidatesAsync(task.AssignedUserId, task.Id, default), x => x.UserId == deputy.Id);
        Assert.Contains(inbox.Items, x => x.Id == task.Id);
        Assert.DoesNotContain(inbox.Items, x => x.Id == otherTask.Id);
        Assert.Equal("delegation_depth_exceeded", (await Assert.ThrowsAsync<SignItDomainException>(() => workflow.MutateAsync(deputy.Id, task.Id, "delegate", request with { ExpectedTaskVersion = delegated.TaskVersion, DelegateUserId = data.Chair }, "chain", default))).Code);
        var signature = services.GetRequiredService<ISignatureWorkflowService>();
        var otherRevision = await db.LetterRevisions.SingleAsync(x => x.Id == otherTask.RevisionId);
        Assert.Equal("forbidden_task_actor", (await Assert.ThrowsAsync<SignItDomainException>(() => signature.ExecuteTaskActionAsync(otherTask.Id, deputy.Id, otherTask.ActionType,
            new(otherTask.RevisionId, otherRevision.ContentHash, ExpectedTaskVersion: otherTask.RowVersion), "other", null, null, default))).Code);
        var signed = await signature.ExecuteTaskActionAsync(task.Id, deputy.Id, task.ActionType, SignRequest(task, revision), "deputy", null, null, default);
        var evidence = await db.SignatureEvidences.SingleAsync(x => x.Id == signed.EvidenceId);
        Assert.Equal(deputy.Id, evidence.ActorId);
        Assert.Equal(task.AssignedUserId, evidence.DelegatedFromUserId);
        Assert.Equal(deputy.Id, (await db.SignatureQrs.SingleAsync(x => x.Id == evidence.QrAssetId)).OwnerUserId);
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task OwnerCancellation_StopsTasks_DeniesOtherOwnerAndOldActor(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var letters = services.GetRequiredService<LettersService>();
        var draft = await letters.GetAsync(data.Owner, data.Submitted.LetterId, default);
        var request = new CancelLetterRequest(draft.Version, draft.RevisionId, draft.ContentHash, "Kegiatan dibatalkan");
        Assert.Equal(DomainErrorKind.NotFound, (await Assert.ThrowsAsync<SignItDomainException>(() => letters.CancelAsync(data.Committee, draft.Id, request, "foreign", default))).Kind);
        var result = await letters.CancelAsync(data.Owner, draft.Id, request, "cancel", default);
        Assert.Equal(result, await letters.CancelAsync(data.Owner, draft.Id, request, "cancel", default));
        var db = services.GetRequiredService<AppDbContext>();
        Assert.Equal(5, await db.WorkflowTasks.CountAsync(x => x.RevisionId == draft.RevisionId && x.Status == WorkflowTaskStatus.Cancelled));
        var task = await db.WorkflowTasks.FirstAsync(x => x.RevisionId == draft.RevisionId);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId);
        Assert.Equal("inactive_letter_revision", (await Assert.ThrowsAsync<SignItDomainException>(() => services.GetRequiredService<ISignatureWorkflowService>()
            .ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, SignRequest(task, revision), "after-cancel", null, null, default))).Code);
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task PdfFailure_DoesNotLoseApprovalOrRequireSigningAgain(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var db = services.GetRequiredService<AppDbContext>();
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == data.Submitted.RevisionId);
        for (var order = 1; order <= 5; order++)
        {
            var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == revision.Id && x.Order == order);
            var signature = IsolatedSignatureWorkflow(services, fail: order == 5);
            var request = SignRequest(task, revision);
            var result = await signature.ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, request, $"fail-{order}", null, null, default);
            if (order == 5)
            {
                Assert.False(result.IsWorkflowCompleted);
                Assert.Equal(WorkflowTaskStatus.Approved, result.Status);
                Assert.Equal(result, await signature.ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, request, $"fail-{order}", null, null, default));
                Assert.False(await signature.RetryFinalizationAsync(data.Submitted.LetterId, default));
            }
        }
        Assert.Equal(LetterStatus.ProcessingFailed, (await db.LetterRequests.AsNoTracking().SingleAsync(x => x.Id == data.Submitted.LetterId)).Status);
        Assert.Equal(5, await db.SignatureEvidences.CountAsync(x => x.RevisionId == revision.Id));
        Assert.False(await db.Documents.AnyAsync(x => x.RevisionId == revision.Id && x.Kind == DocumentKind.Final));
        var controller = FinalizationController(services, data.Owner);
        var failedLetter = await db.LetterRequests.SingleAsync(x => x.Id == data.Submitted.LetterId);
        var retryRequest = new SignIt.Modules.Signatures.Controllers.RetryFinalizationRequest(revision.Id, revision.ContentHash, failedLetter.RowVersion);
        var queued = await controller.Retry(data.Submitted.LetterId, retryRequest, "pdf-retry", default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(queued.Result);
        var replay = await controller.Retry(data.Submitted.LetterId, retryRequest, "pdf-retry", default);
        Assert.IsType<Microsoft.AspNetCore.Mvc.AcceptedResult>(replay.Result);
        Assert.True(await IsolatedSignatureWorkflow(services).RetryFinalizationAsync(data.Submitted.LetterId, default));
        Assert.Equal(5, await db.SignatureEvidences.CountAsync(x => x.RevisionId == revision.Id));
        Assert.Equal(1, await db.Documents.CountAsync(x => x.RevisionId == revision.Id && x.Kind == DocumentKind.Final));
    }

    private static SignIt.Modules.Signatures.Controllers.LetterFinalizationController FinalizationController(IServiceProvider services, Guid actor)
    {
        var controller = new SignIt.Modules.Signatures.Controllers.LetterFinalizationController(
            services.GetRequiredService<AppDbContext>(), services.GetRequiredService<IStorageService>(),
            services.GetRequiredService<IQrCodeGenerator>(), services.GetRequiredService<TimeProvider>());
        controller.ControllerContext = new Microsoft.AspNetCore.Mvc.ControllerContext
        {
            HttpContext = new Microsoft.AspNetCore.Http.DefaultHttpContext
            {
                User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity(
                    [new System.Security.Claims.Claim("sub", actor.ToString())], "test"))
            }
        };
        return controller;
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task BackgroundWorker_PublishesCommittedEvidenceWithoutAnotherSigningRequest(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var db = services.GetRequiredService<AppDbContext>();
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == data.Submitted.RevisionId);
        for (var order = 1; order <= 5; order++)
        {
            var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == revision.Id && x.Order == order);
            await services.GetRequiredService<ISignatureWorkflowService>().ExecuteTaskActionAsync(task.Id,
                task.AssignedUserId, task.ActionType, SignRequest(task, revision), $"worker-{order}", null, null, default);
        }
        using var worker = new SignatureFinalizationWorker(fixture.Provider.GetRequiredService<IServiceScopeFactory>(),
            services.GetRequiredService<TimeProvider>(), NullLogger<SignatureFinalizationWorker>.Instance);
        await worker.StartAsync(default);
        try
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                var state = await db.LetterRequests.AsNoTracking().Where(x => x.Id == data.Submitted.LetterId)
                    .Select(x => x.Status).SingleAsync();
                if (state == LetterStatus.Completed) break;
                await Task.Delay(200);
            }
        }
        finally { await worker.StopAsync(default); }
        Assert.Equal(LetterStatus.Completed, (await db.LetterRequests.AsNoTracking()
            .SingleAsync(x => x.Id == data.Submitted.LetterId)).Status);
        Assert.Equal(5, await db.SignatureEvidences.CountAsync(x => x.RevisionId == revision.Id));
        Assert.Equal(1, await db.Documents.CountAsync(x => x.RevisionId == revision.Id && x.Kind == DocumentKind.Final));
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task RealFinalization_ConcurrentRetry_PrivateDownload_AndHashIntegrity(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services, type);
        var db = services.GetRequiredService<AppDbContext>();
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == data.Submitted.RevisionId);
        var signature = services.GetRequiredService<ISignatureWorkflowService>();
        for (var order = 1; order <= 5; order++)
        {
            var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == revision.Id && x.Order == order);
            var result = await signature.ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType,
                SignRequest(task, revision), $"real-{order}", null, null, default);
            Assert.False(result.IsWorkflowCompleted);
        }
        var controller = FinalizationController(services, data.Owner);
        var notReady = await Assert.ThrowsAsync<SignItDomainException>(() => controller.Download(data.Submitted.LetterId, default));
        Assert.Equal("final_document_not_ready", notReady.Code);
        async Task<bool> FinalizeAsync()
        {
            using var retryScope = fixture.Provider.CreateScope();
            return await retryScope.ServiceProvider.GetRequiredService<ISignatureWorkflowService>()
                .RetryFinalizationAsync(data.Submitted.LetterId, default);
        }
        var outcomes = await Task.WhenAll(FinalizeAsync(), FinalizeAsync());
        Assert.Equal(1, outcomes.Count(x => x));
        db.ChangeTracker.Clear();
        Assert.IsType<Microsoft.AspNetCore.Mvc.FileContentResult>(await controller.Download(data.Submitted.LetterId, default));
        var denied = await Assert.ThrowsAsync<SignItDomainException>(() => FinalizationController(services, data.Committee)
            .Download(data.Submitted.LetterId, default));
        Assert.Equal(DomainErrorKind.NotFound, denied.Kind);
        var document = await db.Documents.SingleAsync(x => x.RevisionId == revision.Id && x.Kind == DocumentKind.Final);
        var storage = services.GetRequiredService<IStorageService>();
        var bytes = await storage.ReadBytesAsync(document.StorageKey, default);
        await storage.SaveAsync(document.StorageKey, "%PDF-corrupt"u8.ToArray(), "application/pdf", default);
        var corrupt = await Assert.ThrowsAsync<SignItDomainException>(() => controller.Download(data.Submitted.LetterId, default));
        Assert.Equal("final_document_invalid", corrupt.Code);
        await storage.SaveAsync(document.StorageKey, bytes!, "application/pdf", default);
        Assert.Equal(5, await db.SignatureEvidences.CountAsync(x => x.RevisionId == revision.Id));
    }

    [PostgresPreviewTheory]
    [InlineData("proposal")]
    public async Task ConcurrentSignAndReject_OnlyOneDecisionCommits(string type)
    {
        using var scope = fixture.Provider.CreateScope();
        var data = await SubmitAsync(scope.ServiceProvider, type);
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 1);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId);
        var signRequest = SignRequest(task, revision);
        var rejectRequest = Mutation(task, revision);
        async Task<object> Decide(bool sign)
        {
            using var concurrent = fixture.Provider.CreateScope();
            try
            {
                if (sign) return await concurrent.ServiceProvider.GetRequiredService<ISignatureWorkflowService>()
                    .ExecuteTaskActionAsync(task.Id, task.AssignedUserId, task.ActionType, signRequest, "race-sign", null, null, default);
                return await concurrent.ServiceProvider.GetRequiredService<WorkflowService>().MutateAsync(task.AssignedUserId, task.Id, "reject", rejectRequest, "race-reject", default);
            }
            catch (SignItDomainException exception) { return exception; }
        }
        var results = await Task.WhenAll(Decide(true), Decide(false));
        Assert.Single(results, x => x is SignItDomainException);
        db.ChangeTracker.Clear();
        var state = await db.LetterRequests.SingleAsync(x => x.Id == data.Submitted.LetterId);
        var evidenceCount = await db.SignatureEvidences.CountAsync(x => x.RevisionId == revision.Id);
        Assert.Equal(state.Status == LetterStatus.Rejected ? 0 : 1, evidenceCount);
        Assert.Equal(state.Status == LetterStatus.Rejected ? 0 : 1,
            await db.WorkflowTasks.CountAsync(x => x.RevisionId == revision.Id && x.Status == WorkflowTaskStatus.Active));
    }

    [PostgresPreviewTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task UnscopedExpiredOrWrongAssignment_CannotReadOrAct(bool expired, bool wrongAssignment)
    {
        using var scope = fixture.Provider.CreateScope();
        var services = scope.ServiceProvider;
        var data = await SubmitAsync(services);
        var db = services.GetRequiredService<AppDbContext>();
        var task = await db.WorkflowTasks.SingleAsync(x => x.RevisionId == data.Submitted.RevisionId && x.Order == 1);
        var revision = await db.LetterRevisions.SingleAsync(x => x.Id == task.RevisionId);
        var deputy = User.Provision(Guid.NewGuid(), "Unauthorized QA", $"unauth-{Guid.NewGuid():N}@test.example", null, UserCategory.StudentGeneral, "test-only", DateTimeOffset.UtcNow, true);
        db.Users.Add(deputy);
        db.Assignments.Add(UserAssignment.Provision(Guid.NewGuid(), deputy.Id, "Ketupel", "Ketua Pelaksana", wrongAssignment ? "bem" : "ukki",
            UserCapability.Signer, DateTimeOffset.UtcNow.AddDays(-2), null));
        var mandate = Delegation.Create(Guid.NewGuid(), task.AssignedUserId, deputy.Id, expired || wrongAssignment ? WorkflowTaskAccess.DelegationScope(task.Id) : "ukki",
            DateTimeOffset.UtcNow.AddDays(-1), expired ? DateTimeOffset.UtcNow.AddHours(-1) : DateTimeOffset.UtcNow.AddDays(1), "QA invalid mandate");
        db.Delegations.Add(mandate);
        await db.SaveChangesAsync();
        var queries = services.GetRequiredService<WorkflowQueryService>();
        Assert.Equal(0, (await queries.QueueAsync(deputy.Id, 1, 100, default)).Total);
        Assert.Equal(DomainErrorKind.NotFound, (await Assert.ThrowsAsync<SignItDomainException>(() => queries.DownloadAsync(deputy.Id, task.Id, default))).Kind);
        Assert.Equal(DomainErrorKind.Forbidden, (await Assert.ThrowsAsync<SignItDomainException>(() => services.GetRequiredService<ISignatureWorkflowService>()
            .ExecuteTaskActionAsync(task.Id, deputy.Id, task.ActionType, SignRequest(task, revision), "unauthorized", null, null, default))).Kind);
    }
}
