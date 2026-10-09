using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignIt.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddLetterPreviewJobs : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "letter_preview_jobs",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestId = table.Column<Guid>(type: "uuid", nullable: false),
                    RevisionId = table.Column<Guid>(type: "uuid", nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputJson = table.Column<string>(type: "text", nullable: false),
                    State = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeaseUntil = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    DocumentId = table.Column<Guid>(type: "uuid", nullable: true),
                    SlotsJson = table.Column<string>(type: "text", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_letter_preview_jobs", x => x.Id);
                    table.CheckConstraint("ck_preview_attempts", "\"Attempts\" >= 0");
                    table.CheckConstraint("ck_preview_state", "\"State\" IN ('Pending','Processing','Ready','Failed','Superseded')");
                    table.ForeignKey(
                        name: "FK_letter_preview_jobs_doc_documents_DocumentId",
                        column: x => x.DocumentId,
                        principalTable: "doc_documents",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_letter_preview_jobs_letter_requests_RequestId",
                        column: x => x.RequestId,
                        principalTable: "letter_requests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_letter_preview_jobs_letter_revisions_RevisionId",
                        column: x => x.RevisionId,
                        principalTable: "letter_revisions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_letter_preview_jobs_DocumentId",
                table: "letter_preview_jobs",
                column: "DocumentId");

            migrationBuilder.CreateIndex(
                name: "IX_letter_preview_jobs_RequestId",
                table: "letter_preview_jobs",
                column: "RequestId");

            migrationBuilder.CreateIndex(
                name: "IX_letter_preview_jobs_RevisionId_InputHash",
                table: "letter_preview_jobs",
                columns: new[] { "RevisionId", "InputHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_letter_preview_jobs_State_CreatedAt",
                table: "letter_preview_jobs",
                columns: new[] { "State", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "letter_preview_jobs");
        }
    }
}
