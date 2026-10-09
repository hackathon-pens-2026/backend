using Microsoft.EntityFrameworkCore;
using SignIt.Modules.Authentication.Models;
using SignIt.Modules.Email.Models;
using SignIt.Modules.Letters.Models;
using SignIt.Modules.Signatures.Models;
using SignIt.Modules.Workflow.Models;

namespace SignIt.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<SignIt.Modules.Routing.Models.Organization> Organizations => Set<SignIt.Modules.Routing.Models.Organization>();
    public DbSet<SignIt.Modules.Routing.Models.Facility> Facilities => Set<SignIt.Modules.Routing.Models.Facility>();
    public DbSet<SignIt.Modules.Routing.Models.FacilityResource> FacilityResources => Set<SignIt.Modules.Routing.Models.FacilityResource>();
    public DbSet<User> Users => Set<User>();
    public DbSet<UserAssignment> Assignments => Set<UserAssignment>();
    public DbSet<AuthSession> Sessions => Set<AuthSession>();
    public DbSet<RefreshCredential> RefreshCredentials => Set<RefreshCredential>();
    public DbSet<PasswordResetToken> ResetTokens => Set<PasswordResetToken>();
    public DbSet<PasswordResetEmail> ResetEmails => Set<PasswordResetEmail>();
    public DbSet<AuthAudit> Audits => Set<AuthAudit>();
    public DbSet<ResetEmailBudget> EmailBudgets => Set<ResetEmailBudget>();
    public DbSet<EmailProviderEvent> EmailProviderEvents => Set<EmailProviderEvent>();
    public DbSet<EmailSuppression> EmailSuppressions => Set<EmailSuppression>();

    public DbSet<UserSignatureQr> SignatureQrs => Set<UserSignatureQr>();
    public DbSet<SignatureEvidence> SignatureEvidences => Set<SignatureEvidence>();
    public DbSet<SigningAttempt> SigningAttempts => Set<SigningAttempt>();
    public DbSet<VerificationRecord> VerificationRecords => Set<VerificationRecord>();
    public DbSet<LetterRequest> LetterRequests => Set<LetterRequest>();
    public DbSet<LetterRevision> LetterRevisions => Set<LetterRevision>();
    public DbSet<LetterParticipant> LetterParticipants => Set<LetterParticipant>();
    public DbSet<WorkflowTask> WorkflowTasks => Set<WorkflowTask>();
    public DbSet<Delegation> Delegations => Set<Delegation>();
    public DbSet<Document> Documents => Set<Document>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    protected override void OnModelCreating(ModelBuilder model)
    {
        var organizations = model.Entity<SignIt.Modules.Routing.Models.Organization>();
        organizations.ToTable("organizations", t => t.HasCheckConstraint("ck_organization_kind", "\"Kind\" IN ('Himpunan','Organisasi')"));
        organizations.HasKey(x => x.Id);
        organizations.Property(x => x.Scope).HasMaxLength(200);
        organizations.Property(x => x.Name).HasMaxLength(200);
        organizations.Property(x => x.Kind).HasMaxLength(30);
        organizations.HasIndex(x => x.Scope).IsUnique();
        var facilities = model.Entity<SignIt.Modules.Routing.Models.Facility>();
        facilities.ToTable("facilities");
        facilities.HasKey(x => x.Id);
        facilities.Property(x => x.Code).HasMaxLength(80);
        facilities.Property(x => x.Name).HasMaxLength(200);
        facilities.HasIndex(x => x.Code).IsUnique();
        var resources = model.Entity<SignIt.Modules.Routing.Models.FacilityResource>();
        resources.ToTable("facility_resources", t => t.HasCheckConstraint("ck_resource_floor", "\"Floor\" IS NULL OR \"Floor\" >= 0"));
        resources.HasKey(x => x.Id);
        resources.Property(x => x.Code).HasMaxLength(80);
        resources.HasIndex(x => new { x.FacilityId, x.Code }).IsUnique();
        resources.HasOne<SignIt.Modules.Routing.Models.Facility>().WithMany().HasForeignKey(x => x.FacilityId).OnDelete(DeleteBehavior.Restrict);
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

        var providerEvents = model.Entity<EmailProviderEvent>();
        providerEvents.ToTable("email_provider_events");
        providerEvents.HasKey(x => x.Id);
        providerEvents.Property(x => x.Provider).HasMaxLength(30);
        providerEvents.Property(x => x.EventId).HasMaxLength(150);
        providerEvents.Property(x => x.EventType).HasMaxLength(80);
        providerEvents.Property(x => x.ProviderMessageId).HasMaxLength(150);
        providerEvents.HasIndex(x => new { x.Provider, x.EventId }).IsUnique();
        providerEvents.HasIndex(x => x.ProviderMessageId);

        var suppressions = model.Entity<EmailSuppression>();
        suppressions.ToTable("email_suppressions");
        suppressions.HasKey(x => x.Id);
        suppressions.Property(x => x.Email).HasMaxLength(254);
        suppressions.Property(x => x.Reason).HasMaxLength(80);
        suppressions.Property(x => x.Source).HasMaxLength(30);
        suppressions.HasIndex(x => x.Email).IsUnique();

        var qrs = model.Entity<UserSignatureQr>();
        qrs.ToTable("sig_user_qrs", t =>
        {
            t.HasCheckConstraint("ck_sig_qr_status", "\"Status\" IN ('Active','Rotated','Revoked')");
            t.HasCheckConstraint("ck_sig_qr_version", "\"Version\" >= 1");
        });
        qrs.HasKey(x => x.Id);
        qrs.Property(x => x.OpaqueCode).HasMaxLength(100);
        qrs.Property(x => x.PrivateStorageKey).HasMaxLength(260);
        qrs.Property(x => x.ImageSha256).HasMaxLength(64);
        qrs.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        qrs.HasIndex(x => x.OpaqueCode).IsUnique();
        qrs.HasIndex(x => new { x.OwnerUserId, x.Version }).IsUnique();
        qrs.HasIndex(x => x.OwnerUserId).HasFilter("\"Status\" = 'Active'").IsUnique();
        qrs.HasOne<User>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);

        var evidences = model.Entity<SignatureEvidence>();
        evidences.ToTable("sig_evidences");
        evidences.HasKey(x => x.Id);
        evidences.Property(x => x.Role).HasMaxLength(100);
        evidences.Property(x => x.PositionSnapshot).HasMaxLength(150);
        evidences.Property(x => x.ContentHash).HasMaxLength(64);
        evidences.Property(x => x.QrHash).HasMaxLength(64);
        evidences.Property(x => x.MandateDescription).HasMaxLength(200);
        evidences.Property(x => x.IpAddress).HasMaxLength(45);
        evidences.Property(x => x.UserAgent).HasMaxLength(512);
        evidences.HasIndex(x => x.TaskId).IsUnique();
        evidences.HasIndex(x => x.RevisionId);
        evidences.HasIndex(x => x.ActorId);
        evidences.HasOne<User>().WithMany().HasForeignKey(x => x.ActorId).OnDelete(DeleteBehavior.Restrict);
        evidences.HasOne<UserSignatureQr>().WithMany().HasForeignKey(x => x.QrAssetId).OnDelete(DeleteBehavior.Restrict);

        var attempts = model.Entity<SigningAttempt>();
        attempts.ToTable("sig_signing_attempts");
        attempts.HasKey(x => x.Id);
        attempts.Property(x => x.QrHash).HasMaxLength(64);
        attempts.Property(x => x.ContentHash).HasMaxLength(64);
        attempts.Property(x => x.Status).HasMaxLength(30);
        attempts.Property(x => x.IdempotencyKey).HasMaxLength(100);
        attempts.HasIndex(x => new { x.TaskId, x.ActorId, x.IdempotencyKey });

        var verifications = model.Entity<VerificationRecord>();
        verifications.ToTable("sig_verification_records", t =>
        {
            t.HasCheckConstraint("ck_sig_verification_status", "\"Status\" IN ('Valid','Revoked')");
        });
        verifications.HasKey(x => x.Id);
        verifications.Property(x => x.RandomCode).HasMaxLength(50);
        verifications.Property(x => x.FinalHash).HasMaxLength(64);
        verifications.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        verifications.Property(x => x.RevocationReason).HasMaxLength(500);
        verifications.HasIndex(x => x.RandomCode).IsUnique();
        verifications.HasIndex(x => x.FinalHash);

        var letterRequests = model.Entity<LetterRequest>();
        letterRequests.ToTable("letter_requests", t =>
        {
            t.HasCheckConstraint("ck_letter_request_status", "\"Status\" IN ('Draft','InProgress','NeedsRevision','AwaitingResourceResolution','Finalizing','ProcessingFailed','Completed','Rejected','Cancelled','Revoked')");
        });
        letterRequests.HasKey(x => x.Id);
        letterRequests.Property(x => x.Number).HasMaxLength(80);
        letterRequests.Property(x => x.TypeId).HasMaxLength(80);
        letterRequests.Property(x => x.Title).HasMaxLength(300);
        letterRequests.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        letterRequests.Property(x => x.RowVersion).IsConcurrencyToken();
        letterRequests.HasIndex(x => x.Number).IsUnique();
        letterRequests.HasIndex(x => new { x.SubmittedByUserId, x.Status });
        letterRequests.HasOne<User>().WithMany().HasForeignKey(x => x.SubmittedByUserId).OnDelete(DeleteBehavior.Restrict);

        var revisions = model.Entity<LetterRevision>();
        revisions.ToTable("letter_revisions");
        revisions.HasKey(x => x.Id);
        revisions.Property(x => x.TemplateVersionId).HasMaxLength(80);
        revisions.Property(x => x.ContentHash).HasMaxLength(64);
        revisions.HasIndex(x => new { x.RequestId, x.RevisionNo }).IsUnique();
        revisions.HasOne<LetterRequest>().WithMany().HasForeignKey(x => x.RequestId).OnDelete(DeleteBehavior.Restrict);

        var participants = model.Entity<LetterParticipant>();
        participants.ToTable("letter_participants", t =>
        {
            t.HasCheckConstraint("ck_letter_participant_role", "\"Role\" IN ('Applicant','ClosingSignatory','AcknowledgingSignatory','ApprovingSignatory')");
        });
        participants.HasKey(x => x.Id);
        participants.Property(x => x.Role).HasConversion<string>().HasMaxLength(30);
        participants.Property(x => x.SlotKey).HasMaxLength(80);
        participants.Property(x => x.DisplayNameSnapshot).HasMaxLength(150);
        participants.Property(x => x.PositionSnapshot).HasMaxLength(150);
        participants.HasIndex(x => new { x.RevisionId, x.UserId });
        participants.HasOne<LetterRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
        participants.HasOne<User>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);

        var tasks = model.Entity<WorkflowTask>();
        tasks.ToTable("wf_tasks", t =>
        {
            t.HasCheckConstraint("ck_wf_task_action", "\"ActionType\" IN ('Sign','Acknowledge','ApproveAndSign','Review')");
            t.HasCheckConstraint("ck_wf_task_status", "\"Status\" IN ('Pending','Active','Signed','Acknowledged','Approved','RevisionRequested','Rejected','Deferred','Cancelled','Superseded')");
        });
        tasks.HasKey(x => x.Id);
        tasks.Property(x => x.ActionType).HasConversion<string>().HasMaxLength(30);
        tasks.Property(x => x.Status).HasConversion<string>().HasMaxLength(30);
        tasks.Property(x => x.DomainCode).HasMaxLength(80);
        tasks.Property(x => x.Comment).HasMaxLength(1000);
        tasks.Property(x => x.RowVersion).IsConcurrencyToken();
        tasks.HasIndex(x => new { x.RevisionId, x.Order });
        tasks.HasIndex(x => new { x.AssignedUserId, x.Status, x.ActivatedAt });
        tasks.HasOne<LetterRevision>().WithMany().HasForeignKey(x => x.RevisionId).OnDelete(DeleteBehavior.Cascade);
        tasks.HasOne<User>().WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.Restrict);

        var delegations = model.Entity<Delegation>();
        delegations.ToTable("wf_delegations");
        delegations.HasKey(x => x.Id);
        delegations.Property(x => x.Scope).HasMaxLength(200);
        delegations.Property(x => x.Reason).HasMaxLength(500);
        delegations.HasIndex(x => new { x.FromUserId, x.ToUserId, x.IsActive });
        delegations.HasOne<User>().WithMany().HasForeignKey(x => x.FromUserId).OnDelete(DeleteBehavior.Restrict);
        delegations.HasOne<User>().WithMany().HasForeignKey(x => x.ToUserId).OnDelete(DeleteBehavior.Restrict);

        var documents = model.Entity<Document>();
        documents.ToTable("doc_documents", t =>
        {
            t.HasCheckConstraint("ck_doc_kind", "\"Kind\" IN ('Source','Review','Final','Template')");
        });
        documents.HasKey(x => x.Id);
        documents.Property(x => x.Kind).HasConversion<string>().HasMaxLength(30);
        documents.Property(x => x.StorageKey).HasMaxLength(260);
        documents.Property(x => x.MimeType).HasMaxLength(100);
        documents.Property(x => x.Sha256).HasMaxLength(64);
        documents.Property(x => x.ProcessingState).HasMaxLength(30);
        documents.HasIndex(x => new { x.RevisionId, x.Kind });
        documents.HasIndex(x => x.Sha256);

        var auditLogs = model.Entity<AuditLog>();
        auditLogs.ToTable("app_audit_logs");
        auditLogs.HasKey(x => x.Id);
        auditLogs.Property(x => x.Action).HasMaxLength(100);
        auditLogs.Property(x => x.Entity).HasMaxLength(80);
        auditLogs.Property(x => x.CorrelationId).HasMaxLength(100);
        auditLogs.Property(x => x.Details).HasMaxLength(2000);
        auditLogs.Property(x => x.IpAddress).HasMaxLength(45);
        auditLogs.Property(x => x.UserAgent).HasMaxLength(512);
        auditLogs.HasIndex(x => new { x.ActorUserId, x.AtUtc });
        auditLogs.HasIndex(x => new { x.Entity, x.EntityId });
    }
}
