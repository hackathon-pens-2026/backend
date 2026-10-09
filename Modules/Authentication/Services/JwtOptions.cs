namespace SignIt.Modules.Authentication.Services;

public sealed class JwtOptions
{
    public string Issuer { get; set; } = "SignIt.Api";
    public string Audience { get; set; } = "SignIt.Bff";
    public string KeyId { get; set; } = "signit-auth-v1";
    public string PrivateKeyPem { get; set; } = string.Empty;
}
