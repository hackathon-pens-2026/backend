namespace SignIt.Infrastructure.Storage;

public interface IStorageService
{
    Task<string> SaveAsync(string path, byte[] content, string contentType, CancellationToken ct);
    Task<string> SaveAsync(string path, Stream content, string contentType, CancellationToken ct);
    Task<byte[]?> ReadBytesAsync(string path, CancellationToken ct);
    Task<Stream?> OpenReadAsync(string path, CancellationToken ct);
    Task<bool> ExistsAsync(string path, CancellationToken ct);
    Task DeleteAsync(string path, CancellationToken ct);
}
