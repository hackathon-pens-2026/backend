namespace SignIt.Modules.Chat.Models;

public sealed class ChatMessageRecord
{
    private ChatMessageRecord() { }

    public long Id { get; private set; }
    public Guid SessionId { get; private set; }
    public string FromRole { get; private set; } = string.Empty;
    public string Content { get; private set; } = string.Empty;
    public string? WidgetType { get; private set; }
    public string? ExtractedDataJson { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static ChatMessageRecord Create(
        Guid sessionId,
        string fromRole,
        string content,
        string? widgetType,
        string? extractedDataJson,
        DateTimeOffset now)
    {
        if (sessionId == Guid.Empty) throw new ArgumentException("SessionId tidak boleh kosong.", nameof(sessionId));
        if (string.IsNullOrWhiteSpace(fromRole)) throw new ArgumentException("FromRole tidak boleh kosong.", nameof(fromRole));
        if (string.IsNullOrWhiteSpace(content)) throw new ArgumentException("Content tidak boleh kosong.", nameof(content));

        return new ChatMessageRecord
        {
            SessionId = sessionId,
            FromRole = fromRole.Trim(),
            Content = content.Trim(),
            WidgetType = widgetType?.Trim(),
            ExtractedDataJson = extractedDataJson?.Trim(),
            CreatedAt = now
        };
    }
}
