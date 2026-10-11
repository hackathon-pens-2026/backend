using SignIt.Infrastructure.Persistence;

namespace SignIt.Modules.Routing.Services;

public interface IRoutingChainStrategy
{
    bool CanHandle(string typeId);
    Task<string[]> ResolveCodesAsync(RoutingInput input, AppDbContext db, CancellationToken ct);
}
