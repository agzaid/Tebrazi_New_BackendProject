using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Prescriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPrescriptions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "prescriptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    visit_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    medications = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    signed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    sent_to_patient_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    pdf_url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    refill_requested_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    refill_status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    refill_notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_prescriptions", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_prescriptions_physician_id",
                table: "prescriptions",
                column: "physician_id");

            migrationBuilder.CreateIndex(
                name: "IX_prescriptions_refill_status",
                table: "prescriptions",
                column: "refill_status");

            migrationBuilder.CreateIndex(
                name: "IX_prescriptions_status",
                table: "prescriptions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_prescriptions_subprofile_id",
                table: "prescriptions",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_prescriptions_visit_id",
                table: "prescriptions",
                column: "visit_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "prescriptions");
        }
    }
}
