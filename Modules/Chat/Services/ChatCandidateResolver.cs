using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Chat.DTOs;
using SignIt.Modules.Rooms.Services;

namespace SignIt.Modules.Chat.Services;

public sealed class ChatCandidateResolver : IChatCandidateResolver
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;

    public ChatCandidateResolver(AppDbContext db, TimeProvider clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<IReadOnlyList<CandidatePersonDto>> GetSignerCandidatesAsync(
        string[] positionCodes,
        CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        return await (from a in _db.Assignments.AsNoTracking()
            join u in _db.Users.AsNoTracking() on a.UserId equals u.Id
            where a.IsActive && u.IsActive && a.Capability == UserCapability.Signer
                && positionCodes.Contains(a.PositionCode)
                && a.ValidFrom <= now && (a.ValidTo == null || a.ValidTo > now)
            orderby a.PositionCode, u.Name
            select new CandidatePersonDto(
                u.Id,
                u.Name,
                a.PositionCode,
                a.PositionName,
                u.NimNip != null ? $"NRP/NIP: {u.NimNip} · {a.PositionName}" : a.PositionName))
            .Distinct()
            .ToListAsync(ct);
    }

    public async Task<RoomOptionDto?> FindMatchingRoomAsync(string? query, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;

        var q = query.Trim().ToLowerInvariant();
        var allRooms = await (from r in _db.FacilityResources.AsNoTracking()
            join f in _db.Facilities.AsNoTracking() on r.FacilityId equals f.Id
            where r.IsBookable
            select new RoomOptionDto(r.Id, r.Code, r.Floor, f.Id, f.Name)).ToListAsync(ct);

        return allRooms.FirstOrDefault(r =>
            q.Contains(r.Code.ToLowerInvariant()) ||
            r.Code.ToLowerInvariant().Contains(q) ||
            q.Contains(r.FacilityName.ToLowerInvariant()) ||
            r.FacilityName.ToLowerInvariant().Contains(q) ||
            $"{r.FacilityName} {r.Code}".ToLowerInvariant().Contains(q) ||
            q.Contains($"{r.FacilityName} {r.Code}".ToLowerInvariant()));
    }
}
