using SignIt.Modules.Email.Models;

namespace SignIt.Modules.Email.Services;

public interface IEmailEventStore
{
    Task<bool> TryRecordEventAsync(EmailProviderEvent providerEvent,
        IReadOnlyList<EmailSuppression> suppressions, CancellationToken ct);
}
