using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Persistence;
using SignIt.Infrastructure.Errors;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Routing.Services;

public sealed record RoutingInput(string TypeId, Guid OrganizationId, Guid CommitteeChairId, Guid OrganizationChairId,
    Guid? ResourceId = null);
public sealed record RoutingStage(int Order, Guid UserId, string Name, string PositionCode, string PositionName);

public sealed class RoutingService(AppDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<RoutingStage>> ResolveAsync(Guid requester, RoutingInput input, CancellationToken ct)
    {
        var organization = await db.Organizations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.OrganizationId && x.IsActive, ct)
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "organization_not_found", "Organisasi tidak tersedia.");
        string? facilityCode = null;
        if (input.TypeId == "peminjaman-ruangan")
        {
            var resource = await db.FacilityResources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ResourceId && x.IsBookable, ct)
                ?? throw new SignItDomainException(DomainErrorKind.Validation, "resource_not_bookable", "Pilih ruangan atau lapangan yang dapat dipinjam.");
            var facility = await db.Facilities.AsNoTracking().SingleAsync(x => x.Id == resource.FacilityId, ct);
            facilityCode = facility.RequiresDagri ? "D3" : "PS";
        }
        var codes = input.TypeId switch
        {
            "proposal" or "lpj" => new[] { "Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Wadir3" },
            "peminjaman-ruangan" => ResolveRoomChain(facilityCode),
            "peminjaman-barang" => ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Wadir3", "Wadir2"],
            _ => throw new SignItDomainException(DomainErrorKind.Validation, "unsupported_letter_type", "Tipe surat tidak tersedia.")
        };
        var now = clock.GetUtcNow();
        var assignments = await db.Assignments.AsNoTracking().Where(x => x.IsActive && x.Scope == organization.Scope
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
            if (code == "Dagri" && assignment.UserId == requester)
                throw new SignItDomainException(DomainErrorKind.Conflict, "self_approval_blocked", "Pengaju tidak dapat menyetujui tugas Dagri miliknya sendiri.");
            var user = await db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == assignment.UserId && x.IsActive, ct);
            if (user == null)
                throw new SignItDomainException(DomainErrorKind.Conflict, "inactive_participant", "Akun peserta tidak aktif.");
            var positionName = code switch
            {
                "Ketupel" => "Ketua Pelaksana",
                "Kemahasiswaan" => "Kemahasiswaan",
                "MinatBakat" => "Tim Pembina Minat dan Bakat",
                "Wadir2" => "Wakil Direktur II",
                "Wadir3" => "Wakil Direktur III",
                _ => assignment.PositionName
            };
            stages.Add(new RoutingStage(stages.Count + 1, user.Id, user.Name, code, positionName));
        }
        return stages;
    }

    private static string[] ResolveRoomChain(string? facilityCode)
    {
        return facilityCode?.Trim().ToUpperInvariant() switch
        {
            "PASCA" or "PS" or "SAW" => ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "BAAK", "Wadir3"],
            "D3" or "D4" or "LAPANGAN_MERAH" or "LAPANGAN_FUTSAL" or "LAPANGAN_BASKET"
                => ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Dagri", "BAAK", "Wadir3"],
            null or "" => throw new SignItDomainException(DomainErrorKind.Validation,
                "facility_required", "Pilih fasilitas untuk menentukan jalur persetujuan."),
            _ => throw new SignItDomainException(DomainErrorKind.Conflict,
                "facility_routing_unconfigured", "Kebijakan persetujuan fasilitas ini belum dikonfigurasi.")
        };
    }
}
