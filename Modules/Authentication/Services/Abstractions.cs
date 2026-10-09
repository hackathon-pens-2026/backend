using SignIt.Modules.Authentication.Models;
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

// This store is specific to authentication, not a generic repository. Commit is explicit.
public interface IAuthStore
{
    Task<IAuthTransaction> BeginTransactionAsync(CancellationToken ct);
    Task<User?> LockUserByEmailAsync(string normalizedEmail, CancellationToken ct);
    Task<User?> LockUserByIdAsync(Guid userId, CancellationToken ct);
    Task<User?> GetUserAsync(Guid userId, CancellationToken ct);
    Task<AuthSession?> GetSessionAsync(Guid sessionId, CancellationToken ct);
    Task<Guid?> FindRefreshOwnerAsync(string hash, CancellationToken ct);
    Task<RefreshCredential?> GetRefreshAsync(string hash, CancellationToken ct);
    Task<Guid?> FindResetOwnerAsync(string hash, CancellationToken ct);
    Task<PasswordResetToken?> GetResetAsync(string hash, CancellationToken ct);
    Task<IReadOnlyList<AuthSession>> GetActiveSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
    Task<IReadOnlyList<PasswordResetToken>> GetUnusedResetsAsync(Guid userId, CancellationToken ct);
    Task<IReadOnlyList<UserAssignment>> GetActiveAssignmentsAsync(Guid userId, DateTimeOffset now, CancellationToken ct);
    void AddSession(AuthSession session, RefreshCredential credential);
    void AddRefresh(RefreshCredential credential);
    void AddReset(PasswordResetToken token, PasswordResetEmail email);
    void AddAudit(AuthAudit audit);
    Task SaveChangesAsync(CancellationToken ct);
}

public interface IAuthTransaction : IAsyncDisposable
{
    Task CommitAsync(CancellationToken ct);
}

public enum PasswordCheck { Failed, Success, RehashNeeded }
public interface IPasswordService
{
    string Hash(string password);
    PasswordCheck Verify(string hash, string password);
    void VerifyDummy(string password);
}
public interface ITokenService
{
    string CreateOpaqueToken();
    string HashOpaqueToken(string token);
    AccessToken CreateAccessToken(User user, AuthSession session, DateTimeOffset now);
}
public interface IResetEmailProtector
{
    string Protect(string rawToken);
    string Unprotect(string protectedToken);
}

public sealed record EmailSendResult(bool Accepted, string? ProviderId, string? ErrorCode,
    bool Retryable = false, bool Unknown = false, TimeSpan? RetryAfter = null);

public interface IResetEmailSender
{
    Task<EmailSendResult> SendResetAsync(Guid emailId, string recipient, string token, CancellationToken ct);
}
