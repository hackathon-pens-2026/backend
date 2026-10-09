using SignIt.Modules.Rooms.Models;
using Xunit;

namespace SignIt.Rooms.Tests;

public sealed class RoomIntervalTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void BackToBackIntervals_DoNotOverlap()
    {
        Assert.False(RoomInterval.Overlaps(Start, Start.AddHours(2), Start.AddHours(2), Start.AddHours(4)));
    }

    [Fact]
    public void PartiallyOverlappingIntervals_AreDetected()
    {
        Assert.True(RoomInterval.Overlaps(Start, Start.AddHours(2), Start.AddHours(1), Start.AddHours(3)));
    }

    [Fact]
    public void ContainedInterval_IsDetected()
    {
        Assert.True(RoomInterval.Overlaps(Start, Start.AddHours(4), Start.AddHours(1), Start.AddHours(2)));
    }

    [Fact]
    public void IdenticalIntervals_AreDetected()
    {
        Assert.True(RoomInterval.Overlaps(Start, Start.AddHours(2), Start, Start.AddHours(2)));
    }
}

public sealed class RoomReservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid ResourceId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();

    private static RoomReservation Pending() => RoomReservation.Request(Guid.NewGuid(), ResourceId, UserId,
        "Uji Kegiatan", Now.AddHours(10), Now.AddHours(12), Now);

    [Fact]
    public void Request_RejectsEndNotAfterStart()
    {
        Assert.Throws<ArgumentException>(() => RoomReservation.Request(Guid.NewGuid(), ResourceId, UserId,
            "Uji", Now.AddHours(10), Now.AddHours(10), Now));
    }

    [Fact]
    public void Request_RejectsEmptyActivityType()
    {
        Assert.Throws<ArgumentException>(() => RoomReservation.Request(Guid.NewGuid(), ResourceId, UserId,
            "  ", Now.AddHours(10), Now.AddHours(12), Now));
    }

    [Fact]
    public void Confirm_MovesPendingToConfirmed()
    {
        var reservation = Pending();
        reservation.Confirm(Now.AddMinutes(5));

        Assert.Equal(RoomReservationStatus.Confirmed, reservation.Status);
        Assert.Equal(Now.AddMinutes(5), reservation.ConfirmedAt);
    }

    [Fact]
    public void Confirm_RejectsReservationThatIsNotPending()
    {
        var reservation = Pending();
        reservation.Confirm(Now);
        Assert.Throws<InvalidOperationException>(() => reservation.Confirm(Now.AddMinutes(1)));
    }

    [Fact]
    public void Cancel_WorksForConfirmedReservation()
    {
        var reservation = Pending();
        reservation.Confirm(Now);
        reservation.Cancel(Now.AddMinutes(1));

        Assert.Equal(RoomReservationStatus.Cancelled, reservation.Status);
    }

    [Fact]
    public void Release_IsOnlyAllowedForConfirmedReservation()
    {
        var pending = Pending();
        Assert.Throws<InvalidOperationException>(() => pending.Release(Now));

        var confirmed = Pending();
        confirmed.Confirm(Now);
        confirmed.Release(Now.AddMinutes(1));
        Assert.Equal(RoomReservationStatus.Released, confirmed.Status);
    }

    [Fact]
    public void Overlaps_UsesHalfOpenInterval()
    {
        var reservation = Pending();

        Assert.True(reservation.Overlaps(Now.AddHours(11), Now.AddHours(13)));
        Assert.False(reservation.Overlaps(Now.AddHours(12), Now.AddHours(14)));
    }
}
