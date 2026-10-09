using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Authentication.Services;

namespace SignIt.Modules.Routing.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/routing")]
public sealed class RoutingCatalogController(AppDbContext db, TimeProvider clock) : ControllerBase
{
    [HttpGet("organizations")]
    public async Task<IActionResult> Organizations(CancellationToken ct)
    {
        var actor = User.GetUserId();
        var now = clock.GetUtcNow();
        return Ok(await db.Organizations.AsNoTracking().Where(o => o.IsActive && db.Assignments.Any(a =>
            a.UserId == actor && a.Scope == o.Scope && a.Capability == UserCapability.Requester && a.IsActive
            && a.ValidFrom <= now && (a.ValidTo == null || a.ValidTo > now)))
            .OrderBy(x => x.Name).Select(x => new { x.Id, x.Name, x.Kind }).ToListAsync(ct));
    }

    [HttpGet("resources")]
    public async Task<IActionResult> Resources([FromQuery] Guid facilityId, CancellationToken ct)
        => Ok(await db.FacilityResources.AsNoTracking().Where(x => x.FacilityId == facilityId && x.IsBookable)
            .OrderBy(x => x.Floor).ThenBy(x => x.Code).Select(x => new { x.Id, x.FacilityId, x.Code, x.Floor }).ToListAsync(ct));

    [HttpGet("facilities")]
    public async Task<IActionResult> Facilities(CancellationToken ct)
        => Ok(await db.Facilities.AsNoTracking().OrderBy(x => x.Name).Select(x => new { x.Id, x.Code, x.Name }).ToListAsync(ct));

    [HttpGet("organizations/{id:guid}/candidates")]
    public async Task<IActionResult> Candidates(Guid id, CancellationToken ct)
    {
        var actor = User.GetUserId();
        var now = clock.GetUtcNow();
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(o => o.Id == id && o.IsActive
            && db.Assignments.Any(a => a.UserId == actor && a.Scope == o.Scope && a.Capability == UserCapability.Requester
            && a.IsActive && a.ValidFrom <= now && (a.ValidTo == null || a.ValidTo > now)), ct);
        if (organization == null) return NotFound();
        return Ok(await (from a in db.Assignments.AsNoTracking()
            join u in db.Users.AsNoTracking() on a.UserId equals u.Id
            where a.Scope == organization.Scope && a.IsActive && u.IsActive && a.Capability == UserCapability.Signer
                && (a.PositionCode == "Ketupel" || a.PositionCode == "KetuaOrganisasi")
                && a.ValidFrom <= now && (a.ValidTo == null || a.ValidTo > now)
            orderby a.PositionCode, u.Name
            select new { UserId = u.Id, u.Name, a.PositionCode, a.PositionName }).Distinct().ToListAsync(ct));
    }
}
