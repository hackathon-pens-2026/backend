namespace SignIt.Modules.Letters.Models;

public sealed class LetterRequest
{
    private LetterRequest() { }

    public Guid Id { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public string TypeId { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public Guid SubmittedByUserId { get; private set; }
    public Guid? OrganizationId { get; private set; }
    public LetterStatus Status { get; private set; }
    public Guid? CurrentRevisionId { get; private set; }
    public DateTimeOffset SubmittedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public Guid RowVersion { get; private set; }

    public static LetterRequest Create(
        Guid id,
        string number,
        string typeId,
        string title,
        Guid submittedByUserId,
        Guid? organizationId,
        DateTimeOffset now)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (string.IsNullOrWhiteSpace(number)) throw new ArgumentException("Number tidak boleh kosong.", nameof(number));
        if (string.IsNullOrWhiteSpace(typeId)) throw new ArgumentException("TypeId tidak boleh kosong.", nameof(typeId));
        if (string.IsNullOrWhiteSpace(title)) throw new ArgumentException("Title tidak boleh kosong.", nameof(title));
        if (submittedByUserId == Guid.Empty) throw new ArgumentException("SubmittedByUserId tidak boleh kosong.", nameof(submittedByUserId));

        return new LetterRequest
        {
            Id = id,
            Number = number.Trim(),
            TypeId = typeId.Trim(),
            Title = title.Trim(),
            SubmittedByUserId = submittedByUserId,
            OrganizationId = organizationId,
            Status = LetterStatus.InProgress,
            SubmittedAt = now,
            RowVersion = Guid.NewGuid()
        };
    }

    public void SetCurrentRevision(Guid revisionId)
    {
        CurrentRevisionId = revisionId;
        RowVersion = Guid.NewGuid();
    }

    public void MarkDraft()
    {
        if (CurrentRevisionId.HasValue || Status != LetterStatus.InProgress)
            throw new InvalidOperationException("Hanya pengajuan baru yang dapat dijadikan draft.");
        Status = LetterStatus.Draft;
        RowVersion = Guid.NewGuid();
    }

    public void MarkFinalizing()
    {
        if (Status != LetterStatus.InProgress && Status != LetterStatus.ProcessingFailed
            && Status != LetterStatus.AwaitingResourceResolution)
            throw new InvalidOperationException($"Transisi ke Finalizing tidak valid dari status {Status}.");

        Status = LetterStatus.Finalizing;
        RowVersion = Guid.NewGuid();
    }

    public void MarkAwaitingResourceResolution()
    {
        if (Status != LetterStatus.InProgress && Status != LetterStatus.AwaitingResourceResolution)
            throw new InvalidOperationException($"Transisi ke AwaitingResourceResolution tidak valid dari status {Status}.");

        Status = LetterStatus.AwaitingResourceResolution;
        RowVersion = Guid.NewGuid();
    }

    public void Submit(Guid organizationId, string number, DateTimeOffset now)
    {
        if (Status is not (LetterStatus.Draft or LetterStatus.NeedsRevision) || organizationId == Guid.Empty || string.IsNullOrWhiteSpace(number))
            throw new InvalidOperationException("Hanya draft valid yang dapat diajukan.");
        OrganizationId = organizationId;
        Number = number;
        SubmittedAt = now;
        Status = LetterStatus.InProgress;
        RowVersion = Guid.NewGuid();
    }

    public void MarkCompleted(DateTimeOffset now)
    {
        if (Status != LetterStatus.Finalizing && Status != LetterStatus.InProgress && Status != LetterStatus.ProcessingFailed)
            throw new InvalidOperationException($"Transisi ke Completed tidak valid dari status {Status}.");

        Status = LetterStatus.Completed;
        CompletedAt = now;
        RowVersion = Guid.NewGuid();
    }

    public void MarkProcessingFailed()
    {
        Status = LetterStatus.ProcessingFailed;
        RowVersion = Guid.NewGuid();
    }

    public void MarkRejected()
    {
        if (Status != LetterStatus.InProgress) throw new InvalidOperationException("Hanya surat berjalan yang dapat ditolak.");
        Status = LetterStatus.Rejected;
        RowVersion = Guid.NewGuid();
    }

    public void MarkNeedsRevision()
    {
        if (Status != LetterStatus.InProgress) throw new InvalidOperationException("Hanya surat berjalan yang dapat diminta revisi.");
        Status = LetterStatus.NeedsRevision;
        RowVersion = Guid.NewGuid();
    }

    public void UpdateDraft(string title, Guid revisionId)
    {
        if (Status is not (LetterStatus.Draft or LetterStatus.NeedsRevision) || string.IsNullOrWhiteSpace(title) || revisionId == Guid.Empty)
            throw new InvalidOperationException("Hanya draft/revisi yang dapat diedit.");
        Title = title.Trim();
        SetCurrentRevision(revisionId);
    }

    public void Cancel()
    {
        if (Status is not (LetterStatus.Draft or LetterStatus.InProgress or LetterStatus.NeedsRevision))
            throw new InvalidOperationException("Surat ini tidak dapat dibatalkan.");
        Status = LetterStatus.Cancelled;
        RowVersion = Guid.NewGuid();
    }

    public void MarkRevoked()
    {
        if (Status != LetterStatus.Completed)
            throw new InvalidOperationException("Hanya surat berstatus Completed yang dapat dicabut.");

        Status = LetterStatus.Revoked;
        RowVersion = Guid.NewGuid();
    }
}
