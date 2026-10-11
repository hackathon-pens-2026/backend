using SignIt.Modules.Rooms.Services;

namespace SignIt.Modules.Chat.Services;

public sealed record ExtractionOutcome(
    string BotResponseText,
    AvailabilityDto? Availability,
    string Status,
    bool FallbackAvailable);

public interface IChatExtractionProcessor
{
    Task<ExtractionOutcome> ProcessExtractionAsync(
        Guid actor,
        Guid sessionId,
        string? userText,
        string? typeId,
        Dictionary<string, string> currentFields,
        CancellationToken ct);
}
