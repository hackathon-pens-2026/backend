namespace SignIt.Modules.Authentication.Models;

public sealed class AuthAudit
{
    private AuthAudit() { }
    public Guid Id { get; private set; }
    public Guid? UserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public DateTimeOffset AtUtc { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;

    public static AuthAudit Record(Guid? userId, string action, DateTimeOffset now,
        string? ip, string? userAgent, string correlationId) => new()
        {
            Id = Guid.NewGuid(), UserId = userId, Action = action, AtUtc = now,
            IpAddress = ip, UserAgent = userAgent, CorrelationId = correlationId
        };
}
