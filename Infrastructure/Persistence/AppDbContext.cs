using Microsoft.EntityFrameworkCore;
using SignIt.Modules.Authentication.Models;

namespace SignIt.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAssignment> Assignments => Set<UserAssignment>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<RefreshCredential> RefreshCredentials => Set<RefreshCredential>();
    public DbSet<PasswordResetToken> ResetTokens => Set<PasswordResetToken>();
    public DbSet<PasswordResetEmail> ResetEmails => Set<PasswordResetEmail>();
    public DbSet<AuthAudit> Audits => Set<AuthAudit>();
    public DbSet<ResetEmailBudget> EmailBudgets => Set<ResetEmailBudget>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var users = model.Entity<User>();
        users.ToTable("auth_users", t =>
        {
            t.HasCheckConstraint("ck_auth_users_failed_logins", "\"FailedLoginCount\" >= 0");
            t.HasCheckConstraint("ck_auth_users_category", "\"Category\" IN ('StudentGeneral','StudentDagri','BAAK','Management')");
        });
        users.HasKey(x => x.Id);
        users.Property(x => x.Name).HasMaxLength(150);
        users.Property(x => x.Email).HasMaxLength(254);
        users.Property(x => x.NormalizedEmail).HasMaxLength(254);
        users.Property(x => x.NimNip).HasMaxLength(50);
        users.Property(x => x.PasswordHash).HasMaxLength(512);
        users.Property(x => x.Category).HasConversion<string>().HasMaxLength(30);
        users.Property(x => x.Version).IsConcurrencyToken();
        users.Ignore(x => x.Surface);
        users.HasIndex(x => x.NormalizedEmail).IsUnique();

        var assignments = model.Entity<UserAssignment>();
        assignments.ToTable("auth_user_assignments", t =>
        {
            t.HasCheckConstraint("ck_auth_assignment_dates", "\"ValidTo\" IS NULL OR \"ValidTo\" > \"ValidFrom\"");
            t.HasCheckConstraint("ck_auth_assignment_capability", "\"Capability\" IN ('Requester','Signer','Approver','UnitOperator')");
        });
        assignments.HasKey(x => x.Id);
        assignments.Property(x => x.PositionCode).HasMaxLength(80);
        assignments.Property(x => x.PositionName).HasMaxLength(150);
        assignments.Property(x => x.Scope).HasMaxLength(200);
        assignments.Property(x => x.Capability).HasConversion<string>().HasMaxLength(30);
        assignments.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        assignments.HasIndex(x => new { x.UserId, x.IsActive, x.ValidFrom });

        var sessions = model.Entity<AuthSession>();
        sessions.ToTable("auth_sessions", t => t.HasCheckConstraint("ck_auth_session_expiry", "\"ExpiresAt\" > \"CreatedAt\""));
        sessions.HasKey(x => x.Id);
        sessions.Property(x => x.RevocationReason).HasMaxLength(80);
        sessions.Property(x => x.Version).IsConcurrencyToken();
        sessions.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        sessions.HasIndex(x => new { x.UserId, x.RevokedAt, x.ExpiresAt });

        var refresh = model.Entity<RefreshCredential>();
        refresh.ToTable("auth_refresh_credentials", t => t.HasCheckConstraint("ck_auth_refresh_expiry", "\"ExpiresAt\" > \"CreatedAt\""));
        refresh.HasKey(x => x.Id);
        refresh.Property(x => x.TokenHash).HasMaxLength(64);
        refresh.Property(x => x.Version).IsConcurrencyToken();
        refresh.HasIndex(x => x.TokenHash).IsUnique();
        refresh.HasOne<AuthSession>().WithMany().HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Restrict);

        var resets = model.Entity<PasswordResetToken>();
        resets.ToTable("auth_password_reset_tokens", t => t.HasCheckConstraint("ck_auth_reset_expiry", "\"ExpiresAt\" > \"CreatedAt\""));
        resets.HasKey(x => x.Id);
        resets.Property(x => x.TokenHash).HasMaxLength(64);
        resets.Property(x => x.Version).IsConcurrencyToken();
        resets.HasIndex(x => x.TokenHash).IsUnique();
        resets.HasIndex(x => new { x.UserId, x.ConsumedAt });
        resets.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        var emails = model.Entity<PasswordResetEmail>();
        emails.ToTable("auth_password_reset_emails");
        emails.HasKey(x => x.Id);
        emails.Property(x => x.Recipient).HasMaxLength(254);
        emails.Property(x => x.ProtectedPayload).HasMaxLength(4096);
        emails.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        emails.Property(x => x.LastErrorCode).HasMaxLength(100);
        emails.Property(x => x.ProviderMessageId).HasMaxLength(150);
        emails.Property(x => x.Version).IsConcurrencyToken();
        emails.HasIndex(x => x.ResetTokenId).IsUnique();
        emails.HasIndex(x => new { x.Status, x.NextAttemptAt });
        emails.HasOne<PasswordResetToken>().WithOne().HasForeignKey<PasswordResetEmail>(x => x.ResetTokenId).OnDelete(DeleteBehavior.Restrict);

        var audits = model.Entity<AuthAudit>();
        audits.ToTable("auth_audits");
        audits.HasKey(x => x.Id);
        audits.Property(x => x.Action).HasMaxLength(100);
        audits.Property(x => x.IpAddress).HasMaxLength(45);
        audits.Property(x => x.UserAgent).HasMaxLength(512);
        audits.Property(x => x.CorrelationId).HasMaxLength(100);
        audits.HasIndex(x => new { x.UserId, x.AtUtc });
        audits.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        var budgets = model.Entity<ResetEmailBudget>();
        budgets.ToTable("auth_reset_email_budget", t =>
        {
            t.HasCheckConstraint("ck_auth_email_budget_singleton", "\"Id\" = 1");
            t.HasCheckConstraint("ck_auth_email_budget_attempts", "\"DailyAttempts\" >= 0 AND \"MonthlyAttempts\" >= 0");
        });
        budgets.HasKey(x => x.Id);
        budgets.Property(x => x.Id).ValueGeneratedNever();
        budgets.Property(x => x.Version).IsConcurrencyToken();
    }
}
