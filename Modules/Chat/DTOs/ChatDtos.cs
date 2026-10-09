using SignIt.Modules.Letters.Services;
using SignIt.Modules.Rooms.Services;

namespace SignIt.Modules.Chat.DTOs;

public sealed record CreateSessionRequest(Guid? LetterRequestId, string? TypeId);

public sealed record SendChatMessageRequest(string? Text, Dictionary<string, string>? DirectFieldUpdates);

public sealed record ChatMessageDto(long Id, string From, string Text, string? Widget, DateTimeOffset CreatedAt);

public sealed record FieldSummaryDto(int TotalRequired, int FilledRequired, IReadOnlyList<string> MissingRequiredKeys);

public sealed record CandidatePersonDto(Guid UserId, string Name, string PositionCode, string PositionName, string? Meta = null);

public sealed record RoomOptionDto(Guid Id, string Code, int? Floor, Guid FacilityId, string FacilityName);

public sealed record ChatTurnResponseDto(
    Guid SessionId,
    Guid? LetterRequestId,
    string? TypeId,
    ChatMessageDto Reply,
    Dictionary<string, string> Fields,
    FieldSummaryDto FieldSummary,
    string? SuggestedWidget,
    IReadOnlyList<CandidatePersonDto>? Candidates,
    AvailabilityDto? Availability,
    DraftDto? Draft,
    string Status,
    bool FallbackAvailable
);

public sealed record ChatSessionDetailDto(
    Guid SessionId,
    Guid? LetterRequestId,
    string? TypeId,
    string Status,
    IReadOnlyList<ChatMessageDto> Messages,
    Dictionary<string, string> Fields,
    FieldSummaryDto FieldSummary,
    string? SuggestedWidget,
    DraftDto? Draft
);
