using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Authentication.Services;

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
