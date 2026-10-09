using System.Security.Cryptography;
using System.Text;
using SignIt.Infrastructure.Email;
using SignIt.Modules.Email.Services;
using Xunit;

namespace SignIt.Email.Tests;

public sealed class ResendWebhookVerifierTests
{
    private static readonly byte[] SecretBytes = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly string Secret = "whsec_" + Convert.ToBase64String(SecretBytes);
    private static readonly string EventId = "msg_test_1";
    private static readonly long Timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private const string Payload = "{\"type\":\"email.delivered\",\"data\":{\"email_id\":\"mail_1\"}}";

    private static ResendWebhookVerifier CreateVerifier(string? secret = null)
        => new(new ResendOptions { WebhookSecret = secret ?? Secret }, TimeProvider.System);

    private static string Sign(string payload, string eventId = "msg_test_1", long? timestamp = null,
        byte[]? key = null)
    {
        using var hmac = new HMACSHA256(key ?? SecretBytes);
        var signed = $"{eventId}.{timestamp ?? Timestamp}.{payload}";
        return "v1," + Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(signed)));
    }

    [Fact]
    public void ValidSignature_IsAccepted()
    {
        var result = CreateVerifier().Verify(EventId, Timestamp.ToString(), Sign(Payload), Payload);
        Assert.Equal(WebhookVerificationResult.Valid, result);
    }

    [Fact]
    public void TamperedPayload_IsRejected()
    {
        var signature = Sign(Payload);
        var result = CreateVerifier().Verify(EventId, Timestamp.ToString(), signature,
            Payload.Replace("delivered", "bounced"));
        Assert.Equal(WebhookVerificationResult.Invalid, result);
    }

    [Fact]
    public void ExpiredTimestamp_IsRejected()
    {
        var oldTimestamp = Timestamp - 600;
        var signature = Sign(Payload, timestamp: oldTimestamp);
        var result = CreateVerifier().Verify(EventId, oldTimestamp.ToString(), signature, Payload);
        Assert.Equal(WebhookVerificationResult.Invalid, result);
    }

    [Fact]
    public void SignatureFromDifferentSecret_IsRejected()
    {
        var otherKey = Enumerable.Repeat((byte)9, 32).ToArray();
        var signature = Sign(Payload, key: otherKey);
        var result = CreateVerifier().Verify(EventId, Timestamp.ToString(), signature, Payload);
        Assert.Equal(WebhookVerificationResult.Invalid, result);
    }

    [Fact]
    public void OneOfMultipleSignaturesMatching_IsAccepted()
    {
        var header = Sign(Payload, key: Enumerable.Repeat((byte)7, 32).ToArray()) + " " + Sign(Payload);
        var result = CreateVerifier().Verify(EventId, Timestamp.ToString(), header, Payload);
        Assert.Equal(WebhookVerificationResult.Valid, result);
    }

    [Fact]
    public void MissingSecret_ReportsNotConfigured()
    {
        var result = CreateVerifier(secret: string.Empty).Verify(EventId, Timestamp.ToString(), Sign(Payload), Payload);
        Assert.Equal(WebhookVerificationResult.NotConfigured, result);
    }
}
