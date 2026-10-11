using SignIt.Modules.Signatures.DTOs;

namespace SignIt.Modules.Signatures.Services;

public interface IPublicVerificationService
{
    Task<PublicVerificationDto> VerifyByCodeAsync(string code, CancellationToken ct);
    Task<VerifyUploadResultDto> VerifyByUploadAsync(Stream fileStream, long fileSize, CancellationToken ct);
}
