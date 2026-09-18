using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Visits.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialVisits : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "visits",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    organization_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    patient_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    clinic_patient_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    audio_url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    raw_transcript = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    raw_notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    subjective = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    objective = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    assessment = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    plan = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    chief_complaint = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    diagnosis = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    diagnosis_codes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    follow_up_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    follow_up_notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    shared_sections = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    patient_feedback = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    patient_feedback_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    patient_dismissed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    visit_date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    specialty_data = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_visits", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "investigations",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    visit_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    type = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: false),
                    name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    instructions = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    result_url = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    result_notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    requested_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    completed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_investigations", x => x.id);
                    table.ForeignKey(
                        name: "FK_investigations_visits_visit_id",
                        column: x => x.visit_id,
                        principalTable: "visits",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_investigations_status",
                table: "investigations",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_investigations_subprofile_id",
                table: "investigations",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_investigations_visit_id",
                table: "investigations",
                column: "visit_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_clinic_id",
                table: "visits",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_clinic_id_visit_date",
                table: "visits",
                columns: new[] { "clinic_id", "visit_date" });

            migrationBuilder.CreateIndex(
                name: "IX_visits_clinic_patient_id",
                table: "visits",
                column: "clinic_patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_organization_id",
                table: "visits",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_patient_user_id",
                table: "visits",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_patient_user_id_visit_date",
                table: "visits",
                columns: new[] { "patient_user_id", "visit_date" });

            migrationBuilder.CreateIndex(
                name: "IX_visits_physician_id",
                table: "visits",
                column: "physician_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_physician_id_status",
                table: "visits",
                columns: new[] { "physician_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_visits_status",
                table: "visits",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_visits_subprofile_id",
                table: "visits",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_visits_visit_date",
                table: "visits",
                column: "visit_date");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "investigations");

            migrationBuilder.DropTable(
                name: "visits");
        }
    }
}
