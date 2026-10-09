namespace SignIt.Modules.Signatures.Models;

public sealed class SignatureEvidence
{
    private SignatureEvidence() { }

    public Guid Id { get; private set; }
    public Guid TaskId { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid ActorId { get; private set; }
    public string Role { get; private set; } = string.Empty;
    public string? PositionSnapshot { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public Guid QrAssetId { get; private set; }
    public int QrVersion { get; private set; }
    public string QrHash { get; private set; } = string.Empty;
    public DateTimeOffset SignedAt { get; private set; }
    public Guid? DelegatedFromUserId { get; private set; }
    public string? MandateDescription { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public static SignatureEvidence Create(
        Guid id,
        Guid taskId,
        Guid revisionId,
        Guid actorId,
        string role,
        string? positionSnapshot,
        string contentHash,
        Guid qrAssetId,
        int qrVersion,
        string qrHash,
        DateTimeOffset signedAt,
        Guid? delegatedFromUserId,
        string? mandateDescription,
        string? ipAddress,
        string? userAgent)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (taskId == Guid.Empty) throw new ArgumentException("TaskId tidak boleh kosong.", nameof(taskId));
        if (revisionId == Guid.Empty) throw new ArgumentException("RevisionId tidak boleh kosong.", nameof(revisionId));
        if (actorId == Guid.Empty) throw new ArgumentException("ActorId tidak boleh kosong.", nameof(actorId));
        if (string.IsNullOrWhiteSpace(role)) throw new ArgumentException("Role tidak boleh kosong.", nameof(role));
        if (string.IsNullOrWhiteSpace(contentHash)) throw new ArgumentException("ContentHash tidak boleh kosong.", nameof(contentHash));
        if (qrAssetId == Guid.Empty) throw new ArgumentException("QrAssetId tidak boleh kosong.", nameof(qrAssetId));
        if (string.IsNullOrWhiteSpace(qrHash)) throw new ArgumentException("QrHash tidak boleh kosong.", nameof(qrHash));

        return new SignatureEvidence
        {
            Id = id,
            TaskId = taskId,
            RevisionId = revisionId,
            ActorId = actorId,
            Role = role.Trim(),
            PositionSnapshot = positionSnapshot?.Trim(),
            ContentHash = contentHash.Trim(),
            QrAssetId = qrAssetId,
            QrVersion = qrVersion,
            QrHash = qrHash.Trim(),
            SignedAt = signedAt,
            DelegatedFromUserId = delegatedFromUserId,
            MandateDescription = mandateDescription?.Trim(),
            IpAddress = ipAddress?.Trim(),
            UserAgent = userAgent?.Trim()
        };
    }
}
