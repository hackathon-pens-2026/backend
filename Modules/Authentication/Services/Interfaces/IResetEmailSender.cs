namespace SignIt.Modules.Authentication.Services;

public sealed record EmailSendResult(bool Accepted, string? ProviderId, string? ErrorCode,
    bool Retryable = false, bool Unknown = false, TimeSpan? RetryAfter = null);

public interface IResetEmailSender
{
    Task<EmailSendResult> SendResetAsync(Guid emailId, string recipient, string token, CancellationToken ct);
}
