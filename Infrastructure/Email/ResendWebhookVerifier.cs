using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SignIt.Modules.Email.Services;

namespace SignIt.Infrastructure.Email;

// Resend delivers Svix-signed webhooks: HMAC-SHA256 over "{svix-id}.{svix-timestamp}.{body}".
public sealed class ResendWebhookVerifier(ResendOptions options, TimeProvider clock) : IEmailWebhookVerifier
{
    private const string SecretPrefix = "whsec_";

    public WebhookVerificationResult Verify(string eventId, string timestamp, string signature, string payload)
    {
        if (string.IsNullOrWhiteSpace(options.WebhookSecret)) return WebhookVerificationResult.NotConfigured;
        if (string.IsNullOrWhiteSpace(eventId) || string.IsNullOrWhiteSpace(timestamp)
            || string.IsNullOrWhiteSpace(signature)) return WebhookVerificationResult.Invalid;
        if (!options.WebhookSecret.StartsWith(SecretPrefix, StringComparison.Ordinal)
            || !long.TryParse(timestamp, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unixSeconds))
            return WebhookVerificationResult.Invalid;
        if ((clock.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(unixSeconds)).Duration()
            > TimeSpan.FromSeconds(options.WebhookToleranceSeconds))
            return WebhookVerificationResult.Invalid;

        byte[] secret;
        try { secret = Convert.FromBase64String(options.WebhookSecret[SecretPrefix.Length..]); }
        catch (FormatException) { return WebhookVerificationResult.Invalid; }

        using var hmac = new HMACSHA256(secret);
        var expected = hmac.ComputeHash(Encoding.UTF8.GetBytes($"{eventId}.{timestamp}.{payload}"));

        // The header may list multiple space-separated version,signature pairs.
        foreach (var candidate in signature.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = candidate.IndexOf(',');
            if (separator <= 0 || candidate[..separator] != "v1") continue;
            byte[] provided;
            try { provided = Convert.FromBase64String(candidate[(separator + 1)..]); }
            catch (FormatException) { continue; }
            if (CryptographicOperations.FixedTimeEquals(expected, provided)) return WebhookVerificationResult.Valid;
        }
        return WebhookVerificationResult.Invalid;
    }
}
