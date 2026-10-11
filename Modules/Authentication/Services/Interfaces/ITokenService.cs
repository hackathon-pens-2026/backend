using SignIt.Modules.Authentication.DTOs;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Authentication.Services;

public interface ITokenService
{
    string CreateOpaqueToken();
    string HashOpaqueToken(string token);
    AccessToken CreateAccessToken(User user, AuthSession session, DateTimeOffset now);
}
