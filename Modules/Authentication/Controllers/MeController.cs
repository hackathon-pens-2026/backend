using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Authentication.DTOs;

namespace SignIt.Modules.Authentication.Controllers;

[Authorize]
[ApiController]
[Route("api/v1/me")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[Produces("application/json")]
public sealed class MeController(IAuthService auth) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType<UserDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<UserDto>> GetMe(CancellationToken ct)
        => Ok(await auth.GetMeAsync(User.GetUserId(), ct));

    [HttpGet("capabilities")]
    [ProducesResponseType<CapabilitiesDto>(StatusCodes.Status200OK)]
    public async Task<ActionResult<CapabilitiesDto>> GetCapabilities(CancellationToken ct)
    {
        var user = await auth.GetMeAsync(User.GetUserId(), ct);
        return Ok(new CapabilitiesDto(user.UserCategory, user.UiSurface, user.Capabilities, user.Assignments));
    }
}
