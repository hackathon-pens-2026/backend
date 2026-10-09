namespace SignIt.Modules.Authentication.Models;

public sealed class AuthSession
{
    private AuthSession() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid SecurityStamp { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public string? RevocationReason { get; private set; }
    public Guid Version { get; private set; }

    public static AuthSession Create(User user, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        if (!user.IsActive || expiresAt <= now) throw new ArgumentException("Sesi tidak valid.");
        return new AuthSession
        {
            Id = Guid.NewGuid(), UserId = user.Id, SecurityStamp = user.SecurityStamp,
            CreatedAt = now, ExpiresAt = expiresAt, Version = Guid.NewGuid()
        };
    }

    public bool IsValid(User user, DateTimeOffset now) => user.IsActive && UserId == user.Id
        && SecurityStamp == user.SecurityStamp && RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is not null) return;
        RevokedAt = now;
        RevocationReason = reason;
        Version = Guid.NewGuid();
    }
}
