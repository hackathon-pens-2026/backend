namespace SignIt.Modules.Rooms.Models;

public enum RoomReservationStatus { Pending, Confirmed, Cancelled, Released }

public sealed class RoomReservation
{
    private RoomReservation() { }

    public Guid Id { get; private set; }
    public Guid FacilityResourceId { get; private set; }
    public Guid RequestedByUserId { get; private set; }
    public Guid? LetterRequestId { get; private set; }
    public Guid? LetterRevisionId { get; private set; }
    public string ActivityType { get; private set; } = string.Empty;
    public DateTimeOffset StartsAt { get; private set; }
    public DateTimeOffset EndsAt { get; private set; }
    public RoomReservationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? ConfirmedAt { get; private set; }
    public DateTimeOffset? ClosedAt { get; private set; }
    public Guid RowVersion { get; private set; }

    public static RoomReservation Request(
        Guid id,
        Guid facilityResourceId,
        Guid requestedByUserId,
        string activityType,
        DateTimeOffset startsAt,
        DateTimeOffset endsAt,
        DateTimeOffset now,
        Guid? letterRequestId = null,
        Guid? letterRevisionId = null)
    {
        if (id == Guid.Empty || facilityResourceId == Guid.Empty || requestedByUserId == Guid.Empty)
            throw new ArgumentException("Data reservasi tidak lengkap.");
        if (string.IsNullOrWhiteSpace(activityType) || activityType.Length > 200)
            throw new ArgumentException("Jenis kegiatan wajib diisi, maksimal 200 karakter.");
        if (endsAt <= startsAt)
            throw new ArgumentException("Waktu selesai harus setelah waktu mulai.");

        return new RoomReservation
        {
            Id = id,
            FacilityResourceId = facilityResourceId,
            RequestedByUserId = requestedByUserId,
            ActivityType = activityType.Trim(),
            StartsAt = startsAt.ToUniversalTime(),
            EndsAt = endsAt.ToUniversalTime(),
            LetterRequestId = letterRequestId,
            LetterRevisionId = letterRevisionId,
            Status = RoomReservationStatus.Pending,
            CreatedAt = now.ToUniversalTime(),
            RowVersion = Guid.NewGuid()
        };
    }

    public void Confirm(DateTimeOffset now)
    {
        if (Status != RoomReservationStatus.Pending)
            throw new InvalidOperationException($"Reservasi berstatus {Status} tidak dapat dikonfirmasi.");
        Status = RoomReservationStatus.Confirmed;
        ConfirmedAt = now.ToUniversalTime();
        RowVersion = Guid.NewGuid();
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status is RoomReservationStatus.Cancelled or RoomReservationStatus.Released)
            throw new InvalidOperationException("Reservasi sudah tidak aktif.");
        Status = RoomReservationStatus.Cancelled;
        ClosedAt = now.ToUniversalTime();
        RowVersion = Guid.NewGuid();
    }

    public void Release(DateTimeOffset now)
    {
        if (Status != RoomReservationStatus.Confirmed)
            throw new InvalidOperationException("Hanya reservasi terkonfirmasi yang dapat dilepas.");
        Status = RoomReservationStatus.Released;
        ClosedAt = now.ToUniversalTime();
        RowVersion = Guid.NewGuid();
    }

    public bool Overlaps(DateTimeOffset startsAt, DateTimeOffset endsAt)
        => RoomInterval.Overlaps(StartsAt, EndsAt, startsAt, endsAt);
}

public static class RoomInterval
{
    // Interval setengah terbuka [start, end): kegiatan yang berakhir tepat saat kegiatan lain mulai tidak bentrok.
    public static bool Overlaps(DateTimeOffset startsAt, DateTimeOffset endsAt,
        DateTimeOffset otherStartsAt, DateTimeOffset otherEndsAt)
        => startsAt < otherEndsAt && otherStartsAt < endsAt;
}
