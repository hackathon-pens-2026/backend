using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Rooms.Services;

namespace SignIt.Modules.Rooms.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/rooms")]
[Produces("application/json")]
public sealed class RoomsController(AppDbContext db, RoomReservationService reservations) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(await (from resource in db.FacilityResources.AsNoTracking()
            join facility in db.Facilities.AsNoTracking() on resource.FacilityId equals facility.Id
            where resource.IsBookable
            orderby facility.Name, resource.Floor, resource.Code
            select new
            {
                resource.Id,
                resource.Code,
                resource.Floor,
                FacilityId = facility.Id,
                FacilityName = facility.Name,
                FacilityCode = facility.Code
            }).ToListAsync(ct));

    [HttpGet("{id:guid}/schedule")]
    [ProducesResponseType<AvailabilityDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AvailabilityDto>> Schedule(Guid id,
        [FromQuery] DateTimeOffset? from, [FromQuery] DateTimeOffset? to, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var start = from ?? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
        var end = to ?? start.AddDays(7);
        return Ok(await reservations.GetScheduleAsync(id, start, end, ct));
    }

    [HttpPost("check-availability")]
    [ProducesResponseType<AvailabilityDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<AvailabilityDto>> CheckAvailability(AvailabilityRequest request, CancellationToken ct)
        => Ok(await reservations.CheckAvailabilityAsync(request.RoomId, request.StartsAt, request.EndsAt, ct));

    [HttpPost("bookings")]
    [ProducesResponseType<ReservationDto>(StatusCodes.Status201Created)]
    public async Task<ActionResult<ReservationDto>> CreateBooking(RoomBookingRequest request, CancellationToken ct)
    {
        var booking = await reservations.CreateBookingAsync(User.GetUserId(), request, ct);
        return CreatedAtAction(nameof(Schedule), new { id = booking.RoomId }, booking);
    }

    [HttpPost("bookings/{id:guid}/confirm")]
    [ProducesResponseType<ConfirmReservationResult>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ConfirmReservationResult>> Confirm(Guid id, CancellationToken ct)
        => Ok(await reservations.TryConfirmAsync(id, User.GetUserId(), ct));

    [HttpPost("bookings/{id:guid}/cancel")]
    [ProducesResponseType<ReservationDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<ReservationDto>> Cancel(Guid id, CancellationToken ct)
        => Ok(await reservations.CancelBookingAsync(id, User.GetUserId(), ct));
}
