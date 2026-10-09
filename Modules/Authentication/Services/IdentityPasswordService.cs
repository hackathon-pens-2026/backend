using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace SignIt.Modules.Authentication.Services;

public sealed class IdentityPasswordService : IPasswordService
{
    private readonly PasswordHasher<object> hasher;
    private readonly object subject = new();
    private readonly string dummyHash;

    public IdentityPasswordService(AuthOptions options)
    {
        hasher = new PasswordHasher<object>(Options.Create(new PasswordHasherOptions
        {
            CompatibilityMode = PasswordHasherCompatibilityMode.IdentityV3,
            IterationCount = options.PasswordHashIterations
        }));
        dummyHash = Hash(Guid.NewGuid().ToString("N"));
    }

    public string Hash(string password) => hasher.HashPassword(subject, password);

    public PasswordCheck Verify(string hash, string password)
        => hasher.VerifyHashedPassword(subject, hash, password) switch
        {
            PasswordVerificationResult.Success => PasswordCheck.Success,
            PasswordVerificationResult.SuccessRehashNeeded => PasswordCheck.RehashNeeded,
            _ => PasswordCheck.Failed
        };

    public void VerifyDummy(string password) => Verify(dummyHash, password);
}
