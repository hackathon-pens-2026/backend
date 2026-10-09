using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignIt.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddEmailWebhookEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "email_provider_events",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Provider = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    EventId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    EventType = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    ProviderMessageId = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: true),
                    OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ProcessedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_provider_events", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "email_suppressions",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(254)", maxLength: 254, nullable: false),
                    Reason = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    Source = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    SuppressedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_email_suppressions", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_email_provider_events_Provider_EventId",
                table: "email_provider_events",
                columns: new[] { "Provider", "EventId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_email_provider_events_ProviderMessageId",
                table: "email_provider_events",
                column: "ProviderMessageId");

            migrationBuilder.CreateIndex(
                name: "IX_email_suppressions_Email",
                table: "email_suppressions",
                column: "Email",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "email_provider_events");

            migrationBuilder.DropTable(
                name: "email_suppressions");
        }
    }
}
