using System.Security.Claims;
using SignIt.Modules.Authentication.DTOs;

namespace SignIt.Modules.Authentication.Services;

public static class AuthContextExtensions
{
    public static Guid GetUserId(this ClaimsPrincipal principal) => GetGuidClaim(principal, "sub");
    public static Guid GetSessionId(this ClaimsPrincipal principal) => GetGuidClaim(principal, "sid");

    private static Guid GetGuidClaim(ClaimsPrincipal principal, string name)
        => Guid.TryParse(principal.FindFirstValue(name), out var id) && id != Guid.Empty ? id
            : throw new AuthException("unauthorized", "Silakan login kembali.", AuthErrorKind.Unauthorized);

    public static RequestMetadata GetAuthMetadata(this HttpContext context)
    {
        var agent = context.Request.Headers.UserAgent.ToString();
        return new RequestMetadata(context.Connection.RemoteIpAddress?.ToString(),
            agent.Length > 512 ? agent[..512] : agent,
            context.TraceIdentifier.Length > 100 ? context.TraceIdentifier[..100] : context.TraceIdentifier);
    }
}
