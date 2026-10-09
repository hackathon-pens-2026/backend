namespace SignIt.Modules.Authentication.Models;

public sealed class User
{
    private User() { }

    public Guid Id { get; private set; }
    public string Name { get; private set; } = string.Empty;
    public string Email { get; private set; } = string.Empty;
    public string NormalizedEmail { get; private set; } = string.Empty;
    public string? NimNip { get; private set; }
    public UserCategory Category { get; private set; }
    public bool IsActive { get; private set; }
    public DateTimeOffset? EmailVerifiedAt { get; private set; }
    public string PasswordHash { get; private set; } = string.Empty;
    public Guid SecurityStamp { get; private set; }
    public Guid Version { get; private set; }
    public int FailedLoginCount { get; private set; }
    public DateTimeOffset? LockedUntil { get; private set; }
    public DateTimeOffset? LastLoginAt { get; private set; }
    public DateTimeOffset? LastPasswordResetRequestedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public UiSurface Surface => Category is UserCategory.StudentGeneral or UserCategory.StudentDagri
        ? UiSurface.Student : UiSurface.Management;

    public static string NormalizeEmail(string email) => email.Trim().ToUpperInvariant();

    public static User Provision(Guid id, string name, string email, string? nimNip,
        UserCategory category, string passwordHash, DateTimeOffset now, bool emailVerified)
    {
        if (id == Guid.Empty || string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(email)
            || string.IsNullOrWhiteSpace(passwordHash) || !Enum.IsDefined(category))
            throw new ArgumentException("Data provisioning akun tidak valid.");

        return new User
        {
            Id = id, Name = name.Trim(), Email = email.Trim(), NormalizedEmail = NormalizeEmail(email),
            NimNip = nimNip, Category = category, PasswordHash = passwordHash, IsActive = true,
            CreatedAt = now, EmailVerifiedAt = emailVerified ? now : null,
            SecurityStamp = Guid.NewGuid(), Version = Guid.NewGuid()
        };
    }

    public bool CanLogin(DateTimeOffset now) => IsActive && (LockedUntil is null || LockedUntil <= now);

    public void RecordFailedLogin(DateTimeOffset now, int limit, TimeSpan duration)
    {
        if (!CanLogin(now)) return;
        if (LockedUntil is not null) FailedLoginCount = 0;
        LockedUntil = null;
        FailedLoginCount++;
        if (FailedLoginCount >= limit)
        {
            LockedUntil = now.Add(duration);
            FailedLoginCount = 0;
        }
        Version = Guid.NewGuid();
    }

    public void RecordSuccessfulLogin(DateTimeOffset now, string? upgradedHash)
    {
        if (!CanLogin(now)) throw new InvalidOperationException("Akun tidak dapat login.");
        FailedLoginCount = 0;
        LockedUntil = null;
        LastLoginAt = now;
        if (upgradedHash is not null) PasswordHash = upgradedHash;
        Version = Guid.NewGuid();
    }

    public void RequestPasswordReset(DateTimeOffset now)
    {
        LastPasswordResetRequestedAt = now;
        Version = Guid.NewGuid();
    }

    public void ResetPassword(string hash)
    {
        if (string.IsNullOrWhiteSpace(hash)) throw new ArgumentException("Hash password wajib diisi.");
        PasswordHash = hash;
        SecurityStamp = Guid.NewGuid();
        FailedLoginCount = 0;
        LockedUntil = null;
        Version = Guid.NewGuid();
    }
}
