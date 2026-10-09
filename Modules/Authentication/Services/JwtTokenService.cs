using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.IdentityModel.Tokens;
using SignIt.Modules.Authentication.DTOs;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Modules.Authentication.Services;

public sealed class JwtTokenService(JwtOptions jwt, AuthOptions auth, JwtSigningKey signingKey) : ITokenService
{
    public string CreateOpaqueToken() => Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public string HashOpaqueToken(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public AccessToken CreateAccessToken(User user, AuthSession session, DateTimeOffset now)
    {
        var expiresAt = now.AddMinutes(auth.AccessTokenMinutes);
        if (expiresAt > session.ExpiresAt) expiresAt = session.ExpiresAt;
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString()),
            new Claim(JwtRegisteredClaimNames.Iat, now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture), ClaimValueTypes.Integer64),
            new Claim("sid", session.Id.ToString()),
            new Claim("sst", user.SecurityStamp.ToString())
        };
        // No role/category/capability claims: authorization must read current assignments and tasks.
        var token = new JwtSecurityToken(jwt.Issuer, jwt.Audience, claims, now.UtcDateTime,
            expiresAt.UtcDateTime, new SigningCredentials(signingKey.Key, SecurityAlgorithms.RsaSha256));
        return new AccessToken(new JwtSecurityTokenHandler().WriteToken(token), expiresAt);
    }
}
