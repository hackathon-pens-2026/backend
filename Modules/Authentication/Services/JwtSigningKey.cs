using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;

namespace SignIt.Modules.Authentication.Services;

public sealed class JwtSigningKey : IDisposable
{
    private readonly RSA rsa = RSA.Create();
    public RsaSecurityKey Key { get; }

    public JwtSigningKey(JwtOptions options)
    {
        try
        {
            rsa.ImportFromPem(options.PrivateKeyPem);
            if (rsa.KeySize < 2048) throw new CryptographicException();
            // Reject a public-only key; issuing tokens needs private material.
            rsa.ExportParameters(true);
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            rsa.Dispose();
            throw new InvalidOperationException("Jwt:PrivateKeyPem harus berupa private key RSA minimal 2048-bit.");
        }
        Key = new RsaSecurityKey(rsa) { KeyId = options.KeyId };
    }

    public void Dispose() => rsa.Dispose();

    public static bool CanImportPrivateKey(string pem)
    {
        if (string.IsNullOrWhiteSpace(pem)) return false;
        using var key = RSA.Create();
        try
        {
            key.ImportFromPem(pem);
            if (key.KeySize < 2048) return false;
            key.SignData([0], HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException) { return false; }
    }
}
