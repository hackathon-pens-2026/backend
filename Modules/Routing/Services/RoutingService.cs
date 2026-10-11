using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Errors;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Routing.Services;

public sealed record RoutingInput(string TypeId, Guid OrganizationId, Guid CommitteeChairId, Guid OrganizationChairId,
    Guid? ResourceId = null);
public sealed record RoutingStage(int Order, Guid UserId, string Name, string PositionCode, string PositionName);

public sealed class RoutingService
{
    private readonly AppDbContext _db;
    private readonly TimeProvider _clock;
    private readonly RoutingChainStrategyRegistry _strategies;

    public RoutingService(
        AppDbContext db,
        TimeProvider clock,
        RoutingChainStrategyRegistry? strategies = null)
    {
        _db = db;
        _clock = clock;
        _strategies = strategies ?? new RoutingChainStrategyRegistry();
    }

    public async Task<IReadOnlyList<RoutingStage>> ResolveAsync(Guid requester, RoutingInput input, CancellationToken ct)
    {
        var organization = await _db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.OrganizationId && x.IsActive, ct)
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "organization_not_found", "Organisasi tidak tersedia.");

        var codes = await _strategies.ResolveAsync(input, _db, ct);

        var now = _clock.GetUtcNow();
        var assignments = await _db.Assignments.AsNoTracking().Where(x => x.IsActive && x.Scope == organization.Scope
            && x.ValidFrom <= now && (x.ValidTo == null || x.ValidTo > now)).ToListAsync(ct);
        if (!assignments.Any(x => x.UserId == requester && x.Capability == UserCapability.Requester))
            throw new SignItDomainException(DomainErrorKind.Forbidden, "requester_scope_denied", "Anda tidak memiliki assignment pengaju pada scope ini.");
        var studentAffairsCode = organization.Kind switch
        {
            "Himpunan" => "Kemahasiswaan",
            "Organisasi" => "MinatBakat",
            _ => throw new SignItDomainException(DomainErrorKind.Conflict, "organization_kind_invalid",
                "Jenis organisasi harus Himpunan atau Organisasi.")
        };
        codes = codes.Select(code => code == "Kemahasiswaan" ? studentAffairsCode : code).ToArray();
        var stages = new List<RoutingStage>();
        foreach (var code in codes)
        {
            var selected = code == "Ketupel" ? input.CommitteeChairId : code == "KetuaOrganisasi" ? input.OrganizationChairId : (Guid?)null;
            var candidates = assignments.Where(x => x.PositionCode == code
                && x.Capability == (selected.HasValue ? UserCapability.Signer : UserCapability.Approver)
                && (!selected.HasValue || x.UserId == selected.Value)).ToArray();
            if (candidates.Length != 1)
                throw new SignItDomainException(DomainErrorKind.Conflict, "routing_unresolved", $"Assignment {code} tidak tersedia atau ambigu pada scope ini.");
            var assignment = candidates[0];
            if (!selected.HasValue && assignment.UserId == requester)
                throw new SignItDomainException(DomainErrorKind.Conflict, "self_approval_blocked", "Pengaju tidak dapat menyetujui pengajuannya sendiri sebagai pejabat approver.");
            var user = await _db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assignment.UserId && x.IsActive, ct);
            if (user == null)
                throw new SignItDomainException(DomainErrorKind.Conflict, "inactive_participant", "Akun peserta tidak aktif.");
            var positionName = PositionTitleResolver.Resolve(code, assignment.PositionName);
            stages.Add(new RoutingStage(stages.Count + 1, user.Id, user.Name, code, positionName));
        }
        return stages;
    }
}
