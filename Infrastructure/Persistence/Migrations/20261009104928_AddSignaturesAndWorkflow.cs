using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignIt.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSignaturesAndWorkflow : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "app_audit_logs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Entity = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: true),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    AtUtc = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CorrelationId = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Details = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_app_audit_logs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "doc_documents",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    Kind = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    StorageKey = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    MimeType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Bytes = table.Column<long>(type: "bigint", nullable: false),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ProcessingState = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doc_documents", x => x.Id);
                    table.CheckConstraint("ck_doc_kind", "\"Kind\" IN ('Source','Review','Final','Template')");
                });

            migrationBuilder.CreateTable(
                name: "letter_requests",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Number = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    TypeId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Title = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SubmittedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OrganizationId = table.Column<Guid>(type: "uuid", nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CurrentRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    SubmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letter_requests", x => x.Id);
                    table.CheckConstraint("ck_letter_request_status", "\"Status\" IN ('Draft','InProgress','NeedsRevision','AwaitingResourceResolution','Finalizing','ProcessingFailed','Completed','Rejected','Cancelled','Revoked')");
                    table.ForeignKey(
                        name: "FK_letter_requests_auth_users_SubmittedByUserId",
                        column: x => x.SubmittedByUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sig_signing_attempts",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    QrAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    QrVersion = table.Column<int>(type: "integer", nullable: false),
                    QrHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    AttemptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sig_signing_attempts", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "sig_user_qrs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    OpaqueCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PrivateStorageKey = table.Column<string>(type: "character varying(260)", maxLength: 260, nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    ImageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sig_user_qrs", x => x.Id);
                    table.CheckConstraint("ck_sig_qr_status", "\"Status\" IN ('Active','Rotated','Revoked')");
                    table.CheckConstraint("ck_sig_qr_version", "\"Version\" >= 1");
                    table.ForeignKey(
                        name: "FK_sig_user_qrs_auth_users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sig_verification_records",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FinalDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    RandomCode = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    FinalHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevocationReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sig_verification_records", x => x.Id);
                    table.CheckConstraint("ck_sig_verification_status", "\"Status\" IN ('Valid','Revoked')");
                });

            migrationBuilder.CreateTable(
                name: "wf_delegations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Scope = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    StartAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_delegations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wf_delegations_auth_users_FromUserId",
                        column: x => x.FromUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wf_delegations_auth_users_ToUserId",
                        column: x => x.ToUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "letter_revisions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionNo = table.Column<int>(type: "integer", nullable: false),
                    TemplateVersionId = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ReviewDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FinalDocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    FrozenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letter_revisions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_letter_revisions_letter_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "letter_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "sig_evidences",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ActorId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    PositionSnapshot = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    QrAssetId = table.Column<Guid>(type: "uuid", nullable: false),
                    QrVersion = table.Column<int>(type: "integer", nullable: false),
                    QrHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DelegatedFromUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    MandateDescription = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: true),
                    IpAddress = table.Column<string>(type: "character varying(45)", maxLength: 45, nullable: true),
                    UserAgent = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sig_evidences", x => x.Id);
                    table.ForeignKey(
                        name: "FK_sig_evidences_auth_users_ActorId",
                        column: x => x.ActorId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_sig_evidences_sig_user_qrs_QrAssetId",
                        column: x => x.QrAssetId,
                        principalTable: "sig_user_qrs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "letter_participants",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Role = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SlotKey = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DisplayNameSnapshot = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    PositionSnapshot = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    Required = table.Column<bool>(type: "boolean", nullable: false),
                    PageIndex = table.Column<int>(type: "integer", nullable: false),
                    X = table.Column<double>(type: "double precision", nullable: false),
                    Y = table.Column<double>(type: "double precision", nullable: false),
                    Width = table.Column<double>(type: "double precision", nullable: false),
                    Height = table.Column<double>(type: "double precision", nullable: false),
                    Rotation = table.Column<double>(type: "double precision", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letter_participants", x => x.Id);
                    table.CheckConstraint("ck_letter_participant_role", "\"Role\" IN ('Applicant','ClosingSignatory','AcknowledgingSignatory','ApprovingSignatory')");
                    table.ForeignKey(
                        name: "FK_letter_participants_auth_users_UserId",
                        column: x => x.UserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_letter_participants_letter_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "letter_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wf_tasks",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    Order = table.Column<int>(type: "integer", nullable: false),
                    ActionType = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DomainCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    ActivatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DueAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ActedByUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    Comment = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wf_tasks", x => x.Id);
                    table.CheckConstraint("ck_wf_task_action", "\"ActionType\" IN ('Sign','Acknowledge','ApproveAndSign','Review')");
                    table.CheckConstraint("ck_wf_task_status", "\"Status\" IN ('Pending','Active','Signed','Acknowledged','Approved','RevisionRequested','Rejected','Deferred','Cancelled','Superseded')");
                    table.ForeignKey(
                        name: "FK_wf_tasks_auth_users_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_wf_tasks_letter_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "letter_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_app_audit_logs_ActorUserId_AtUtc",
                table: "app_audit_logs",
                columns: new[] { "ActorUserId", "AtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_app_audit_logs_Entity_EntityId",
                table: "app_audit_logs",
                columns: new[] { "Entity", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_doc_documents_RevisionId_Kind",
                table: "doc_documents",
                columns: new[] { "RevisionId", "Kind" });

            migrationBuilder.CreateIndex(
                name: "IX_doc_documents_Sha256",
                table: "doc_documents",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_letter_participants_RevisionId_UserId",
                table: "letter_participants",
                columns: new[] { "RevisionId", "UserId" });

            migrationBuilder.CreateIndex(
                name: "IX_letter_participants_UserId",
                table: "letter_participants",
                column: "UserId");

            migrationBuilder.CreateIndex(
                name: "IX_letter_requests_Number",
                table: "letter_requests",
                column: "Number",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_letter_requests_SubmittedByUserId_Status",
                table: "letter_requests",
                columns: new[] { "SubmittedByUserId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_letter_revisions_RequestId_RevisionNo",
                table: "letter_revisions",
                columns: new[] { "RequestId", "RevisionNo" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sig_evidences_ActorId",
                table: "sig_evidences",
                column: "ActorId");

            migrationBuilder.CreateIndex(
                name: "IX_sig_evidences_QrAssetId",
                table: "sig_evidences",
                column: "QrAssetId");

            migrationBuilder.CreateIndex(
                name: "IX_sig_evidences_RevisionId",
                table: "sig_evidences",
                column: "RevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_sig_evidences_TaskId",
                table: "sig_evidences",
                column: "TaskId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sig_signing_attempts_TaskId_ActorId_IdempotencyKey",
                table: "sig_signing_attempts",
                columns: new[] { "TaskId", "ActorId", "IdempotencyKey" });

            migrationBuilder.CreateIndex(
                name: "IX_sig_user_qrs_OpaqueCode",
                table: "sig_user_qrs",
                column: "OpaqueCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sig_user_qrs_OwnerUserId",
                table: "sig_user_qrs",
                column: "OwnerUserId",
                unique: true,
                filter: "\"Status\" = 'Active'");

            migrationBuilder.CreateIndex(
                name: "IX_sig_user_qrs_OwnerUserId_Version",
                table: "sig_user_qrs",
                columns: new[] { "OwnerUserId", "Version" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sig_verification_records_FinalHash",
                table: "sig_verification_records",
                column: "FinalHash");

            migrationBuilder.CreateIndex(
                name: "IX_sig_verification_records_RandomCode",
                table: "sig_verification_records",
                column: "RandomCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wf_delegations_FromUserId_ToUserId_IsActive",
                table: "wf_delegations",
                columns: new[] { "FromUserId", "ToUserId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_wf_delegations_ToUserId",
                table: "wf_delegations",
                column: "ToUserId");

            migrationBuilder.CreateIndex(
                name: "IX_wf_tasks_AssignedUserId_Status_ActivatedAt",
                table: "wf_tasks",
                columns: new[] { "AssignedUserId", "Status", "ActivatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_wf_tasks_RevisionId_Order",
                table: "wf_tasks",
                columns: new[] { "RevisionId", "Order" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "app_audit_logs");

            migrationBuilder.DropTable(
                name: "doc_documents");

            migrationBuilder.DropTable(
                name: "letter_participants");

            migrationBuilder.DropTable(
                name: "sig_evidences");

            migrationBuilder.DropTable(
                name: "sig_signing_attempts");

            migrationBuilder.DropTable(
                name: "sig_verification_records");

            migrationBuilder.DropTable(
                name: "wf_delegations");

            migrationBuilder.DropTable(
                name: "wf_tasks");

            migrationBuilder.DropTable(
                name: "sig_user_qrs");

            migrationBuilder.DropTable(
                name: "letter_revisions");

            migrationBuilder.DropTable(
                name: "letter_requests");
        }
    }
}
