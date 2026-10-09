using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SignIt.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddRoomReservations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "room_reservations",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    FacilityResourceId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestedByUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    LetterRequestId = table.Column<Guid>(type: "uuid", nullable: true),
                    LetterRevisionId = table.Column<Guid>(type: "uuid", nullable: true),
                    ActivityType = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    StartsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    EndsAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    Status = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConfirmedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RowVersion = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_room_reservations", x => x.Id);
                    table.CheckConstraint("ck_room_reservation_range", "\"EndsAt\" > \"StartsAt\"");
                    table.CheckConstraint("ck_room_reservation_status", "\"Status\" IN ('Pending','Confirmed','Cancelled','Released')");
                    table.ForeignKey(
                        name: "FK_room_reservations_auth_users_RequestedByUserId",
                        column: x => x.RequestedByUserId,
                        principalTable: "auth_users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_room_reservations_facility_resources_FacilityResourceId",
                        column: x => x.FacilityResourceId,
                        principalTable: "facility_resources",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_room_reservations_FacilityResourceId_Status_StartsAt",
                table: "room_reservations",
                columns: new[] { "FacilityResourceId", "Status", "StartsAt" });

            migrationBuilder.CreateIndex(
                name: "IX_room_reservations_LetterRevisionId",
                table: "room_reservations",
                column: "LetterRevisionId");

            migrationBuilder.CreateIndex(
                name: "IX_room_reservations_RequestedByUserId",
                table: "room_reservations",
                column: "RequestedByUserId");

            // Anti-overlap dijamin database: hanya reservasi Confirmed yang tidak boleh bertumpuk
            // pada resource dan rentang waktu yang sama (interval setengah terbuka [start, end)).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS btree_gist;");
            migrationBuilder.Sql(
                "ALTER TABLE \"room_reservations\" ADD CONSTRAINT \"ex_room_reservations_confirmed_no_overlap\" "
                + "EXCLUDE USING gist (\"FacilityResourceId\" WITH =, tstzrange(\"StartsAt\", \"EndsAt\", '[)') WITH &&) "
                + "WHERE (\"Status\" = 'Confirmed');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE \"room_reservations\" DROP CONSTRAINT IF EXISTS \"ex_room_reservations_confirmed_no_overlap\";");
            migrationBuilder.DropTable(
                name: "room_reservations");
        }
    }
}
