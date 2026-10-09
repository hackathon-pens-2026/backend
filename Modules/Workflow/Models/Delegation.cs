namespace SignIt.Modules.Workflow.Models;

public sealed class Delegation
{
    private Delegation() { }

    public Guid Id { get; private set; }
    public Guid FromUserId { get; private set; }
    public Guid ToUserId { get; private set; }
    public string? Scope { get; private set; }
    public DateTimeOffset StartAt { get; private set; }
    public DateTimeOffset EndAt { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }

    public static Delegation Create(
        Guid id,
        Guid fromUserId,
        Guid toUserId,
        string? scope,
        DateTimeOffset startAt,
        DateTimeOffset endAt,
        string reason)
    {
        if (id == Guid.Empty) throw new ArgumentException("Id tidak boleh kosong.", nameof(id));
        if (fromUserId == Guid.Empty) throw new ArgumentException("FromUserId tidak boleh kosong.", nameof(fromUserId));
        if (toUserId == Guid.Empty) throw new ArgumentException("ToUserId tidak boleh kosong.", nameof(toUserId));
        if (fromUserId == toUserId) throw new ArgumentException("Tidak dapat mendelegasikan tugas ke diri sendiri.", nameof(toUserId));
        if (endAt <= startAt) throw new ArgumentException("EndAt harus lebih besar dari StartAt.", nameof(endAt));
        if (string.IsNullOrWhiteSpace(reason)) throw new ArgumentException("Alasan delegasi wajib diisi.", nameof(reason));

        return new Delegation
        {
            Id = id,
            FromUserId = fromUserId,
            ToUserId = toUserId,
            Scope = scope?.Trim(),
            StartAt = startAt,
            EndAt = endAt,
            Reason = reason.Trim(),
            IsActive = true
        };
    }

    public bool IsValidFor(Guid fromUserId, Guid toUserId, DateTimeOffset now)
        => IsActive && FromUserId == fromUserId && ToUserId == toUserId && now >= StartAt && now <= EndAt;

    public void Revoke() => IsActive = false;
}
