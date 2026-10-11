using SignIt.Modules.Signatures.DTOs;
using SignIt.Modules.Signatures.Models;

namespace SignIt.Modules.Signatures.Services;

public interface IUserSignatureQrService
{
    Task<UserSignatureQrDto> GetOrCreateForUserAsync(Guid userId, CancellationToken ct);
    Task<UserSignatureQrDto?> GetActiveByUserIdAsync(Guid userId, CancellationToken ct);
    Task<(byte[] Bytes, string MimeType, string Sha256)?> GetRawImageAsync(Guid userId, CancellationToken ct);
    Task<UserSignatureQr> EnsureActiveEntityAsync(Guid userId, CancellationToken ct);
}
