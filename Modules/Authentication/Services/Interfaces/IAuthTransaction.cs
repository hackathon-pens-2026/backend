namespace SignIt.Modules.Authentication.Services;

public interface IAuthTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}
