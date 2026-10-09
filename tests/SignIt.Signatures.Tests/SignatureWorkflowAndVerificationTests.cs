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

namespace SignIt.Signatures.Tests;

public sealed class SignatureWorkflowAndVerificationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    private static (AppDbContext db, IStorageService storage, SignatureWorkflowService workflow, PublicVerificationService verify, UserSignatureQrService qrService) CreateTestContext()
    {
        var dbName = "SignIt_Test_" + Guid.NewGuid().ToString("N");
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(dbName)
            .Options;
        var db = new AppDbContext(options);

        var tempDir = Path.Combine(Path.GetTempPath(), "signit_wf_" + Guid.NewGuid().ToString("N"));
        var storageOptions = Options.Create(new StorageOptions { RootDirectory = tempDir });
        var env = new TestHostEnvironment { ContentRootPath = tempDir };
        var storage = new LocalStorageService(storageOptions, env);

        var qrGen = new QRCoderGenerator();
        var clock = new FixedTimeProvider(Now);
        var qrLogger = NullLogger<UserSignatureQrService>.Instance;
        var qrService = new UserSignatureQrService(db, qrGen, storage, clock, qrLogger);

        var pdfOverlay = new PdfSharpOverlayService();
        var wfLogger = NullLogger<SignatureWorkflowService>.Instance;
        var workflow = new SignatureWorkflowService(db, qrService, qrGen, pdfOverlay, storage, clock, wfLogger);
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
        var letter = LetterRequest.Create(letterId, "SURAT/2026/001", "proposal", "Proposal Kegiatan LKMM", user1.Id, null, Now);
        var revId = Guid.NewGuid();
        var contentHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
        var revision = LetterRevision.Create(revId, letterId, 1, "v1", "{\"kegiatan\":\"LKMM\"}", contentHash, null, Now);
        letter.SetCurrentRevision(revId);

        var part1 = LetterParticipant.Create(Guid.NewGuid(), revId, LetterRole.Applicant, "ketupel",
            user1.Id, "Ahmad Mahasiswa", "Ketua Pelaksana", true, 0, 50, 400, 140, 100);
        var part2 = LetterParticipant.Create(Guid.NewGuid(), revId, LetterRole.ApprovingSignatory, "pembina",
            user2.Id, "Dr. Budi Pembina", "Pembina Organisasi", true, 0, 250, 400, 140, 100);

        var task1 = WorkflowTask.Create(Guid.NewGuid(), revId, part1.Id, 1, WorkflowActionType.Sign, user1.Id, "kemahasiswaan",
            WorkflowTaskStatus.Active, activatedAt: Now);
        var task2 = WorkflowTask.Create(Guid.NewGuid(), revId, part2.Id, 2, WorkflowActionType.ApproveAndSign, user2.Id, "kemahasiswaan",
            WorkflowTaskStatus.Pending);

        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(revision);
        db.LetterParticipants.AddRange(part1, part2);
        db.WorkflowTasks.AddRange(task1, task2);
        await db.SaveChangesAsync();

        // 3. User 1 signs task 1
        var signReq1 = new SignTaskRequest(revId, contentHash, "Saya setujui selaku ketupel");
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
        var signReq2 = new SignTaskRequest(revId, contentHash, "Disetujui untuk dilaksanakan");
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

        var letter = LetterRequest.Create(Guid.NewGuid(), "001", "undangan", "Undangan", user.Id, null, Now);
        var rev = LetterRevision.Create(Guid.NewGuid(), letter.Id, 1, null, "{}", "actualhash", null, Now);
        letter.SetCurrentRevision(rev.Id);

        var task = WorkflowTask.Create(Guid.NewGuid(), rev.Id, null, 1, WorkflowActionType.Sign, user.Id, null,
            WorkflowTaskStatus.Active, Now);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(rev);
        db.WorkflowTasks.Add(task);
        await db.SaveChangesAsync();

        var request = new SignTaskRequest(rev.Id, "wronghash");
        var ex = await Assert.ThrowsAsync<SignItDomainException>(() =>
            workflow.ExecuteTaskActionAsync(task.Id, user.Id, WorkflowActionType.Sign, request, null, null, null, CancellationToken.None));

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

        var letter = LetterRequest.Create(Guid.NewGuid(), "001", "undangan", "Undangan", assigned.Id, null, Now);
        var rev = LetterRevision.Create(Guid.NewGuid(), letter.Id, 1, null, "{}", "hash", null, Now);
        letter.SetCurrentRevision(rev.Id);

        var task = WorkflowTask.Create(Guid.NewGuid(), rev.Id, null, 1, WorkflowActionType.Sign, assigned.Id, null,
            WorkflowTaskStatus.Active, Now);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(rev);
        db.WorkflowTasks.Add(task);
        await db.SaveChangesAsync();

        // Attacker attempts to sign
        var request = new SignTaskRequest(rev.Id, "hash");
        var ex = await Assert.ThrowsAsync<SignItDomainException>(() =>
            workflow.ExecuteTaskActionAsync(task.Id, attacker.Id, WorkflowActionType.Sign, request, null, null, null, CancellationToken.None));
        Assert.Equal(DomainErrorKind.Forbidden, ex.Kind);

        // Add valid delegation to delegateUser
        var delegation = Delegation.Create(Guid.NewGuid(), assigned.Id, delegateUser.Id, "scope", Now.AddDays(-1), Now.AddDays(1), "Dinas Luar");
        db.Delegations.Add(delegation);
        await db.SaveChangesAsync();

        // Delegate signs
        var result = await workflow.ExecuteTaskActionAsync(
            task.Id, delegateUser.Id, WorkflowActionType.Sign, request, null, null, null, CancellationToken.None);
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

        var letter = LetterRequest.Create(Guid.NewGuid(), "001", "undangan", "Undangan", user.Id, null, Now);
        var rev = LetterRevision.Create(Guid.NewGuid(), letter.Id, 1, null, "{}", "hash", null, Now);
        letter.SetCurrentRevision(rev.Id);

        var task = WorkflowTask.Create(Guid.NewGuid(), rev.Id, null, 1, WorkflowActionType.ApproveAndSign, user.Id, null,
            WorkflowTaskStatus.Active, Now);
        db.LetterRequests.Add(letter);
        db.LetterRevisions.Add(rev);
        db.WorkflowTasks.Add(task);
        await db.SaveChangesAsync();

        await workflow.RejectTaskAsync(task.Id, user.Id, "Anggaran tidak rasional", null, null, CancellationToken.None);

        var updatedTask = await db.WorkflowTasks.FindAsync(task.Id);
        var updatedLetter = await db.LetterRequests.FindAsync(letter.Id);
        Assert.Equal(WorkflowTaskStatus.Rejected, updatedTask!.Status);
        Assert.Equal("Anggaran tidak rasional", updatedTask.Comment);
        Assert.Equal(LetterStatus.Rejected, updatedLetter!.Status);
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
