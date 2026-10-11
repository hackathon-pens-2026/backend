using SignIt.Modules.Authentication.DTOs;

namespace SignIt.Modules.Authentication.Services;

public interface IAuthService
{
    Task<AuthTokensDto> LoginAsync(string email, string password, RequestMetadata metadata, CancellationToken ct);
    Task<AuthTokensDto> RefreshAsync(string refreshToken, RequestMetadata metadata, CancellationToken ct);
    Task LogoutAsync(Guid userId, Guid sessionId, RequestMetadata metadata, CancellationToken ct);
    Task ForgotPasswordAsync(string email, RequestMetadata metadata, CancellationToken ct);
    Task ResetPasswordAsync(string token, string password, RequestMetadata metadata, CancellationToken ct);
    Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct);
    Task<bool> IsSessionValidAsync(Guid userId, Guid sessionId, Guid securityStamp, CancellationToken ct);
}
