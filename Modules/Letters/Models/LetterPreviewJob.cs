namespace SignIt.Modules.Letters.Models;

public sealed class LetterPreviewJob
{
    private LetterPreviewJob() { }
    public Guid Id { get; private set; }
    public Guid RequestId { get; private set; }
    public Guid RevisionId { get; private set; }
    public string InputHash { get; private set; } = "";
    public string InputJson { get; private set; } = "";
    public string State { get; private set; } = "Pending";
    public int Attempts { get; private set; }
    public Guid? LeaseToken { get; private set; }
    public DateTimeOffset? LeaseUntil { get; private set; }
    public Guid? DocumentId { get; private set; }
    public string? SlotsJson { get; private set; }
    public string? ErrorCode { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static LetterPreviewJob Queue(Guid requestId, Guid revisionId, string hash, string input, DateTimeOffset now)
    {
        if (requestId == Guid.Empty || revisionId == Guid.Empty || hash.Length != 64 || string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Data preview job tidak valid.");
        return new() { Id = Guid.NewGuid(), RequestId = requestId, RevisionId = revisionId,
            InputHash = hash, InputJson = input, CreatedAt = now, UpdatedAt = now };
    }

    public Guid Claim(DateTimeOffset now, TimeSpan duration)
    {
        if (State != "Pending" && !(State == "Processing" && LeaseUntil <= now))
            throw new InvalidOperationException("Preview job belum dapat diproses.");
        State = "Processing";
        Attempts++;
        LeaseToken = Guid.NewGuid();
        LeaseUntil = now + duration;
        UpdatedAt = now;
        return LeaseToken.Value;
    }

    public void Complete(Guid lease, Guid documentId, string slotsJson, DateTimeOffset now)
    {
        CheckLease(lease);
        if (documentId == Guid.Empty || string.IsNullOrWhiteSpace(slotsJson)) throw new ArgumentException("Hasil preview tidak valid.");
        DocumentId = documentId;
        SlotsJson = slotsJson;
        Finish("Ready", null, now);
    }
    public void Fail(Guid lease, string errorCode, DateTimeOffset now)
    { CheckLease(lease); Finish("Failed", errorCode, now); }
    public void Supersede(Guid lease, DateTimeOffset now)
    { CheckLease(lease); Finish("Superseded", "stale_draft", now); }
    public void Retry(DateTimeOffset now)
    {
        if (State != "Failed" || Attempts >= 3) throw new InvalidOperationException("Batas retry preview tercapai.");
        Finish("Pending", null, now);
    }
    private void CheckLease(Guid lease)
    {
        if (State != "Processing" || LeaseToken != lease) throw new InvalidOperationException("Lease preview tidak valid.");
    }
    private void Finish(string state, string? error, DateTimeOffset now)
    { State = state; ErrorCode = error; LeaseToken = null; LeaseUntil = null; UpdatedAt = now; }
}
