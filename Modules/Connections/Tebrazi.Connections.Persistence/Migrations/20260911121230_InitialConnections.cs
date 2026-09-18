using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Connections.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialConnections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "connection_pins",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    pin = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    used_by_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_connection_pins", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "doctor_patient_connections",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    patient_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    initiated_by = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    connected_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_doctor_patient_connections", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_connection_pins_expires_at",
                table: "connection_pins",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_connection_pins_physician_user_id",
                table: "connection_pins",
                column: "physician_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_connection_pins_pin",
                table: "connection_pins",
                column: "pin");

            migrationBuilder.CreateIndex(
                name: "IX_doctor_patient_connections_patient_user_id",
                table: "doctor_patient_connections",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_doctor_patient_connections_physician_user_id",
                table: "doctor_patient_connections",
                column: "physician_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_doctor_patient_connections_status",
                table: "doctor_patient_connections",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "UX_doctor_patient_connections_physician_patient_subprofile",
                table: "doctor_patient_connections",
                columns: new[] { "physician_user_id", "patient_user_id", "subprofile_id" },
                unique: true,
                filter: "[subprofile_id] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "connection_pins");

            migrationBuilder.DropTable(
                name: "doctor_patient_connections");
        }
    }
}
