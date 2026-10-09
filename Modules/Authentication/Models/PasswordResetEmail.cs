namespace SignIt.Modules.Authentication.Models;

public enum ResetEmailStatus { Queued, QuotaDeferred, Accepted, Failed, Unknown, Cancelled }

public sealed class PasswordResetEmail
{
    private PasswordResetEmail() { }
    public Guid Id { get; private set; }
    public Guid ResetTokenId { get; private set; }
    public string Recipient { get; private set; } = string.Empty;
    public string ProtectedPayload { get; private set; } = string.Empty;
    public ResetEmailStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? FirstAttemptAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? LastErrorCode { get; private set; }
    public Guid Version { get; private set; }

    public static PasswordResetEmail Queue(Guid resetTokenId, string recipient, string protectedPayload,
        DateTimeOffset now) => new()
        {
            Id = Guid.NewGuid(), ResetTokenId = resetTokenId, Recipient = recipient,
            ProtectedPayload = protectedPayload, Status = ResetEmailStatus.Queued,
            CreatedAt = now, NextAttemptAt = now, Version = Guid.NewGuid()
        };

    public void BeginAttempt(DateTimeOffset now, TimeSpan lease)
    {
        Attempts++;
        FirstAttemptAt ??= now;
        // Persist before calling Resend so a crash is retried using the same idempotency key.
        Status = ResetEmailStatus.Unknown;
        NextAttemptAt = now.Add(lease);
        Version = Guid.NewGuid();
    }

    public void Accept(string providerId, DateTimeOffset now)
    {
        Status = ResetEmailStatus.Accepted;
        ProviderMessageId = providerId;
        AcceptedAt = now;
        ProtectedPayload = string.Empty;
        LastErrorCode = null;
        Version = Guid.NewGuid();
    }

    public void Retry(string code, DateTimeOffset nextAttempt, bool unknown)
    {
        Status = unknown ? ResetEmailStatus.Unknown : ResetEmailStatus.Queued;
        LastErrorCode = code;
        NextAttemptAt = nextAttempt;
        Version = Guid.NewGuid();
    }

    public void DeferQuota(DateTimeOffset nextAttempt)
    {
        if (Status != ResetEmailStatus.Unknown) Status = ResetEmailStatus.QuotaDeferred;
        LastErrorCode = "email_quota_deferred";
        NextAttemptAt = nextAttempt;
        Version = Guid.NewGuid();
    }

    public void Stop(string code, bool cancelled = false, bool unknown = false)
    {
        Status = cancelled ? ResetEmailStatus.Cancelled : unknown ? ResetEmailStatus.Unknown : ResetEmailStatus.Failed;
        LastErrorCode = code;
        NextAttemptAt = DateTimeOffset.MaxValue;
        ProtectedPayload = string.Empty;
        Version = Guid.NewGuid();
    }
}
