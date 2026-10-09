namespace SignIt.Modules.Workflow.Models;

public sealed class WorkflowTask
{
    private WorkflowTask() { }

    public Guid Id { get; private set; }
    public Guid RevisionId { get; private set; }
    public Guid? ParticipantId { get; private set; }
    public int Order { get; private set; }
    public WorkflowActionType ActionType { get; private set; }
    public Guid AssignedUserId { get; private set; }
    public string? DomainCode { get; private set; }
    public WorkflowTaskStatus Status { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? DueAt { get; private set; }
    public Guid? ActedByUserId { get; private set; }
    public DateTimeOffset? ActedAt { get; private set; }
    public string? Comment { get; private set; }
    public Guid RowVersion { get; private set; }

    public static WorkflowTask Create(
        Guid id,
        Guid revisionId,
        Guid? participantId,
        int order,
        WorkflowActionType actionType,
        Guid assignedUserId,
        string? domainCode,
        WorkflowTaskStatus status = WorkflowTaskStatus.Pending,
        DateTimeOffset? activatedAt = null,
        DateTimeOffset? dueAt = null)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (revisionId == Guid.Empty) throw new ArgumentException("RevisionId tidak boleh kosong.", nameof(revisionId));
        if (assignedUserId == Guid.Empty) throw new ArgumentException("AssignedUserId tidak boleh kosong.", nameof(assignedUserId));
        if (order < 1) throw new ArgumentException("Order harus >= 1.", nameof(order));

        return new WorkflowTask
        {
            Id = id,
            RevisionId = revisionId,
            ParticipantId = participantId,
            Order = order,
            ActionType = actionType,
            AssignedUserId = assignedUserId,
            DomainCode = domainCode?.Trim(),
            Status = status,
            ActivatedAt = activatedAt,
            DueAt = dueAt,
            RowVersion = Guid.NewGuid()
        };
    }

    public void Activate(DateTimeOffset now, DateTimeOffset? dueAt = null)
    {
        if (Status != WorkflowTaskStatus.Pending && Status != WorkflowTaskStatus.Deferred)
            throw new InvalidOperationException($"Tugas dengan status {Status} tidak dapat diaktifkan.");

        Status = WorkflowTaskStatus.Active;
        ActivatedAt = now;
        if (dueAt.HasValue) DueAt = dueAt;
        RowVersion = Guid.NewGuid();
    }

    public void Complete(WorkflowTaskStatus completedStatus, Guid actorUserId, DateTimeOffset now, string? comment = null)
    {
        if (Status != WorkflowTaskStatus.Active)
            throw new InvalidOperationException("Hanya tugas yang aktif yang dapat diselesaikan.");
        if (completedStatus is not (WorkflowTaskStatus.Signed or WorkflowTaskStatus.Approved or WorkflowTaskStatus.Acknowledged))
            throw new ArgumentException("Status penyelesaian tidak valid.", nameof(completedStatus));
        if (actorUserId == Guid.Empty)
            throw new ArgumentException("ActorUserId tidak boleh kosong.", nameof(actorUserId));

        Status = completedStatus;
        ActedByUserId = actorUserId;
        ActedAt = now;
        Comment = comment?.Trim();
        RowVersion = Guid.NewGuid();
    }

    public void Reject(Guid actorUserId, DateTimeOffset now, string comment)
    {
        if (Status != WorkflowTaskStatus.Active)
            throw new InvalidOperationException("Hanya tugas yang aktif yang dapat ditolak.");
        if (string.IsNullOrWhiteSpace(comment))
            throw new ArgumentException("Alasan penolakan tugas wajib diisi.", nameof(comment));

        Status = WorkflowTaskStatus.Rejected;
        ActedByUserId = actorUserId;
        ActedAt = now;
        Comment = comment.Trim();
        RowVersion = Guid.NewGuid();
    }

    public void RequestRevision(Guid actorUserId, DateTimeOffset now, string comment)
    {
        if (Status != WorkflowTaskStatus.Active)
            throw new InvalidOperationException("Hanya tugas yang aktif yang dapat meminta revisi.");
        if (string.IsNullOrWhiteSpace(comment))
            throw new ArgumentException("Catatan revisi wajib diisi.", nameof(comment));

        Status = WorkflowTaskStatus.RevisionRequested;
        ActedByUserId = actorUserId;
        ActedAt = now;
        Comment = comment.Trim();
        RowVersion = Guid.NewGuid();
    }

    public void Supersede()
    {
        Status = WorkflowTaskStatus.Superseded;
        RowVersion = Guid.NewGuid();
    }

    public void Defer(Guid actor, DateTimeOffset now, DateTimeOffset until, string reason)
    {
        if (Status != WorkflowTaskStatus.Active || actor == Guid.Empty || until <= now || string.IsNullOrWhiteSpace(reason))
            throw new InvalidOperationException("Penundaan membutuhkan tugas aktif, actor, alasan dan batas waktu baru.");
        Status = WorkflowTaskStatus.Deferred;
        DueAt = until;
        ActedByUserId = actor;
        ActedAt = now;
        Comment = reason.Trim();
        RowVersion = Guid.NewGuid();
    }

    public void Cancel()
    {
        if (Status is not (WorkflowTaskStatus.Pending or WorkflowTaskStatus.Active or WorkflowTaskStatus.Deferred)) return;
        Status = WorkflowTaskStatus.Cancelled;
        RowVersion = Guid.NewGuid();
    }

    public void Touch() => RowVersion = Guid.NewGuid();
}
