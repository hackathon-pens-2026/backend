namespace SignIt.Modules.Signatures.Models;

public sealed class VerificationRecord
{
    private VerificationRecord() { }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid? FinalDocumentId { get; private set; }
    public string RandomCode { get; private set; } = string.Empty;
    public string FinalHash { get; private set; } = string.Empty;
    public VerificationStatus Status { get; private set; }
    public DateTimeOffset PublishedAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }

    public static VerificationRecord Create(
        Guid id,
        Guid requestId,
        Guid? finalDocumentId,
        string randomCode,
        string finalHash,
        DateTimeOffset publishedAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (requestId == Guid.Empty) throw new ArgumentException("RequestId tidak boleh kosong.", nameof(requestId));
        if (string.IsNullOrWhiteSpace(randomCode)) throw new ArgumentException("RandomCode tidak boleh kosong.", nameof(randomCode));
        if (string.IsNullOrWhiteSpace(finalHash) || finalHash.Length != 64)
            throw new ArgumentException("FinalHash harus 64 karakter hex.", nameof(finalHash));

        return new VerificationRecord
        {
            Id = id,
            RequestId = requestId,
            FinalDocumentId = finalDocumentId,
            RandomCode = randomCode.Trim().ToUpperInvariant(),
            FinalHash = finalHash.Trim().ToLowerInvariant(),
            Status = VerificationStatus.Valid,
            PublishedAt = publishedAt
        };
    }

    public void Revoke(string reason, DateTimeOffset revokedAt)
    {
        if (Status == VerificationStatus.Revoked)
            throw new InvalidOperationException("Surat sudah dicabut.");
        if (string.IsNullOrWhiteSpace(reason))
            throw new ArgumentException("Alasan pencabutan surat wajib diisi.", nameof(reason));

        Status = VerificationStatus.Revoked;
        RevokedAt = revokedAt;
        RevocationReason = reason.Trim();
    }
}
