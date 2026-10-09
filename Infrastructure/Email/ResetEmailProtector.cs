using Microsoft.AspNetCore.DataProtection;
using SignIt.Modules.Authentication.Services;

namespace SignIt.Infrastructure.Email;

public sealed class ResetEmailProtector(IDataProtectionProvider provider) : IResetEmailProtector
{
    private readonly IDataProtector protector = provider.CreateProtector("SignIt.Auth.PasswordResetEmail.v1");
    public string Protect(string rawToken) => protector.Protect(rawToken);
    public string Unprotect(string protectedToken) => protector.Unprotect(protectedToken);
}
