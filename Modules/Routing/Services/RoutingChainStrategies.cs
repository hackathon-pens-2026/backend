using Microsoft.EntityFrameworkCore;
using SignIt.Infrastructure.Errors;
using SignIt.Infrastructure.Persistence;

namespace SignIt.Modules.Routing.Services;

public sealed class ActivityRoutingChainStrategy : IRoutingChainStrategy
{
    private static readonly string[] Chain = ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Wadir3"];

    public bool CanHandle(string typeId) => typeId is "proposal" or "lpj";

    public Task<string[]> ResolveCodesAsync(RoutingInput input, AppDbContext db, CancellationToken ct)
    {
        return Task.FromResult((string[])Chain.Clone());
    }
}

public sealed class EquipmentLoanRoutingChainStrategy : IRoutingChainStrategy
{
    private static readonly string[] Chain = ["Ketupel", "KetuaOrganisasi", "Pembina", "Kemahasiswaan", "Wadir3", "Wadir2"];

    public bool CanHandle(string typeId) => typeId == "peminjaman-barang";

    public Task<string[]> ResolveCodesAsync(RoutingInput input, AppDbContext db, CancellationToken ct)
    {
        return Task.FromResult((string[])Chain.Clone());
    }
}

public sealed class RoomReservationRoutingChainStrategy : IRoutingChainStrategy
{
    public bool CanHandle(string typeId) => typeId == "peminjaman-ruangan";

    public async Task<string[]> ResolveCodesAsync(RoutingInput input, AppDbContext db, CancellationToken ct)
    {
        var resource = await db.FacilityResources.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.ResourceId && x.IsBookable, ct)
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "resource_not_bookable", "Pilih ruangan atau lapangan yang dapat dipinjam.");
        var facility = await db.Facilities.AsNoTracking().SingleAsync(x => x.Id == resource.FacilityId, ct);
        var facilityCode = facility.RequiresDagri ? "D3" : "PS";

        return facilityCode.Trim().ToUpperInvariant() switch
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

public sealed class RoutingChainStrategyRegistry
{
    private readonly IReadOnlyList<IRoutingChainStrategy> _strategies;

    public RoutingChainStrategyRegistry(IEnumerable<IRoutingChainStrategy>? strategies = null)
    {
        _strategies = strategies?.ToArray() ??
        [
            new ActivityRoutingChainStrategy(),
            new EquipmentLoanRoutingChainStrategy(),
            new RoomReservationRoutingChainStrategy()
        ];
    }

    public async Task<string[]> ResolveAsync(RoutingInput input, AppDbContext db, CancellationToken ct)
    {
        var strategy = _strategies.FirstOrDefault(s => s.CanHandle(input.TypeId))
            ?? throw new SignItDomainException(DomainErrorKind.Validation, "unsupported_letter_type", "Tipe surat tidak tersedia.");
        return await strategy.ResolveCodesAsync(input, db, ct);
    }
}

public static class PositionTitleResolver
{
    private static readonly Dictionary<string, string> StandardTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Ketupel"] = "Ketua Pelaksana",
        ["Kemahasiswaan"] = "Kemahasiswaan",
        ["MinatBakat"] = "Tim Pembina Minat dan Bakat",
        ["Wadir2"] = "Wakil Direktur II",
        ["Wadir3"] = "Wakil Direktur III"
    };

    public static string Resolve(string positionCode, string fallbackPositionName)
    {
        return StandardTitles.TryGetValue(positionCode, out var standardTitle)
            ? standardTitle
            : fallbackPositionName;
    }
}
