namespace SignIt.Modules.Authentication.Services;

public sealed class AuthOptions
{
    public int AccessTokenMinutes { get; set; } = 10;
    public int SessionDays { get; set; } = 7;
    public int MaxFailedLoginAttempts { get; set; } = 5;
    public int LockoutMinutes { get; set; } = 15;
    public int ResetTokenMinutes { get; set; } = 30;
    public int ResetCooldownSeconds { get; set; } = 60;
    public int MinimumPasswordLength { get; set; } = 12;
    public int PasswordHashIterations { get; set; } = 210000;
    public int RateLimitPermitCount { get; set; } = 10;
    public int RateLimitWindowSeconds { get; set; } = 60;
    public int MaxActiveSessions { get; set; } = 10;
}
