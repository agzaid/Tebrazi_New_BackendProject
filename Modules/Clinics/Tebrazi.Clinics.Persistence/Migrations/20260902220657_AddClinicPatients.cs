using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Clinics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddClinicPatients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clinic_patients",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    national_id = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    blood_type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    allergies = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    chronic_conditions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    linked_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    linked_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    last_visit_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clinic_patients", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_clinic_id",
                table: "clinic_patients",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_linked_user_id",
                table: "clinic_patients",
                column: "linked_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_name",
                table: "clinic_patients",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_phone",
                table: "clinic_patients",
                column: "phone");

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_physician_user_id",
                table: "clinic_patients",
                column: "physician_user_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clinic_patients");
        }
    }
}
