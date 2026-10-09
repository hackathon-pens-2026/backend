CREATE TABLE IF NOT EXISTS "__EFMigrationsHistory" (
    "MigrationId" character varying(150) NOT NULL,
    "ProductVersion" character varying(32) NOT NULL,
    CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
);

START TRANSACTION;
CREATE TABLE auth_reset_email_budget (
    "Id" integer NOT NULL,
    "Day" date NOT NULL,
    "Month" integer NOT NULL,
    "DailyAttempts" integer NOT NULL,
    "MonthlyAttempts" integer NOT NULL,
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_auth_reset_email_budget" PRIMARY KEY ("Id"),
    CONSTRAINT ck_auth_email_budget_attempts CHECK ("DailyAttempts" >= 0 AND "MonthlyAttempts" >= 0),
    CONSTRAINT ck_auth_email_budget_singleton CHECK ("Id" = 1)
);

CREATE TABLE auth_users (
    "Id" uuid NOT NULL,
    "Name" character varying(150) NOT NULL,
    "Email" character varying(254) NOT NULL,
    "NormalizedEmail" character varying(254) NOT NULL,
    "NimNip" character varying(50),
    "Category" character varying(30) NOT NULL,
    "IsActive" boolean NOT NULL,
    "EmailVerifiedAt" timestamp with time zone,
    "PasswordHash" character varying(512) NOT NULL,
    "SecurityStamp" uuid NOT NULL,
    "Version" uuid NOT NULL,
    "FailedLoginCount" integer NOT NULL,
    "LockedUntil" timestamp with time zone,
    "LastLoginAt" timestamp with time zone,
    "LastPasswordResetRequestedAt" timestamp with time zone,
    "CreatedAt" timestamp with time zone NOT NULL,
    CONSTRAINT "PK_auth_users" PRIMARY KEY ("Id"),
    CONSTRAINT ck_auth_users_category CHECK ("Category" IN ('StudentGeneral','StudentDagri','BAAK','Management')),
    CONSTRAINT ck_auth_users_failed_logins CHECK ("FailedLoginCount" >= 0)
);

CREATE TABLE auth_audits (
    "Id" uuid NOT NULL,
    "UserId" uuid,
    "Action" character varying(100) NOT NULL,
    "AtUtc" timestamp with time zone NOT NULL,
    "IpAddress" character varying(45),
    "UserAgent" character varying(512),
    "CorrelationId" character varying(100) NOT NULL,
    CONSTRAINT "PK_auth_audits" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_auth_audits_auth_users_UserId" FOREIGN KEY ("UserId") REFERENCES auth_users ("Id") ON DELETE RESTRICT
);

CREATE TABLE auth_password_reset_tokens (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "SecurityStamp" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "ConsumedAt" timestamp with time zone,
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_auth_password_reset_tokens" PRIMARY KEY ("Id"),
    CONSTRAINT ck_auth_reset_expiry CHECK ("ExpiresAt" > "CreatedAt"),
    CONSTRAINT "FK_auth_password_reset_tokens_auth_users_UserId" FOREIGN KEY ("UserId") REFERENCES auth_users ("Id") ON DELETE RESTRICT
);

CREATE TABLE auth_sessions (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "SecurityStamp" uuid NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "RevokedAt" timestamp with time zone,
    "RevocationReason" character varying(80),
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_auth_sessions" PRIMARY KEY ("Id"),
    CONSTRAINT ck_auth_session_expiry CHECK ("ExpiresAt" > "CreatedAt"),
    CONSTRAINT "FK_auth_sessions_auth_users_UserId" FOREIGN KEY ("UserId") REFERENCES auth_users ("Id") ON DELETE RESTRICT
);

CREATE TABLE auth_user_assignments (
    "Id" uuid NOT NULL,
    "UserId" uuid NOT NULL,
    "PositionCode" character varying(80) NOT NULL,
    "PositionName" character varying(150) NOT NULL,
    "Scope" character varying(200) NOT NULL,
    "Capability" character varying(30) NOT NULL,
    "ValidFrom" timestamp with time zone NOT NULL,
    "ValidTo" timestamp with time zone,
    "IsActive" boolean NOT NULL,
    CONSTRAINT "PK_auth_user_assignments" PRIMARY KEY ("Id"),
    CONSTRAINT ck_auth_assignment_capability CHECK ("Capability" IN ('Requester','Signer','Approver','UnitOperator')),
    CONSTRAINT ck_auth_assignment_dates CHECK ("ValidTo" IS NULL OR "ValidTo" > "ValidFrom"),
    CONSTRAINT "FK_auth_user_assignments_auth_users_UserId" FOREIGN KEY ("UserId") REFERENCES auth_users ("Id") ON DELETE RESTRICT
);

CREATE TABLE auth_password_reset_emails (
    "Id" uuid NOT NULL,
    "ResetTokenId" uuid NOT NULL,
    "Recipient" character varying(254) NOT NULL,
    "ProtectedPayload" character varying(4096) NOT NULL,
    "Status" character varying(30) NOT NULL,
    "Attempts" integer NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "NextAttemptAt" timestamp with time zone NOT NULL,
    "FirstAttemptAt" timestamp with time zone,
    "AcceptedAt" timestamp with time zone,
    "ProviderMessageId" character varying(150),
    "LastErrorCode" character varying(100),
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_auth_password_reset_emails" PRIMARY KEY ("Id"),
    CONSTRAINT "FK_auth_password_reset_emails_auth_password_reset_tokens_Reset~" FOREIGN KEY ("ResetTokenId") REFERENCES auth_password_reset_tokens ("Id") ON DELETE RESTRICT
);

CREATE TABLE auth_refresh_credentials (
    "Id" uuid NOT NULL,
    "SessionId" uuid NOT NULL,
    "TokenHash" character varying(64) NOT NULL,
    "CreatedAt" timestamp with time zone NOT NULL,
    "ExpiresAt" timestamp with time zone NOT NULL,
    "UsedAt" timestamp with time zone,
    "Version" uuid NOT NULL,
    CONSTRAINT "PK_auth_refresh_credentials" PRIMARY KEY ("Id"),
    CONSTRAINT ck_auth_refresh_expiry CHECK ("ExpiresAt" > "CreatedAt"),
    CONSTRAINT "FK_auth_refresh_credentials_auth_sessions_SessionId" FOREIGN KEY ("SessionId") REFERENCES auth_sessions ("Id") ON DELETE RESTRICT
);

CREATE INDEX "IX_auth_audits_UserId_AtUtc" ON auth_audits ("UserId", "AtUtc");

CREATE UNIQUE INDEX "IX_auth_password_reset_emails_ResetTokenId" ON auth_password_reset_emails ("ResetTokenId");

CREATE INDEX "IX_auth_password_reset_emails_Status_NextAttemptAt" ON auth_password_reset_emails ("Status", "NextAttemptAt");

CREATE UNIQUE INDEX "IX_auth_password_reset_tokens_TokenHash" ON auth_password_reset_tokens ("TokenHash");

CREATE INDEX "IX_auth_password_reset_tokens_UserId_ConsumedAt" ON auth_password_reset_tokens ("UserId", "ConsumedAt");

CREATE INDEX "IX_auth_refresh_credentials_SessionId" ON auth_refresh_credentials ("SessionId");

CREATE UNIQUE INDEX "IX_auth_refresh_credentials_TokenHash" ON auth_refresh_credentials ("TokenHash");

CREATE INDEX "IX_auth_sessions_UserId_RevokedAt_ExpiresAt" ON auth_sessions ("UserId", "RevokedAt", "ExpiresAt");

CREATE INDEX "IX_auth_user_assignments_UserId_IsActive_ValidFrom" ON auth_user_assignments ("UserId", "IsActive", "ValidFrom");

CREATE UNIQUE INDEX "IX_auth_users_NormalizedEmail" ON auth_users ("NormalizedEmail");

INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261009094753_InitialAuthentication', '10.0.12');

COMMIT;

