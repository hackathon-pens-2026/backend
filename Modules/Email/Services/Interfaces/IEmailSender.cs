using SignIt.Modules.Authentication.Services;

namespace SignIt.Modules.Email.Services;

public interface IEmailSender
{
    Task<EmailSendResult> SendAsync(Guid deliveryId, string recipient, string subject, string text,
        string html, CancellationToken ct);
}
