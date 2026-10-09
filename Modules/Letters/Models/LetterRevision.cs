namespace SignIt.Modules.Letters.Models;

public sealed class LetterRevision
{
    private LetterRevision() { }

    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public int RevisionNo { get; private set; }
    public string? TemplateVersionId { get; private set; }
    public string DataJson { get; private set; } = string.Empty;
    public string ContentHash { get; private set; } = string.Empty;
    public Guid? ReviewDocumentId { get; private set; }
    public Guid? FinalDocumentId { get; private set; }
    public DateTimeOffset FrozenAt { get; private set; }

    public static LetterRevision Create(
        Guid id,
        Guid requestId,
        int revisionNo,
        string? templateVersionId,
        string dataJson,
        string contentHash,
        Guid? reviewDocumentId,
        DateTimeOffset frozenAt)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (requestId == Guid.Empty) throw new ArgumentException("RequestId tidak boleh kosong.", nameof(requestId));
        if (revisionNo < 1) throw new ArgumentException("RevisionNo harus >= 1.", nameof(revisionNo));
        if (string.IsNullOrWhiteSpace(contentHash)) throw new ArgumentException("ContentHash tidak boleh kosong.", nameof(contentHash));

        return new LetterRevision
        {
            Id = id,
            RequestId = requestId,
            RevisionNo = revisionNo,
            TemplateVersionId = templateVersionId?.Trim(),
            DataJson = dataJson.Trim(),
            ContentHash = contentHash.Trim(),
            ReviewDocumentId = reviewDocumentId,
            FrozenAt = frozenAt
        };
    }

    public void SetFinalDocument(Guid finalDocumentId)
    {
        if (finalDocumentId == Guid.Empty) throw new ArgumentException("FinalDocumentId tidak boleh kosong.", nameof(finalDocumentId));
        FinalDocumentId = finalDocumentId;
    }

    public void UpdateDraftContent(string dataJson, string contentHash, DateTimeOffset updatedAt)
    {
        if (string.IsNullOrWhiteSpace(dataJson)) throw new ArgumentException("DataJson tidak boleh kosong.", nameof(dataJson));
        if (string.IsNullOrWhiteSpace(contentHash)) throw new ArgumentException("ContentHash tidak boleh kosong.", nameof(contentHash));
        DataJson = dataJson.Trim();
        ContentHash = contentHash.Trim();
        FrozenAt = updatedAt;
    }
}
