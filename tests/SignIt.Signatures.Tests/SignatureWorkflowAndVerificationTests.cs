using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Storage;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Signatures.Services;
using SignIt.Modules.Workflow.Models;
using Xunit;
using SignIt.Modules.Workflow.Services;
using SignIt.Modules.Routing.Models;
using PdfSharp.Pdf;

namespace SignIt.Signatures.Tests;

public sealed class SignatureWorkflowAndVerificationTests
{
    private static readonly Guid OrganizationId = Guid.NewGuid();
    private static readonly Guid RequesterId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private static (AppDbContext db, IStorageService storage, SignatureWorkflowService workflow, PublicVerificationService verify, UserSignatureQrService qrService) CreateTestContext()
    {
        var dbName = "SignIt_Test_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new AppDbContext(options);
        var organization = new Organization();
        db.Organizations.Add(organization);
        db.Entry(organization).Property(x => x.Id).CurrentValue = OrganizationId;
        db.Entry(organization).Property(x => x.Scope).CurrentValue = "test";
        db.Entry(organization).Property(x => x.Name).CurrentValue = "Test Organization";
        db.Entry(organization).Property(x => x.Kind).CurrentValue = "Organisasi";
        db.Entry(organization).Property(x => x.IsActive).CurrentValue = true;
        db.Users.Add(User.Provision(RequesterId, "Requester", "requester@test.example", null, UserCategory.StudentGeneral, "test-hash", Now, true));
        db.SaveChanges();

        var tempDir = Path.Combine(Path.GetTempPath(), "signit_wf_" + Guid.NewGuid().ToString("N"));
        var storageOptions = Options.Create(new StorageOptions { RootDirectory = tempDir });
        var env = new TestHostEnvironment { ContentRootPath = tempDir };
        var storage = new LocalStorageService(storageOptions, env);

        var qrGen = new QRCoderGenerator();
        var clock = new FixedTimeProvider(Now);
        var qrLogger = NullLogger<UserSignatureQrService>.Instance;
        var qrService = new UserSignatureQrService(db, qrGen, storage, clock, qrLogger);

        var pdfOverlay = new WorkflowTestPdfAdapter();
        var wfLogger = NullLogger<SignatureWorkflowService>.Instance;
        var workflow = new SignatureWorkflowService(db, qrService, qrGen, pdfOverlay, storage, clock, wfLogger, new WorkflowTaskAccess(db, clock));
        var verify = new PublicVerificationService(db);

        return (db, storage, workflow, verify, qrService);
    }

    [Fact]
    public async Task CompleteWorkflow_TwoSigners_SequentialProgression_AndPublicVerification()
    {
        var (db, storage, workflow, verify, qrService) = CreateTestContext();

        // 1. Seed two users
        var user1 = User.Provision(Guid.NewGuid(), "Ahmad Mahasiswa", "ahmad@signit.test", "123456",
            UserCategory.StudentGeneral, "hash1", Now, true);
        var user2 = User.Provision(Guid.NewGuid(), "Dr. Budi Pembina", "budi@signit.test", "654321",
            UserCategory.Management, "hash2", Now, true);
        db.Users.AddRange(user1, user2);

        // 2. Seed Letter Request & Revision
        var letterId = Guid.NewGuid();
        var letter = LetterRequest.Create(letterId, "SURAT/2026/001", "proposal", "Proposal Kegiatan LKMM", user1.Id, OrganizationId, Now);
        var revId = Guid.NewGuid();
        var contentHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var reviewId = Guid.NewGuid();
        var revision = LetterRevision.Create(revId, letterId, 1, "v1", "{\"kegiatan\":\"LKMM\"}", contentHash, reviewId, Now);
        using (var pdf = new PdfDocument())
        {
            pdf.AddPage();
            using var stream = new MemoryStream();
            pdf.Save(stream, false);
            var bytes = stream.ToArray();
            var key = $"test/{reviewId}.pdf";
            await storage.SaveAsync(key, bytes, "application/pdf", default);
            db.Documents.Add(Document.Create(reviewId, revId, DocumentKind.Review, key, "application/pdf", bytes.Length,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)), Now));
        }
        letter.SetCurrentRevision(revId);

        var part1 = LetterParticipant.Create(Guid.NewGuid(), revId, LetterRole.Applicant, "ketupel",
            user1.Id, "Ahmad Mahasiswa", "Ketua Pelaksana", true, 0, 50, 400, 140, 100);
        var part2 = LetterParticipant.Create(Guid.NewGuid(), revId, LetterRole.ApprovingSignatory, "pembina",
            user2.Id, "Dr. Budi Pembina", "Pembina Organisasi", true, 0, 250, 400, 140, 100);

        var task1 = WorkflowTask.Create(Guid.NewGuid(), revId, part1.Id, 1, WorkflowActionType.Sign, user1.Id, "Ketupel",
            WorkflowTaskStatus.Active, activatedAt: Now);
        var task2 = WorkflowTask.Create(Guid.NewGuid(), revId, part2.Id, 2, WorkflowActionType.ApproveAndSign, user2.Id, "Pembina",
            WorkflowTaskStatus.Pending);

        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(revision);
        db.LetterParticipants.AddRange(part1, part2);
        db.WorkflowTasks.AddRange(task1, task2);
        SeedAssignments(db);
        await db.SaveChangesAsync();

        // 3. User 1 signs task 1
        var signReq1 = new SignTaskRequest(revId, contentHash, "Saya setujui selaku ketupel", task1.RowVersion);
        var result1 = await workflow.ExecuteTaskActionAsync(
            task1.Id, user1.Id, WorkflowActionType.Sign, signReq1,
            idempotencyKey: "key-1", ipAddress: "10.0.0.1", userAgent: "SignItApp", CancellationToken.None);

        Assert.Equal(WorkflowTaskStatus.Signed, result1.Status);
        Assert.False(result1.IsWorkflowCompleted);
        Assert.Null(result1.VerificationCode);

        // Verify task 1 is Signed, task 2 was activated
        var updatedTask1 = await db.WorkflowTasks.FindAsync(task1.Id);
        var updatedTask2 = await db.WorkflowTasks.FindAsync(task2.Id);
        Assert.Equal(WorkflowTaskStatus.Signed, updatedTask1!.Status);
        Assert.Equal(WorkflowTaskStatus.Active, updatedTask2!.Status);

        // Verify evidence for task 1
        var evidence1 = await db.SignatureEvidences.FirstOrDefaultAsync(x => x.TaskId == task1.Id);
        Assert.NotNull(evidence1);
        Assert.Equal(user1.Id, evidence1.ActorId);
        Assert.Equal("Applicant", evidence1.Role);
        Assert.Equal(contentHash, evidence1.ContentHash);

        // 4. User 2 approves task 2
        var signReq2 = new SignTaskRequest(revId, contentHash, "Disetujui untuk dilaksanakan", task2.RowVersion);
        var result2 = await workflow.ExecuteTaskActionAsync(
            task2.Id, user2.Id, WorkflowActionType.ApproveAndSign, signReq2,
            idempotencyKey: "key-2", ipAddress: "10.0.0.2", userAgent: "SignItApp", CancellationToken.None);

        Assert.Equal(WorkflowTaskStatus.Approved, result2.Status);
        Assert.True(result2.IsWorkflowCompleted);
        Assert.NotNull(result2.VerificationCode);

        // 5. Verify Letter is now Completed
        var completedLetter = await db.LetterRequests.FindAsync(letterId);
        Assert.Equal(LetterStatus.Completed, completedLetter!.Status);
        Assert.NotNull(completedLetter.CompletedAt);

        // 6. Public Verification by Code
        var publicInfo = await verify.VerifyByCodeAsync(result2.VerificationCode!, CancellationToken.None);
        Assert.Equal(result2.VerificationCode, publicInfo.VerificationCode);
        Assert.Equal("Valid", publicInfo.Status);
        Assert.Equal("SURAT/2026/001", publicInfo.LetterNumber);
        Assert.Equal(2, publicInfo.Signers.Count);
        Assert.Contains(publicInfo.Signers, s => s.Name == "Ahmad Mahasiswa" && s.Role == "Applicant");
        Assert.Contains(publicInfo.Signers, s => s.Name == "Dr. Budi Pembina" && s.Role == "ApprovingSignatory");

        // 7. Verify by Uploading final document
        var finalDoc = await db.Documents.FirstOrDefaultAsync(x => x.RevisionId == revId && x.Kind == DocumentKind.Final);
        Assert.NotNull(finalDoc);
        var finalBytes = await storage.ReadBytesAsync(finalDoc.StorageKey, CancellationToken.None);
        Assert.NotNull(finalBytes);

        using var uploadStream = new MemoryStream(finalBytes);
        var uploadResult = await verify.VerifyByUploadAsync(uploadStream, finalBytes.Length, CancellationToken.None);
        Assert.True(uploadResult.Matches);
        Assert.Equal(result2.VerificationCode, uploadResult.VerificationCode);

        // 8. Verify Tampered file upload fails
        var tamperedBytes = (byte[])finalBytes.Clone();
        tamperedBytes[tamperedBytes.Length - 10] ^= 0xFF; // flip byte
        using var tamperedStream = new MemoryStream(tamperedBytes);
        var tamperedResult = await verify.VerifyByUploadAsync(tamperedStream, tamperedBytes.Length, CancellationToken.None);
        Assert.False(tamperedResult.Matches);
        Assert.Null(tamperedResult.VerificationCode);
    }

    [Fact]
    public async Task SigningTask_RejectsMismatchedRevisionHash()
    {
        var (db, _, workflow, _, _) = CreateTestContext();
        var user = User.Provision(Guid.NewGuid(), "User", "u@test.com", null, UserCategory.StudentGeneral, "hash", Now, true);
        db.Users.Add(user);

        var letter = LetterRequest.Create(Guid.NewGuid(), "001", "undangan", "Undangan", RequesterId, OrganizationId, Now);
        var rev = LetterRevision.Create(Guid.NewGuid(), letter.Id, 1, null, "{}", "actualhash", null, Now);
        letter.SetCurrentRevision(rev.Id);

        var task = WorkflowTask.Create(Guid.NewGuid(), rev.Id, null, 1, WorkflowActionType.Sign, user.Id, "Ketupel",
            WorkflowTaskStatus.Active, Now);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(rev);
        db.WorkflowTasks.Add(task);
        SeedAssignments(db);
        await db.SaveChangesAsync();

        var request = new SignTaskRequest(rev.Id, "wronghash", ExpectedTaskVersion: task.RowVersion);
        var ex = await Assert.ThrowsAsync<SignItDomainException>(() =>
            workflow.ExecuteTaskActionAsync(task.Id, user.Id, WorkflowActionType.Sign, request, "test-action", null, null, CancellationToken.None));

        Assert.Equal(DomainErrorKind.Conflict, ex.Kind);
        Assert.Equal("revision_hash_mismatch", ex.Code);
    }

    [Fact]
    public async Task SigningTask_RejectsUnauthorizedActor_UnlessDelegated()
    {
        var (db, _, workflow, _, _) = CreateTestContext();
        var assigned = User.Provision(Guid.NewGuid(), "Pejabat", "p@test.com", null, UserCategory.Management, "h1", Now, true);
        var attacker = User.Provision(Guid.NewGuid(), "Lain", "l@test.com", null, UserCategory.StudentGeneral, "h2", Now, true);
        var delegateUser = User.Provision(Guid.NewGuid(), "Wakil", "w@test.com", null, UserCategory.Management, "h3", Now, true);
        db.Users.AddRange(assigned, attacker, delegateUser);

        var letter = LetterRequest.Create(Guid.NewGuid(), "001", "undangan", "Undangan", RequesterId, OrganizationId, Now);
        var rev = LetterRevision.Create(Guid.NewGuid(), letter.Id, 1, null, "{}", "hash", null, Now);
        letter.SetCurrentRevision(rev.Id);

        var task = WorkflowTask.Create(Guid.NewGuid(), rev.Id, null, 1, WorkflowActionType.Sign, assigned.Id, "Ketupel",
            WorkflowTaskStatus.Active, Now);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(rev);
        db.WorkflowTasks.Add(task);
        SeedAssignments(db);
        await db.SaveChangesAsync();

        // Attacker attempts to sign
        var request = new SignTaskRequest(rev.Id, "hash", ExpectedTaskVersion: task.RowVersion);
        var ex = await Assert.ThrowsAsync<SignItDomainException>(() =>
            workflow.ExecuteTaskActionAsync(task.Id, attacker.Id, WorkflowActionType.Sign, request, "delegated-sign", null, null, CancellationToken.None));
        Assert.Equal(DomainErrorKind.Forbidden, ex.Kind);

        // Add valid delegation to delegateUser
        var delegation = Delegation.Create(Guid.NewGuid(), assigned.Id, delegateUser.Id, WorkflowTaskAccess.DelegationScope(task.Id), Now.AddDays(-1), Now.AddDays(1), "Dinas Luar");
        db.Delegations.Add(delegation);
        db.Assignments.Add(UserAssignment.Provision(Guid.NewGuid(), delegateUser.Id, "Ketupel", "Ketua Pelaksana", "test", UserCapability.Signer, Now.AddDays(-1), null));
        SeedAssignments(db);
        await db.SaveChangesAsync();

        // Delegate signs
        var result = await workflow.ExecuteTaskActionAsync(
            task.Id, delegateUser.Id, WorkflowActionType.Sign, request, "delegate-sign", null, null, CancellationToken.None);
        Assert.Equal(WorkflowTaskStatus.Signed, result.Status);

        var evidence = await db.SignatureEvidences.FirstOrDefaultAsync(x => x.TaskId == task.Id);
        Assert.NotNull(evidence);
        Assert.Equal(delegateUser.Id, evidence.ActorId);
        Assert.Equal(assigned.Id, evidence.DelegatedFromUserId);
        Assert.Equal("a.n. Pejabat", evidence.MandateDescription);
    }

    [Fact]
    public async Task RejectTask_UpdatesLetterAndTaskToRejected()
    {
        var (db, _, workflow, _, _) = CreateTestContext();
        var user = User.Provision(Guid.NewGuid(), "User", "u@test.com", null, UserCategory.Management, "h", Now, true);
        db.Users.Add(user);

        var letter = LetterRequest.Create(Guid.NewGuid(), "001", "undangan", "Undangan", RequesterId, OrganizationId, Now);
        var rev = LetterRevision.Create(Guid.NewGuid(), letter.Id, 1, null, "{}", "hash", null, Now);
        letter.SetCurrentRevision(rev.Id);

        var task = WorkflowTask.Create(Guid.NewGuid(), rev.Id, null, 1, WorkflowActionType.ApproveAndSign, user.Id, "Pembina",
            WorkflowTaskStatus.Active, Now);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(rev);
        db.WorkflowTasks.Add(task);
        SeedAssignments(db);
        await db.SaveChangesAsync();

        var decisions = new WorkflowService(db, new WorkflowTaskAccess(db, new FixedTimeProvider(Now)), new FixedTimeProvider(Now));
        await decisions.MutateAsync(user.Id, task.Id, "reject", new(rev.Id, rev.ContentHash, task.RowVersion, "Anggaran tidak rasional"), "reject-1", default);

        var updatedTask = await db.WorkflowTasks.FindAsync(task.Id);
        var updatedLetter = await db.LetterRequests.FindAsync(letter.Id);
        Assert.Equal(WorkflowTaskStatus.Rejected, updatedTask!.Status);
        Assert.Equal("Anggaran tidak rasional", updatedTask.Comment);
        Assert.Equal(LetterStatus.Rejected, updatedLetter!.Status);
    }

    private static void SeedAssignments(AppDbContext db)
    {
        foreach (var task in db.ChangeTracker.Entries<WorkflowTask>().Where(x => x.State == EntityState.Added).Select(x => x.Entity).ToList())
        {
            var capability = task.ActionType == WorkflowActionType.Sign ? UserCapability.Signer : UserCapability.Approver;
            if (!db.Assignments.Local.Any(x => x.UserId == task.AssignedUserId && x.PositionCode == task.DomainCode))
                db.Assignments.Add(UserAssignment.Provision(Guid.NewGuid(), task.AssignedUserId, task.DomainCode!, task.DomainCode!, "test", capability, Now.AddDays(-1), null));
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestHostEnvironment : Microsoft.Extensions.Hosting.IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "SignIt.Tests";
        public string ContentRootPath { get; set; } = Path.GetTempPath();
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }
}
