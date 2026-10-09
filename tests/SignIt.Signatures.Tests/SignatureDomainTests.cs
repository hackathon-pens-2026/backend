using SignIt.Modules.Letters.Models;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Workflow.Models;
using Xunit;

namespace SignIt.Signatures.Tests;

public sealed class SignatureDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 9, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void UserSignatureQr_Create_ValidInputs_InitializesCorrectly()
    {
        var id = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var opaque = "abcd1234efgh5678";
        var storageKey = $"signatures/users/{userId}/qr-v1.png";
        var hash = new string('a', 64);

        var qr = UserSignatureQr.Create(id, userId, opaque, storageKey, 1, hash, Now);

        Assert.Equal(id, qr.Id);
        Assert.Equal(userId, qr.OwnerUserId);
        Assert.Equal(opaque, qr.OpaqueCode);
        Assert.Equal(storageKey, qr.PrivateStorageKey);
        Assert.Equal(1, qr.Version);
        Assert.Equal(hash, qr.ImageSha256);
        Assert.Equal(SignatureQrStatus.Active, qr.Status);
        Assert.Equal(Now, qr.CreatedAt);
    }

    [Fact]
    public void UserSignatureQr_Create_InvalidInputs_ThrowsArgumentException()
    {
        var userId = Guid.NewGuid();
        var hash = new string('a', 64);

        Assert.Throws<ArgumentException>(() => UserSignatureQr.Create(Guid.Empty, userId, "code", "path", 1, hash, Now));
        Assert.Throws<ArgumentException>(() => UserSignatureQr.Create(Guid.NewGuid(), Guid.Empty, "code", "path", 1, hash, Now));
        Assert.Throws<ArgumentException>(() => UserSignatureQr.Create(Guid.NewGuid(), userId, "", "path", 1, hash, Now));
        Assert.Throws<ArgumentException>(() => UserSignatureQr.Create(Guid.NewGuid(), userId, "code", "", 1, hash, Now));
        Assert.Throws<ArgumentException>(() => UserSignatureQr.Create(Guid.NewGuid(), userId, "code", "path", 0, hash, Now));
        Assert.Throws<ArgumentException>(() => UserSignatureQr.Create(Guid.NewGuid(), userId, "code", "path", 1, "short", Now));
    }

    [Fact]
    public void UserSignatureQr_Rotate_UpdatesStatusToRotated()
    {
        var qr = UserSignatureQr.Create(Guid.NewGuid(), Guid.NewGuid(), "code", "path", 1, new string('a', 64), Now);
        qr.Rotate("new-path", 2, new string('b', 64));

        Assert.Equal(SignatureQrStatus.Rotated, qr.Status);
        Assert.Throws<InvalidOperationException>(() => qr.Rotate("path3", 3, new string('c', 64)));
    }

    [Fact]
    public void UserSignatureQr_Revoke_UpdatesStatusToRevoked()
    {
        var qr = UserSignatureQr.Create(Guid.NewGuid(), Guid.NewGuid(), "code", "path", 1, new string('a', 64), Now);
        qr.Revoke();

        Assert.Equal(SignatureQrStatus.Revoked, qr.Status);
        Assert.Throws<InvalidOperationException>(() => qr.Revoke());
    }

    [Fact]
    public void SignatureEvidence_Create_StoresCompleteAuditSnapshot()
    {
        var id = Guid.NewGuid();
        var taskId = Guid.NewGuid();
        var revId = Guid.NewGuid();
        var actorId = Guid.NewGuid();
        var qrId = Guid.NewGuid();
        var contentHash = new string('c', 64);
        var qrHash = new string('q', 64);

        var evidence = SignatureEvidence.Create(
            id, taskId, revId, actorId,
            "Ketua Pelaksana", "Ketua Himpunan",
            contentHash, qrId, 1, qrHash, Now,
            delegatedFromUserId: null,
            mandateDescription: null,
            ipAddress: "127.0.0.1",
            userAgent: "Mozilla/5.0");

        Assert.Equal(id, evidence.Id);
        Assert.Equal(taskId, evidence.TaskId);
        Assert.Equal(revId, evidence.RevisionId);
        Assert.Equal(actorId, evidence.ActorId);
        Assert.Equal("Ketua Pelaksana", evidence.Role);
        Assert.Equal("Ketua Himpunan", evidence.PositionSnapshot);
        Assert.Equal(contentHash, evidence.ContentHash);
        Assert.Equal(qrId, evidence.QrAssetId);
        Assert.Equal(1, evidence.QrVersion);
        Assert.Equal(qrHash, evidence.QrHash);
        Assert.Equal(Now, evidence.SignedAt);
        Assert.Null(evidence.DelegatedFromUserId);
        Assert.Equal("127.0.0.1", evidence.IpAddress);
    }

    [Fact]
    public void VerificationRecord_CreateAndRevoke_Lifecycle()
    {
        var id = Guid.NewGuid();
        var reqId = Guid.NewGuid();
        var hash = new string('f', 64);
        var record = VerificationRecord.Create(id, reqId, Guid.NewGuid(), "SIG-TEST-1234", hash, Now);

        Assert.Equal("SIG-TEST-1234", record.RandomCode);
        Assert.Equal(VerificationStatus.Valid, record.Status);
        Assert.Null(record.RevokedAt);

        var revokeTime = Now.AddDays(1);
        record.Revoke("Surat dibatalkan oleh BAAK", revokeTime);

        Assert.Equal(VerificationStatus.Revoked, record.Status);
        Assert.Equal(revokeTime, record.RevokedAt);
        Assert.Equal("Surat dibatalkan oleh BAAK", record.RevocationReason);
        Assert.Throws<InvalidOperationException>(() => record.Revoke("Ulang", revokeTime));
    }

    [Fact]
    public void WorkflowTask_Lifecycle_TransitionsCorrectly()
    {
        var task = WorkflowTask.Create(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1,
            WorkflowActionType.Sign, Guid.NewGuid(), "kemahasiswaan");

        Assert.Equal(WorkflowTaskStatus.Pending, task.Status);

        task.Activate(Now);
        Assert.Equal(WorkflowTaskStatus.Active, task.Status);
        Assert.Equal(Now, task.ActivatedAt);

        var actor = Guid.NewGuid();
        task.Complete(WorkflowTaskStatus.Signed, actor, Now.AddHours(1), "Disetujui");
        Assert.Equal(WorkflowTaskStatus.Signed, task.Status);
        Assert.Equal(actor, task.ActedByUserId);
        Assert.Equal("Disetujui", task.Comment);

        Assert.Throws<InvalidOperationException>(() => task.Complete(WorkflowTaskStatus.Signed, actor, Now));
    }

    [Fact]
    public void Delegation_Validation_OnlyValidWithinDateRange()
    {
        var from = Guid.NewGuid();
        var to = Guid.NewGuid();
        var delegation = Delegation.Create(Guid.NewGuid(), from, to, "scope", Now, Now.AddDays(3), "Cuti");

        Assert.True(delegation.IsValidFor(from, to, Now.AddDays(1)));
        Assert.False(delegation.IsValidFor(from, to, Now.AddDays(4)));
        Assert.False(delegation.IsValidFor(from, Guid.NewGuid(), Now.AddDays(1)));

        delegation.Revoke();
        Assert.False(delegation.IsValidFor(from, to, Now.AddDays(1)));
    }
}
