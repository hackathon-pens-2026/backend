namespace SignIt.Modules.Email.Models;

public sealed class EmailSuppression
{
    private EmailSuppression() { }

    public Guid Id { get; private set; }
    public string Email { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string Source { get; private set; } = string.Empty;
    public DateTimeOffset SuppressedAt { get; private set; }
    public bool IsActive { get; private set; }

    public static string Normalize(string email) => email.Trim().ToLowerInvariant();

    public static EmailSuppression Create(string email, string reason, string source, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(reason)
            || string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("Data suppression email tidak valid.");
        return new EmailSuppression
        {
            Id = Guid.NewGuid(), Email = Normalize(email), Reason = reason.Trim(),
            Source = source.Trim(), SuppressedAt = now.ToUniversalTime(), IsActive = true
        };
    }

    public void Refresh(string reason, string source, DateTimeOffset now)
    {
        Reason = reason.Trim();
        Source = source.Trim();
        SuppressedAt = now.ToUniversalTime();
        IsActive = true;
    }
}
