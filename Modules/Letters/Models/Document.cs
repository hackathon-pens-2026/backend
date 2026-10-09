namespace SignIt.Modules.Letters.Models;

public sealed class Document
{
    private Document() { }

    public Guid Id { get; private set; }
    public Guid? RevisionId { get; private set; }
    public DocumentKind Kind { get; private set; }
    public string StorageKey { get; private set; } = string.Empty;
    public string MimeType { get; private set; } = string.Empty;
    public long Bytes { get; private set; }
    public string Sha256 { get; private set; } = string.Empty;
    public string ProcessingState { get; private set; } = "Ready";
    public DateTimeOffset CreatedAt { get; private set; }

    public static Document Create(
        Guid id,
        Guid? revisionId,
        DocumentKind kind,
        string storageKey,
        string mimeType,
        long bytes,
        string sha256,
        DateTimeOffset createdAt,
        string processingState = "Ready")
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (string.IsNullOrWhiteSpace(storageKey)) throw new ArgumentException("StorageKey tidak boleh kosong.", nameof(storageKey));
        if (string.IsNullOrWhiteSpace(mimeType)) throw new ArgumentException("MimeType tidak boleh kosong.", nameof(mimeType));
        if (string.IsNullOrWhiteSpace(sha256) || sha256.Length != 64) throw new ArgumentException("Sha256 harus 64 karakter hex.", nameof(sha256));

        return new Document
        {
            Id = id,
            RevisionId = revisionId,
            Kind = kind,
            StorageKey = storageKey.Trim(),
            MimeType = mimeType.Trim(),
            Bytes = bytes,
            Sha256 = sha256.Trim().ToLowerInvariant(),
            ProcessingState = processingState,
            CreatedAt = createdAt
        };
    }
}
