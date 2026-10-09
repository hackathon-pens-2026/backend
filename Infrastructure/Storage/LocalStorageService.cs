using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace SignIt.Infrastructure.Storage;

public sealed class LocalStorageService : IStorageService
{
    private readonly string _rootPath;

    public LocalStorageService(IOptions<StorageOptions> options, IHostEnvironment env)
    {
        var configured = options.Value.RootDirectory;
        _rootPath = Path.IsPathRooted(configured)
            ? configured
            : Path.GetFullPath(configured, env.ContentRootPath);

        if (!Directory.Exists(_rootPath))
        {
            Directory.CreateDirectory(_rootPath);
        }
    }

    private string GetFullPath(string relativePath)
    {
        var normalized = relativePath.Replace('\\', '/').TrimStart('/');
        var full = Path.GetFullPath(Path.Combine(_rootPath, normalized));
        if (!full.StartsWith(_rootPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Jalur penyimpanan tidak aman.", nameof(relativePath));
        }
        return full;
    }

    public async Task<string> SaveAsync(string path, byte[] content, string contentType, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await File.WriteAllBytesAsync(fullPath, content, ct);
        return path.Replace('\\', '/');
    }

    public async Task<string> SaveAsync(string path, Stream content, string contentType, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        await using var fileStream = new FileStream(fullPath, FileMode.Create, FileAccess.Write, FileShare.None, 4096, useAsync: true);
        await content.CopyToAsync(fileStream, ct);
        return path.Replace('\\', '/');
    }

    public async Task<byte[]?> ReadBytesAsync(string path, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        if (!File.Exists(fullPath)) return null;
        return await File.ReadAllBytesAsync(fullPath, ct);
    }

    public Task<Stream?> OpenReadAsync(string path, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        if (!File.Exists(fullPath)) return Task.FromResult<Stream?>(null);
        Stream stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, useAsync: true);
        return Task.FromResult<Stream?>(stream);
    }

    public Task<bool> ExistsAsync(string path, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        return Task.FromResult(File.Exists(fullPath));
    }

    public Task DeleteAsync(string path, CancellationToken ct)
    {
        var fullPath = GetFullPath(path);
        if (File.Exists(fullPath))
        {
            File.Delete(fullPath);
        }
        return Task.CompletedTask;
    }
}
