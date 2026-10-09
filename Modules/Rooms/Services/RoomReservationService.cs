using Microsoft.EntityFrameworkCore;
using Npgsql;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Rooms.Models;

namespace SignIt.Modules.Rooms.Services;

public sealed record RoomBookingRequest(Guid FacilityResourceId, DateTimeOffset StartsAt, DateTimeOffset EndsAt,
    string ActivityType);
public sealed record AvailabilityRequest(Guid RoomId, DateTimeOffset StartsAt, DateTimeOffset EndsAt);
public sealed record ReservationDto(Guid Id, Guid RoomId, string ActivityType, DateTimeOffset StartsAt,
    DateTimeOffset EndsAt, string Status, Guid? LetterRequestId);
public sealed record AvailabilityDto(bool IsAvailable, IReadOnlyList<ReservationDto> ConfirmedConflicts,
    IReadOnlyList<ReservationDto> PendingConflicts);
public sealed record ConfirmReservationResult(bool Confirmed, IReadOnlyList<ReservationDto> Conflicts);

public sealed class RoomReservationService(AppDbContext db, TimeProvider clock)
{
    private const int MaxScheduleRangeDays = 90;
    private static readonly TimeSpan JakartaOffset = TimeSpan.FromHours(7);

    public async Task<AvailabilityDto> CheckAvailabilityAsync(Guid roomId, DateTimeOffset startsAt,
        DateTimeOffset endsAt, CancellationToken ct)
    {
        await EnsureBookableAsync(roomId, ct);
        ValidateRange(startsAt, endsAt);
        return await BuildAvailabilityAsync(roomId, startsAt.ToUniversalTime(), endsAt.ToUniversalTime(), ct);
    }

    public async Task<AvailabilityDto> GetScheduleAsync(Guid roomId, DateTimeOffset from, DateTimeOffset to,
        CancellationToken ct)
    {
        await EnsureBookableAsync(roomId, ct);
        ValidateRange(from, to);
        if (to - from > TimeSpan.FromDays(MaxScheduleRangeDays))
            throw Error("schedule_range_too_large", $"Rentang jadwal maksimal {MaxScheduleRangeDays} hari.");
        return await BuildAvailabilityAsync(roomId, from.ToUniversalTime(), to.ToUniversalTime(), ct);
    }

    public async Task<ReservationDto> CreateBookingAsync(Guid actor, RoomBookingRequest request, CancellationToken ct)
    {
        await EnsureBookableAsync(request.FacilityResourceId, ct);
        ValidateRange(request.StartsAt, request.EndsAt);
        var reservation = RoomReservation.Request(Guid.NewGuid(), request.FacilityResourceId, actor,
            request.ActivityType, request.StartsAt.ToUniversalTime(), request.EndsAt.ToUniversalTime(),
            clock.GetUtcNow());
        db.RoomReservations.Add(reservation);
        await db.SaveChangesAsync(ct);
        return Map(reservation);
    }

    public async Task<ReservationDto> CancelBookingAsync(Guid id, Guid actor, CancellationToken ct)
    {
        var reservation = await LoadAuthorizedAsync(id, actor, ct);
        reservation.Cancel(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
        return Map(reservation);
    }

    public async Task<ConfirmReservationResult> TryConfirmAsync(Guid id, Guid actor, CancellationToken ct)
    {
        var reservation = await LoadAuthorizedAsync(id, actor, ct);
        return await ConfirmInternalAsync(reservation, ct);
    }

    // Dipanggil alur finalisasi surat: konfirmasi seluruh booking milik revisi secara atomik per booking.
    public async Task<ConfirmReservationResult> TryConfirmForRevisionAsync(Guid revisionId, CancellationToken ct)
    {
        var pending = await db.RoomReservations
            .Where(x => x.LetterRevisionId == revisionId && x.Status == RoomReservationStatus.Pending)
            .OrderBy(x => x.CreatedAt)
            .ToListAsync(ct);
        var conflicts = new List<ReservationDto>();
        var allConfirmed = true;
        foreach (var reservation in pending)
        {
            var result = await ConfirmInternalAsync(reservation, ct);
            if (!result.Confirmed)
            {
                allConfirmed = false;
                conflicts.AddRange(result.Conflicts);
            }
        }
        return new ConfirmReservationResult(allConfirmed, conflicts);
    }

    private async Task<ConfirmReservationResult> ConfirmInternalAsync(RoomReservation reservation, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        reservation.Confirm(now);
        try
        {
            await db.SaveChangesAsync(ct);
            return new ConfirmReservationResult(true, []);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.ExclusionViolation })
        {
            // Constraint EXCLUDE di database menolak reservasi yang bentrok; yang kalah tidak mendapat konfirmasi.
            db.ChangeTracker.Clear();
            var conflicts = await db.RoomReservations.AsNoTracking()
                .Where(x => x.FacilityResourceId == reservation.FacilityResourceId
                    && x.Status == RoomReservationStatus.Confirmed && x.Id != reservation.Id
                    && x.StartsAt < reservation.EndsAt && reservation.StartsAt < x.EndsAt)
                .OrderBy(x => x.StartsAt)
                .ToListAsync(ct);
            return new ConfirmReservationResult(false, conflicts.Select(Map).ToArray());
        }
    }

    private async Task<RoomReservation> LoadAuthorizedAsync(Guid id, Guid actor, CancellationToken ct)
    {
        var reservation = await db.RoomReservations.SingleOrDefaultAsync(x => x.Id == id, ct)
            ?? throw new SignItDomainException(DomainErrorKind.NotFound, "reservation_not_found", "Reservasi tidak ditemukan.");
        if (reservation.RequestedByUserId == actor) return reservation;
        var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == actor && x.IsActive, ct);
        if (user is null || user.Category is not (UserCategory.BAAK or UserCategory.Management))
            throw new SignItDomainException(DomainErrorKind.Forbidden, "reservation_forbidden",
                "Hanya pemohon atau petugas yang dapat mengubah reservasi ini.");
        return reservation;
    }

    private async Task<AvailabilityDto> BuildAvailabilityAsync(Guid roomId, DateTimeOffset startsAt,
        DateTimeOffset endsAt, CancellationToken ct)
    {
        var overlapping = await db.RoomReservations.AsNoTracking()
            .Where(x => x.FacilityResourceId == roomId
                && (x.Status == RoomReservationStatus.Confirmed || x.Status == RoomReservationStatus.Pending)
                && x.StartsAt < endsAt && startsAt < x.EndsAt)
            .OrderBy(x => x.StartsAt)
            .ToListAsync(ct);
        var confirmed = overlapping.Where(x => x.Status == RoomReservationStatus.Confirmed).Select(Map).ToArray();
        var pending = overlapping.Where(x => x.Status == RoomReservationStatus.Pending).Select(Map).ToArray();
        return new AvailabilityDto(confirmed.Length == 0, confirmed, pending);
    }

    private async Task EnsureBookableAsync(Guid roomId, CancellationToken ct)
    {
        var exists = await db.FacilityResources.AsNoTracking()
            .AnyAsync(x => x.Id == roomId && x.IsBookable, ct);
        if (!exists)
            throw new SignItDomainException(DomainErrorKind.Validation, "resource_not_bookable",
                "Ruangan atau lapangan tidak tersedia untuk dipinjam.");
    }

    private static void ValidateRange(DateTimeOffset startsAt, DateTimeOffset endsAt)
    {
        if (endsAt <= startsAt)
            throw Error("invalid_time_range", "Waktu selesai harus setelah waktu mulai.");
    }

    // Waktu ditampilkan dalam Asia/Jakarta (UTC+7) sesuai kontrak, data tetap disimpan sebagai instant UTC.
    private static ReservationDto Map(RoomReservation reservation) => new(
        reservation.Id, reservation.FacilityResourceId, reservation.ActivityType,
        reservation.StartsAt.ToOffset(JakartaOffset), reservation.EndsAt.ToOffset(JakartaOffset),
        reservation.Status.ToString(), reservation.LetterRequestId);

    private static SignItDomainException Error(string code, string message)
        => new(DomainErrorKind.Validation, code, message);
}
