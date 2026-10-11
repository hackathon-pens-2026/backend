using SignIt.Modules.Authentication.DTOs;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Authentication.Services;

public interface IAuthUserMapper
{
    Task<UserDto> MapUserAsync(User user, DateTimeOffset now, CancellationToken ct);
}
