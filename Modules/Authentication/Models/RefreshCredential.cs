namespace SignIt.Modules.Authentication.Models;

public sealed class RefreshCredential
{
    private RefreshCredential() { }
    public Guid Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public Guid Version { get; private set; }

    public static RefreshCredential Create(Guid sessionId, string hash, DateTimeOffset now, DateTimeOffset expiresAt)
        => new()
        {
            Id = Guid.NewGuid(), SessionId = sessionId, TokenHash = hash,
            CreatedAt = now, ExpiresAt = expiresAt, Version = Guid.NewGuid()
        };

    public void Consume(DateTimeOffset now)
    {
        if (UsedAt is not null || ExpiresAt <= now) throw new InvalidOperationException("Refresh token tidak valid.");
        UsedAt = now;
        Version = Guid.NewGuid();
    }
}
