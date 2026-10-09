using System.Net.Mail;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Authentication.DTOs;

namespace SignIt.Modules.Authentication.Services;

public sealed class AuthService(IAuthStore store, IPasswordService passwords, ITokenService tokens,
    IResetEmailProtector emailProtector, AuthOptions options, TimeProvider clock) : IAuthService
{
    private static AuthException InvalidLogin() => new("invalid_credentials", "Email atau password tidak valid.", AuthErrorKind.Unauthorized);
    private static AuthException InvalidRefresh() => new("invalid_refresh_token", "Sesi tidak valid. Silakan login kembali.", AuthErrorKind.Unauthorized);
    private static AuthException InvalidReset() => new("invalid_reset_token", "Tautan reset tidak valid atau sudah kedaluwarsa.", AuthErrorKind.Validation);

    public async Task<AuthTokensDto> LoginAsync(string email, string password, RequestMetadata metadata, CancellationToken ct)
    {
        ValidateEmail(email);
        if (string.IsNullOrEmpty(password) || password.Length > PasswordPolicy.MaximumLength) throw InvalidLogin();
        await using var transaction = await store.BeginTransactionAsync(ct);
        var user = await store.LockUserByEmailAsync(User.NormalizeEmail(email), ct);
        var now = clock.GetUtcNow();
        if (user is null)
        {
            passwords.VerifyDummy(password);
            throw InvalidLogin();
        }

        var check = passwords.Verify(user.PasswordHash, password);
        if (!user.CanLogin(now) || check == PasswordCheck.Failed)
        {
            user.RecordFailedLogin(now, options.MaxFailedLoginAttempts, TimeSpan.FromMinutes(options.LockoutMinutes));
            Audit(user.Id, "auth.login_failed", now, metadata);
            await store.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            throw InvalidLogin();
        }

        user.RecordSuccessfulLogin(now, check == PasswordCheck.RehashNeeded ? passwords.Hash(password) : null);
        var sessions = await store.GetActiveSessionsAsync(user.Id, now, ct);
        foreach (var old in sessions.OrderBy(s => s.CreatedAt).Take(Math.Max(0, sessions.Count - options.MaxActiveSessions + 1)))
            old.Revoke(now, "session_limit");

        var session = AuthSession.Create(user, now, now.AddDays(options.SessionDays));
        var rawRefresh = tokens.CreateOpaqueToken();
        store.AddSession(session, RefreshCredential.Create(session.Id, tokens.HashOpaqueToken(rawRefresh), now, session.ExpiresAt));
        Audit(user.Id, "auth.login", now, metadata);
        var result = await CreateResultAsync(user, session, rawRefresh, now, ct);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task<AuthTokensDto> RefreshAsync(string refreshToken, RequestMetadata metadata, CancellationToken ct)
    {
        if (!IsOpaqueToken(refreshToken)) throw InvalidRefresh();
        var hash = tokens.HashOpaqueToken(refreshToken);
        var owner = await store.FindRefreshOwnerAsync(hash, ct);
        if (owner is null) throw InvalidRefresh();
        await using var transaction = await store.BeginTransactionAsync(ct);
        var user = await store.LockUserByIdAsync(owner.Value, ct);
        var credential = await store.GetRefreshAsync(hash, ct);
        var session = credential is null ? null : await store.GetSessionAsync(credential.SessionId, ct);
        var now = clock.GetUtcNow();
        if (user is null || credential is null || session is null || !session.IsValid(user, now)) throw InvalidRefresh();
        if (credential.UsedAt is not null)
        {
            session.Revoke(now, "refresh_token_reuse");
            Audit(user.Id, "auth.refresh_reuse", now, metadata);
            await store.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            throw InvalidRefresh();
        }
        if (credential.ExpiresAt <= now) throw InvalidRefresh();

        credential.Consume(now);
        var rawRefresh = tokens.CreateOpaqueToken();
        store.AddRefresh(RefreshCredential.Create(session.Id, tokens.HashOpaqueToken(rawRefresh), now, session.ExpiresAt));
        Audit(user.Id, "auth.refresh", now, metadata);
        var result = await CreateResultAsync(user, session, rawRefresh, now, ct);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return result;
    }

    public async Task LogoutAsync(Guid userId, Guid sessionId, RequestMetadata metadata, CancellationToken ct)
    {
        await using var transaction = await store.BeginTransactionAsync(ct);
        var user = await store.LockUserByIdAsync(userId, ct);
        var session = await store.GetSessionAsync(sessionId, ct);
        if (user is null || session is null || session.UserId != userId) throw InvalidRefresh();
        var now = clock.GetUtcNow();
        session.Revoke(now, "logout");
        Audit(userId, "auth.logout", now, metadata);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ForgotPasswordAsync(string email, RequestMetadata metadata, CancellationToken ct)
    {
        ValidateEmail(email);
        await using var transaction = await store.BeginTransactionAsync(ct);
        var user = await store.LockUserByEmailAsync(User.NormalizeEmail(email), ct);
        var now = clock.GetUtcNow();
        // Existence, disabled accounts and cooldown all produce the same response.
        if (user is null || !user.IsActive
            || user.LastPasswordResetRequestedAt > now.AddSeconds(-options.ResetCooldownSeconds)) return;

        foreach (var previous in await store.GetUnusedResetsAsync(user.Id, ct)) previous.Invalidate(now);
        var raw = tokens.CreateOpaqueToken();
        var reset = PasswordResetToken.Create(user, tokens.HashOpaqueToken(raw), now, now.AddMinutes(options.ResetTokenMinutes));
        store.AddReset(reset, PasswordResetEmail.Queue(reset.Id, user.Email, emailProtector.Protect(raw), now));
        user.RequestPasswordReset(now);
        Audit(user.Id, "auth.password_reset_requested", now, metadata);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task ResetPasswordAsync(string token, string password, RequestMetadata metadata, CancellationToken ct)
    {
        PasswordPolicy.Validate(password, options);
        if (!IsOpaqueToken(token)) throw InvalidReset();
        var hash = tokens.HashOpaqueToken(token);
        var owner = await store.FindResetOwnerAsync(hash, ct);
        if (owner is null) throw InvalidReset();
        await using var transaction = await store.BeginTransactionAsync(ct);
        var user = await store.LockUserByIdAsync(owner.Value, ct);
        var reset = await store.GetResetAsync(hash, ct);
        var now = clock.GetUtcNow();
        if (user is null || reset is null || !reset.IsValid(user, now)) throw InvalidReset();

        user.ResetPassword(passwords.Hash(password));
        foreach (var previous in await store.GetUnusedResetsAsync(user.Id, ct)) previous.Invalidate(now);
        foreach (var session in await store.GetActiveSessionsAsync(user.Id, now, ct)) session.Revoke(now, "password_reset");
        Audit(user.Id, "auth.password_reset", now, metadata);
        await store.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
    }

    public async Task<UserDto> GetMeAsync(Guid userId, CancellationToken ct)
    {
        var user = await store.GetUserAsync(userId, ct);
        if (user is null || !user.IsActive) throw InvalidRefresh();
        return await MapUserAsync(user, clock.GetUtcNow(), ct);
    }

    public async Task<bool> IsSessionValidAsync(Guid userId, Guid sessionId, Guid securityStamp, CancellationToken ct)
    {
        var user = await store.GetUserAsync(userId, ct);
        var session = await store.GetSessionAsync(sessionId, ct);
        return user is not null && session is not null && user.SecurityStamp == securityStamp
            && session.IsValid(user, clock.GetUtcNow());
    }

    private async Task<AuthTokensDto> CreateResultAsync(User user, AuthSession session, string refresh,
        DateTimeOffset now, CancellationToken ct)
    {
        var access = tokens.CreateAccessToken(user, session, now);
        return new AuthTokensDto(access.Value, access.ExpiresAt, refresh, session.ExpiresAt, await MapUserAsync(user, now, ct));
    }

    private async Task<UserDto> MapUserAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        var assignments = await store.GetActiveAssignmentsAsync(user.Id, now, ct);
        // Requester is the base capability; approval/unit permissions never come from category.
        var capabilities = assignments.Select(a => a.Capability).Append(UserCapability.Requester).Distinct().Order().ToArray();
        return new UserDto(user.Id, user.Name, user.Email, user.NimNip, user.IsActive, user.EmailVerifiedAt,
            user.Category, user.Surface, capabilities, assignments.Select(a => new AssignmentDto(a.Id,
                a.PositionCode, a.PositionName, a.Scope, a.Capability, a.ValidFrom, a.ValidTo)).ToArray());
    }

    private void Audit(Guid userId, string action, DateTimeOffset now, RequestMetadata metadata)
        => store.AddAudit(AuthAudit.Record(userId, action, now, metadata.IpAddress, metadata.UserAgent, metadata.CorrelationId));

    private static bool IsOpaqueToken(string token) => token is { Length: 43 }
        && token.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    private static void ValidateEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email) || email.Length > 254
            || !MailAddress.TryCreate(email.Trim(), out var address) || address.Address != email.Trim())
            throw new AuthException("validation_failed", "Format email tidak valid.", AuthErrorKind.Validation);
    }
}
