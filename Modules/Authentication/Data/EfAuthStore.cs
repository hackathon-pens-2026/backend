using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Authentication.DTOs;
using SignIt.Modules.Authentication.Models;
using SignIt.Infrastructure.Persistence;

namespace SignIt.Modules.Authentication.Data;

public sealed class EfAuthStore(AppDbContext db) : IAuthStore
{
    public async Task<IAuthTransaction> BeginTransactionAsync(CancellationToken ct)
        => new AuthTransaction(await db.Database.BeginTransactionAsync(ct));

    public Task<User?> LockUserByEmailAsync(string normalizedEmail, CancellationToken ct)
        => db.Users.FromSqlInterpolated($"SELECT * FROM auth_users WHERE \"NormalizedEmail\" = {normalizedEmail} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    public Task<User?> LockUserByIdAsync(Guid userId, CancellationToken ct)
        => db.Users.FromSqlInterpolated($"SELECT * FROM auth_users WHERE \"Id\" = {userId} FOR UPDATE")
            .SingleOrDefaultAsync(ct);

    public Task<User?> GetUserAsync(Guid userId, CancellationToken ct)
        => db.Users.AsNoTracking().SingleOrDefaultAsync(x => x.Id == userId, ct);

    public Task<AuthSession?> GetSessionAsync(Guid sessionId, CancellationToken ct)
        => db.Sessions.SingleOrDefaultAsync(x => x.Id == sessionId, ct);

    public Task<Guid?> FindRefreshOwnerAsync(string hash, CancellationToken ct)
        => (from token in db.RefreshCredentials
            join session in db.Sessions on token.SessionId equals session.Id
            where token.TokenHash == hash
            select (Guid?)session.UserId).SingleOrDefaultAsync(ct);

    public Task<RefreshCredential?> GetRefreshAsync(string hash, CancellationToken ct)
        => db.RefreshCredentials.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);

    public Task<Guid?> FindResetOwnerAsync(string hash, CancellationToken ct)
        => db.ResetTokens.Where(x => x.TokenHash == hash).Select(x => (Guid?)x.UserId).SingleOrDefaultAsync(ct);

    public Task<PasswordResetToken?> GetResetAsync(string hash, CancellationToken ct)
        => db.ResetTokens.SingleOrDefaultAsync(x => x.TokenHash == hash, ct);

    public async Task<IReadOnlyList<AuthSession>> GetActiveSessionsAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
        => await db.Sessions.Where(x => x.UserId == userId && x.RevokedAt == null && x.ExpiresAt > now).ToListAsync(ct);

    public async Task<IReadOnlyList<PasswordResetToken>> GetUnusedResetsAsync(Guid userId, CancellationToken ct)
        => await db.ResetTokens.Where(x => x.UserId == userId && x.ConsumedAt == null).ToListAsync(ct);

    public async Task<IReadOnlyList<UserAssignment>> GetActiveAssignmentsAsync(Guid userId, DateTimeOffset now, CancellationToken ct)
        => await db.Assignments.AsNoTracking().Where(x => x.UserId == userId && x.IsActive
            && x.ValidFrom <= now && (x.ValidTo == null || x.ValidTo > now))
            .OrderBy(x => x.PositionCode).ThenBy(x => x.Id).ToListAsync(ct);

    public void AddSession(AuthSession session, RefreshCredential credential)
    {
        db.Sessions.Add(session);
        db.RefreshCredentials.Add(credential);
    }

    public void AddRefresh(RefreshCredential credential) => db.RefreshCredentials.Add(credential);
    public void AddReset(PasswordResetToken token, PasswordResetEmail email)
    {
        db.ResetTokens.Add(token);
        db.ResetEmails.Add(email);
    }
    public void AddAudit(AuthAudit audit) => db.Audits.Add(audit);

    public async Task SaveChangesAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new AuthException("auth_state_conflict", "Status akun berubah. Ulangi permintaan.", AuthErrorKind.Conflict);
        }
    }

    private sealed class AuthTransaction(IDbContextTransaction transaction) : IAuthTransaction
    {
        public Task CommitAsync(CancellationToken ct) => transaction.CommitAsync(ct);
        public ValueTask DisposeAsync() => transaction.DisposeAsync();
    }
}
