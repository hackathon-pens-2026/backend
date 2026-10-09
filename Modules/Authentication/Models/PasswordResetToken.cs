namespace SignIt.Modules.Authentication.Models;

public sealed class PasswordResetToken
{
    private PasswordResetToken() { }
    public Guid Id { get; private set; }
    public Guid UserId { get; private set; }
    public Guid SecurityStamp { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ConsumedAt { get; private set; }
    public Guid Version { get; private set; }

    public static PasswordResetToken Create(User user, string hash, DateTimeOffset now, DateTimeOffset expiresAt)
        => new()
        {
            Id = Guid.NewGuid(), UserId = user.Id, SecurityStamp = user.SecurityStamp,
            TokenHash = hash, CreatedAt = now, ExpiresAt = expiresAt, Version = Guid.NewGuid()
        };

    public bool IsValid(User user, DateTimeOffset now) => user.IsActive && UserId == user.Id
        && SecurityStamp == user.SecurityStamp && ConsumedAt is null && ExpiresAt > now;

    public void Invalidate(DateTimeOffset now)
    {
        ConsumedAt ??= now;
        Version = Guid.NewGuid();
    }
}
