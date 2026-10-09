namespace SignIt.Modules.Email.Models;

public sealed class EmailProviderEvent
{
    private EmailProviderEvent() { }

    public Guid Id { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public string EventType { get; private set; } = string.Empty;
    public string? ProviderMessageId { get; private set; }
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }

    public static EmailProviderEvent Record(string provider, string eventId, string eventType,
        string? providerMessageId, DateTimeOffset occurredAt, DateTimeOffset processedAt)
    {
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(eventId)
            || string.IsNullOrWhiteSpace(eventType))
            throw new ArgumentException("Data event provider email tidak valid.");
        return new EmailProviderEvent
        {
            Id = Guid.NewGuid(), Provider = provider.Trim(), EventId = eventId.Trim(),
            EventType = eventType.Trim(), ProviderMessageId = providerMessageId,
            OccurredAt = occurredAt.ToUniversalTime(), ProcessedAt = processedAt.ToUniversalTime()
        };
    }
}
