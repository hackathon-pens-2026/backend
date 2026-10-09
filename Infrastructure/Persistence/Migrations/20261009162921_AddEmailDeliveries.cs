using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignIt.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailDeliveries : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_deliveries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    DeduplicationKey = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    RecipientUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    RecipientEmailSnapshot = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    TemplateVersion = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Subject = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    BodyText = table.Column<string>(type: "text", nullable: false),
                    BodyHtml = table.Column<string>(type: "text", nullable: false),
                    LetterRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    RelatedTaskId = table.Column<Guid>(type: "uuid", nullable: true),
                    IsReminder = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    FirstAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ProviderMessageId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Version = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_deliveries", x => x.Id);
                    table.CheckConstraint("ck_email_delivery_status", "\"Status\" IN ('Queued','QuotaDeferred','Accepted','Failed','Unknown','Cancelled','Suppressed')");
                    table.ForeignKey(
                        name: "FK_email_deliveries_auth_users_RecipientUserId",
                        column: x => x.RecipientUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_email_deliveries_letter_requests_LetterRequestId",
                        column: x => x.LetterRequestId,
                        principalTable: "letter_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_DeduplicationKey",
                table: "email_deliveries",
                column: "DeduplicationKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_LetterRequestId",
                table: "email_deliveries",
                column: "LetterRequestId");

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_RecipientUserId",
                table: "email_deliveries",
                column: "RecipientUserId");

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_RelatedTaskId",
                table: "email_deliveries",
                column: "RelatedTaskId");

            migrationBuilder.CreateIndex(
                name: "IX_email_deliveries_Status_NextAttemptAt",
                table: "email_deliveries",
                columns: new[] { "Status", "NextAttemptAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_deliveries");
        }
    }
}
