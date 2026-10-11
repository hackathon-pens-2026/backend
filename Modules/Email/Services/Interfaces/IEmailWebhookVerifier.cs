namespace SignIt.Modules.Email.Services;

public enum WebhookVerificationResult { Valid, Invalid, NotConfigured }

public interface IEmailWebhookVerifier
{
    WebhookVerificationResult Verify(string eventId, string timestamp, string signature, string payload);
}
