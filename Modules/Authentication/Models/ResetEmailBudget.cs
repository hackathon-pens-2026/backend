namespace SignIt.Modules.Authentication.Models;

public sealed class ResetEmailBudget
{
    private ResetEmailBudget() { }
    public int Id { get; private set; }
    public DateOnly Day { get; private set; }
    public int Month { get; private set; }
    public int DailyAttempts { get; private set; }
    public int MonthlyAttempts { get; private set; }
    public Guid Version { get; private set; }

    public static ResetEmailBudget Create() => new() { Id = 1, Version = Guid.NewGuid() };

    // Reserve conservatively before every HTTP attempt, including an idempotent retry.
    // Provider quotas remain authoritative if other services use the same Resend account.
    public DateTimeOffset? Reserve(DateTimeOffset now, int dailyLimit, int monthlyLimit)
    {
        var utc = now.UtcDateTime;
        var day = DateOnly.FromDateTime(utc);
        var month = utc.Year * 100 + utc.Month;
        if (Day != day) { Day = day; DailyAttempts = 0; }
        if (Month != month) { Month = month; MonthlyAttempts = 0; }
        Version = Guid.NewGuid();
        if (MonthlyAttempts >= monthlyLimit)
            return new DateTimeOffset(new DateTime(utc.Year, utc.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1));
        if (DailyAttempts >= dailyLimit)
            return new DateTimeOffset(utc.Date.AddDays(1));
        DailyAttempts++;
        MonthlyAttempts++;
        return null;
    }
}
