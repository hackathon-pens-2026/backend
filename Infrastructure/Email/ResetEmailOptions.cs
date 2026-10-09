namespace SignIt.Infrastructure.Email;

public sealed class ResetEmailOptions
{
    public bool WorkerEnabled { get; set; }
    public bool SandboxMode { get; set; } = true;
    public string[] RecipientAllowlist { get; set; } = [];
    public string From { get; set; } = string.Empty;
    public string ReplyTo { get; set; } = string.Empty;
    public string ResetPasswordUrl { get; set; } = "https://app.signit.example/reset-password";
    public int PollSeconds { get; set; } = 10;
    public int MaxAttempts { get; set; } = 5;
    public int HttpTimeoutSeconds { get; set; } = 30;
    public int DailyBudget { get; set; } = 100;
    public int MonthlyBudget { get; set; } = 3000;
}

public sealed class ResendOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string WebhookSecret { get; set; } = string.Empty;
    public int WebhookToleranceSeconds { get; set; } = 300;
}

public sealed class DataProtectionOptions
{
    public string KeyDirectory { get; set; } = ".data/keys";
}
