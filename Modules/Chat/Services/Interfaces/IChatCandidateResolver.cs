using SignIt.Modules.Chat.DTOs;

namespace SignIt.Modules.Chat.Services;

public interface IChatCandidateResolver
{
    Task<IReadOnlyList<CandidatePersonDto>> GetSignerCandidatesAsync(string[] positionCodes, CancellationToken ct);
    Task<RoomOptionDto?> FindMatchingRoomAsync(string? query, CancellationToken ct);
}
