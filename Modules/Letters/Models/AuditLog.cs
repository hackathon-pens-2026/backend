namespace SignIt.Modules.Letters.Models;

public sealed class AuditLog
{
    private AuditLog() { }

    public Guid Id { get; private set; }
    public Guid? ActorUserId { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string Entity { get; private set; } = string.Empty;
    public Guid? EntityId { get; private set; }
    public Guid? RevisionId { get; private set; }
    public DateTimeOffset AtUtc { get; private set; }
    public string CorrelationId { get; private set; } = string.Empty;
    public string? Details { get; private set; }
    public string? IpAddress { get; private set; }
    public string? UserAgent { get; private set; }

    public static AuditLog Record(
        Guid? actorUserId,
        string action,
        string entity,
        Guid? entityId,
        Guid? revisionId,
        DateTimeOffset atUtc,
        string correlationId,
        string? details = null,
        string? ipAddress = null,
        string? userAgent = null)
    {
        return new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            Action = action.Trim(),
            Entity = entity.Trim(),
            EntityId = entityId,
            RevisionId = revisionId,
            AtUtc = atUtc,
            CorrelationId = correlationId.Trim(),
            Details = details,
            IpAddress = ipAddress,
            UserAgent = userAgent
        };
    }
}
