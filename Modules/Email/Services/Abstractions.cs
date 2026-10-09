using SignIt.Modules.Authentication.Services;
using SignIt.Modules.Email.Models;

namespace SignIt.Modules.Email.Services;

public enum WebhookVerificationResult { Valid, Invalid, NotConfigured }

public interface IEmailWebhookVerifier
{
    WebhookVerificationResult Verify(string eventId, string timestamp, string signature, string payload);
}

// Records a provider event and any suppressions atomically; duplicate events are acknowledged
// without reapplying their effects.
public interface IEmailEventStore
{
    Task<bool> TryRecordEventAsync(EmailProviderEvent providerEvent,
        IReadOnlyList<EmailSuppression> suppressions, CancellationToken ct);
}

// Generic transactional email sender used by the workflow outbox.
public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(Guid deliveryId, string recipient, string subject, string text,
        string html, CancellationToken ct);
}
