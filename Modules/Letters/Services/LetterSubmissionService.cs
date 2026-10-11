using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Email.Services;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Routing.Services;
using SignIt.Modules.Templates.Services;
using SignIt.Modules.Workflow.Models;
using SignIt.Modules.Workflow.Services;

namespace SignIt.Modules.Letters.Services;

public sealed record SignatureSlot(string PositionCode, int PageIndex, double X, double Y, double Width, double Height);
public sealed record SubmitLetterRequest(Guid ExpectedVersion, Guid ExpectedRevisionId, string ExpectedContentHash,
    Guid OrganizationId, Guid CommitteeChairId, Guid OrganizationChairId, Guid? ResourceId,
    Guid ReviewDocumentId, string ExpectedReviewHash, SignatureSlot[] Slots);
public sealed record SubmissionDto(Guid LetterId, Guid RevisionId, string Number, LetterStatus Status);

public sealed class LetterSubmissionService(
    AppDbContext db,
    RoutingService routing,
    TemplateCatalog templates,
    IStorageService storage,
    TimeProvider clock,
    LetterPreviewService previews,
    WorkflowEmailService emails,
    WorkflowOptions workflow,
    ISignatureSlotValidator? slotValidator = null,
    ISubmissionWorkflowInitializer? workflowInitializer = null,
    ISubmissionValidator? validator = null)
{
    private readonly ISignatureSlotValidator _slotValidator = slotValidator ?? new PdfSignatureSlotValidator();
    private readonly ISubmissionWorkflowInitializer _workflowInitializer = workflowInitializer ?? new SubmissionWorkflowInitializer();
    private readonly ISubmissionValidator _validator = validator ?? new SubmissionValidator();

    public async Task<SubmissionDto> SubmitAsync(Guid actor, Guid id, SubmitLetterRequest request, string key, CancellationToken ct)
    {
        _validator.ValidateIdempotencyKey(key);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var letter = await db.LetterRequests.FromSqlInterpolated($"SELECT * FROM letter_requests WHERE \"Id\"={id} AND \"SubmittedByUserId\"={actor} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "letter_not_found", "Surat tidak ditemukan.");
        await db.Entry(letter).ReloadAsync(ct);

        var fingerprint = Hash(JsonSerializer.Serialize(request));
        var correlation = Hash($"{actor}:{id}:{key}");
        var previous = await db.AuditLogs.SingleOrDefaultAsync(x => x.EntityId == id && x.Action == "letter.submitted" && x.CorrelationId == correlation, ct);
        if (previous != null)
        {
            if (previous.Details != fingerprint) throw Conflict("idempotency_payload_conflict", "Key sudah digunakan untuk data berbeda.");
            await transaction.CommitAsync(ct);
            return new(id, previous.RevisionId!.Value, letter.Number, letter.Status);
        }

        var hasExistingTasks = await db.WorkflowTasks.AnyAsync(x => x.RevisionId == request.ExpectedRevisionId, ct);
        _validator.ValidateDraftState(letter, request, hasExistingTasks);

        var draft = await db.LetterRevisions.SingleAsync(x => x.Id == letter.CurrentRevisionId, ct);
        _validator.ValidateContent(draft, request.ExpectedContentHash);

        var template = (await templates.GetAllAsync(ct)).Single(x => x.TypeId == letter.TypeId);
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(draft.DataJson) ?? [];
        _validator.ValidateTemplate(draft, template, fields);

        var stages = await routing.ResolveAsync(actor, new(letter.TypeId, request.OrganizationId, request.CommitteeChairId, request.OrganizationChairId, request.ResourceId), ct);
        var review = await db.Documents.SingleOrDefaultAsync(x => x.Id == request.ReviewDocumentId && x.RevisionId == draft.Id
            && x.Kind == DocumentKind.Review && x.ProcessingState == "Ready" && x.MimeType == "application/pdf", ct)
            ?? throw Error("review_required", "Preview PDF untuk revisi ini belum siap.");
        var job = await db.LetterPreviewJobs.AsNoTracking().SingleOrDefaultAsync(x => x.DocumentId == review.Id
            && x.RevisionId == draft.Id && x.RequestId == id && x.State == "Ready", ct)
            ?? throw Error("generated_review_required", "Gunakan preview yang dihasilkan renderer template.");

        var renderedInput = await previews.BuildInputAsync(actor, letter, draft, template, request.OrganizationId,
            request.CommitteeChairId, request.OrganizationChairId, request.ResourceId, ct);
        var serverSlots = JsonSerializer.Deserialize<SignatureSlot[]>(job.SlotsJson!)!;
        _validator.ValidatePreviewJob(job, Hash(JsonSerializer.Serialize(renderedInput)), request.Slots, serverSlots);

        var bytes = await storage.ReadBytesAsync(review.StorageKey, ct);
        _validator.ValidateReviewDocument(review, request.ExpectedReviewHash, bytes);
        _slotValidator.ValidateSlots(bytes!, request.Slots!, stages);

        var now = clock.GetUtcNow();
        var snapshot = JsonSerializer.Serialize(new { Fields = renderedInput.Fields, request.OrganizationId, request.ResourceId,
            Stages = stages, Slots = serverSlots, ReviewHash = review.Sha256, renderedInput.TemplateAssetHash,
            renderedInput.Layout.RendererVersion, PreviewJobId = job.Id, PolicyVersion = "2026-10-09" });
        var revision = LetterRevision.Create(Guid.NewGuid(), id, draft.RevisionNo + 1, draft.TemplateVersionId, snapshot, Hash(snapshot), review.Id, now);
        db.LetterRevisions.Add(revision);

        var initialized = _workflowInitializer.Initialize(revision.Id, stages, request.Slots!, now, workflow.SlaDays);
        db.LetterParticipants.AddRange(initialized.Participants);
        db.WorkflowTasks.AddRange(initialized.Tasks);

        letter.SetCurrentRevision(revision.Id);
        var submitterUser = await db.Users.AsNoTracking().SingleAsync(x => x.Id == actor, ct);
        await emails.EnqueueSubmittedAsync(letter, submitterUser, ct);
        if (initialized.FirstTask != null)
        {
            var firstAssignee = await db.Users.AsNoTracking().SingleAsync(x => x.Id == initialized.FirstTask.AssignedUserId, ct);
            await emails.EnqueueTaskActiveAsync(initialized.FirstTask, letter, firstAssignee, ct);
        }

        letter.Submit(request.OrganizationId, letter.Status == LetterStatus.NeedsRevision ? letter.Number : $"SGN-{id:N}", now);
        db.AuditLogs.Add(AuditLog.Record(actor, "letter.submitted", "LetterRequest", id, revision.Id, now, correlation, fingerprint));
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(id, revision.Id, letter.Number, letter.Status);
    }

    private static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    private static SignItDomainException Error(string code, string message) => new(DomainErrorKind.Validation, code, message);
    private static SignItDomainException Conflict(string code, string message) => new(DomainErrorKind.Conflict, code, message);
}
