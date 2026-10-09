namespace SignIt.Modules.Email.Models;

public enum EmailDeliveryStatus { Queued, QuotaDeferred, Accepted, Failed, Unknown, Cancelled, Suppressed }

public sealed class EmailDelivery
{
    private EmailDelivery() { }

    public Guid Id { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string DeduplicationKey { get; private set; } = string.Empty;
    public Guid? RecipientUserId { get; private set; }
    public string RecipientEmailSnapshot { get; private set; } = string.Empty;
    public string TemplateVersion { get; private set; } = "v1";
    public string Subject { get; private set; } = string.Empty;
    public string BodyText { get; private set; } = string.Empty;
    public string BodyHtml { get; private set; } = string.Empty;
    public Guid? LetterRequestId { get; private set; }
    public Guid? RelatedTaskId { get; private set; }
    public bool IsReminder { get; private set; }
    public EmailDeliveryStatus Status { get; private set; }
    public int Attempts { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset NextAttemptAt { get; private set; }
    public DateTimeOffset? FirstAttemptAt { get; private set; }
    public DateTimeOffset? AcceptedAt { get; private set; }
    public string? ProviderMessageId { get; private set; }
    public string? LastErrorCode { get; private set; }
    public Guid Version { get; private set; }

    public static EmailDelivery Queue(Guid id, string eventType, string deduplicationKey,
        Guid? recipientUserId, string recipientEmail, string subject, string bodyText, string bodyHtml,
        DateTimeOffset now, Guid? letterRequestId = null, Guid? relatedTaskId = null, bool isReminder = false)
    {
        if (string.IsNullOrWhiteSpace(eventType) || string.IsNullOrWhiteSpace(deduplicationKey)
            || string.IsNullOrWhiteSpace(recipientEmail) || string.IsNullOrWhiteSpace(subject))
            throw new ArgumentException("Data email delivery tidak valid.");
        return new EmailDelivery
        {
            Id = id,
            EventType = eventType.Trim(),
            DeduplicationKey = deduplicationKey.Trim(),
            RecipientUserId = recipientUserId,
            RecipientEmailSnapshot = recipientEmail.Trim(),
            Subject = subject,
            BodyText = bodyText,
            BodyHtml = bodyHtml,
            LetterRequestId = letterRequestId,
            RelatedTaskId = relatedTaskId,
            IsReminder = isReminder,
            Status = EmailDeliveryStatus.Queued,
            CreatedAt = now.ToUniversalTime(),
            NextAttemptAt = now.ToUniversalTime(),
            Version = Guid.NewGuid()
        };
    }

    public void BeginAttempt(DateTimeOffset now, TimeSpan lease)
    {
        Attempts++;
        FirstAttemptAt ??= now.ToUniversalTime();
        // Persist sebelum panggilan provider agar crash di-retry dengan idempotency key yang sama.
        Status = EmailDeliveryStatus.Unknown;
        NextAttemptAt = now.Add(lease).ToUniversalTime();
        Version = Guid.NewGuid();
    }

    public void Accept(string providerId, DateTimeOffset now)
    {
        Status = EmailDeliveryStatus.Accepted;
        ProviderMessageId = providerId;
        AcceptedAt = now.ToUniversalTime();
        LastErrorCode = null;
        Version = Guid.NewGuid();
    }

    public void Retry(string code, DateTimeOffset nextAttempt, bool unknown)
    {
        Status = unknown ? EmailDeliveryStatus.Unknown : EmailDeliveryStatus.Queued;
        LastErrorCode = code;
        NextAttemptAt = nextAttempt.ToUniversalTime();
        Version = Guid.NewGuid();
    }

    public void DeferQuota(DateTimeOffset nextAttempt)
    {
        if (Status != EmailDeliveryStatus.Unknown) Status = EmailDeliveryStatus.QuotaDeferred;
        LastErrorCode = "email_quota_deferred";
        NextAttemptAt = nextAttempt.ToUniversalTime();
        Version = Guid.NewGuid();
    }

    public void Stop(string code, bool cancelled = false, bool suppressed = false, bool unknown = false)
    {
        Status = cancelled ? EmailDeliveryStatus.Cancelled
            : suppressed ? EmailDeliveryStatus.Suppressed
            : unknown ? EmailDeliveryStatus.Unknown
            : EmailDeliveryStatus.Failed;
        LastErrorCode = code;
        NextAttemptAt = DateTimeOffset.MaxValue;
        Version = Guid.NewGuid();
    }

    // Reminder wajib berhenti saat tugas tidak lagi aktif.
    public void CancelIfPending(string code)
    {
        if (Status is EmailDeliveryStatus.Queued or EmailDeliveryStatus.QuotaDeferred or EmailDeliveryStatus.Unknown)
        {
            Status = EmailDeliveryStatus.Cancelled;
            LastErrorCode = code;
            NextAttemptAt = DateTimeOffset.MaxValue;
            Version = Guid.NewGuid();
        }
    }
}
