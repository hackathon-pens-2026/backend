namespace SignIt.Modules.Signatures.Models;

public sealed class UserSignatureQr
{
    private UserSignatureQr() { }

    public Guid Id { get; private set; }
    public Guid OwnerUserId { get; private set; }
    public string OpaqueCode { get; private set; } = string.Empty;
    public string PrivateStorageKey { get; private set; } = string.Empty;
    public int Version { get; private set; }
    public string ImageSha256 { get; private set; } = string.Empty;
    public SignatureQrStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static UserSignatureQr Create(
        Guid id,
        Guid ownerUserId,
        string opaqueCode,
        string privateStorageKey,
        int version,
        string imageSha256,
        DateTimeOffset createdAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (ownerUserId == Guid.Empty) throw new ArgumentException("OwnerUserId tidak boleh kosong.", nameof(ownerUserId));
        if (string.IsNullOrWhiteSpace(opaqueCode)) throw new ArgumentException("OpaqueCode tidak boleh kosong.", nameof(opaqueCode));
        if (string.IsNullOrWhiteSpace(privateStorageKey)) throw new ArgumentException("PrivateStorageKey tidak boleh kosong.", nameof(privateStorageKey));
        if (version < 1) throw new ArgumentException("Version harus >= 1.", nameof(version));
        if (string.IsNullOrWhiteSpace(imageSha256) || imageSha256.Length != 64)
            throw new ArgumentException("ImageSha256 harus 64 karakter hex.", nameof(imageSha256));

        return new UserSignatureQr
        {
            Id = id,
            OwnerUserId = ownerUserId,
            OpaqueCode = opaqueCode.Trim(),
            PrivateStorageKey = privateStorageKey.Trim(),
            Version = version,
            ImageSha256 = imageSha256.Trim().ToLowerInvariant(),
            Status = SignatureQrStatus.Active,
            CreatedAt = createdAt
        };
    }

    public void Rotate(string newStorageKey, int newVersion, string newSha256)
    {
        if (Status != SignatureQrStatus.Active)
            throw new InvalidOperationException("Hanya QR aktif yang dapat dirotasi.");
        if (newVersion <= Version)
            throw new ArgumentException("Versi baru harus lebih tinggi.", nameof(newVersion));

        Status = SignatureQrStatus.Rotated;
    }

    public void Revoke()
    {
        if (Status == SignatureQrStatus.Revoked)
            throw new InvalidOperationException("QR sudah dicabut.");

        Status = SignatureQrStatus.Revoked;
    }
}
