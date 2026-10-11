using SignIt.Modules.Authentication.DTOs;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Authentication.Services;

public sealed class AuthUserMapper(IAuthStore store) : IAuthUserMapper
{
    public async Task<UserDto> MapUserAsync(User user, DateTimeOffset now, CancellationToken ct)
    {
        var assignments = await store.GetActiveAssignmentsAsync(user.Id, now, ct);
        // Requester is the base capability; approval/unit permissions never come from category.
        var capabilities = assignments.Select(a => a.Capability).Append(UserCapability.Requester).Distinct().Order().ToArray();
        return new UserDto(
            user.Id,
            user.Name,
            user.Email,
            user.NimNip,
            user.IsActive,
            user.EmailVerifiedAt,
            user.Category,
            user.Surface,
            capabilities,
            assignments.Select(a => new AssignmentDto(
                a.Id,
                a.PositionCode,
                a.PositionName,
                a.Scope,
                a.Capability,
                a.ValidFrom,
                a.ValidTo)).ToArray());
    }
}
