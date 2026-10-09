using SignIt.Modules.Templates.Services;

namespace SignIt.Infrastructure.Llm;

public sealed record LlmExtractionResult(
    bool Success,
    string? AssistantMessage,
    Dictionary<string, string> ExtractedFields,
    string? DetectedLetterType,
    string? DetectedRoom,
    DateTimeOffset? DetectedStartsAt,
    DateTimeOffset? DetectedEndsAt,
    string? ErrorCode,
    string? ErrorMessage,
    long LatencyMs = 0
);

public interface ILlmClient
{
    Task<LlmExtractionResult> ExtractFieldsAsync(
        string userMessage,
        string? currentLetterType,
        Dictionary<string, string> currentFields,
        IReadOnlyList<TemplateField> targetFields,
        CancellationToken ct);
}
