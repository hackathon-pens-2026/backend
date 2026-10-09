namespace SignIt.Modules.Signatures.Models;

public sealed class SigningAttempt
{
    private SigningAttempt() { }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid ActorId { get; private set; }
    public Guid QrAssetId { get; private set; }
    public int QrVersion { get; private set; }
    public string QrHash { get; private set; } = string.Empty;
    public string ContentHash { get; private set; } = string.Empty;
    public string Status { get; private set; } = string.Empty;
    public string? IdempotencyKey { get; private set; }
    public DateTimeOffset AttemptedAt { get; private set; }

    public static SigningAttempt Create(
        Guid id,
        Guid taskId,
        Guid revisionId,
        Guid actorId,
        Guid qrAssetId,
        int qrVersion,
        string qrHash,
        string contentHash,
        string status,
        string? idempotencyKey,
        DateTimeOffset attemptedAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId tidak boleh kosong.", nameof(taskId));
        if (actorId == Guid.Empty) throw new ArgumentException("ActorId tidak boleh kosong.", nameof(actorId));

        return new SigningAttempt
        {
            Id = id,
            TaskId = taskId,
            RevisionId = revisionId,
            ActorId = actorId,
            QrAssetId = qrAssetId,
            QrVersion = qrVersion,
            QrHash = qrHash,
            ContentHash = contentHash,
            Status = status,
            IdempotencyKey = idempotencyKey?.Trim(),
            AttemptedAt = attemptedAt
        };
    }
}
