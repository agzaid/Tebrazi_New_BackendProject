using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Appointments.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialAppointments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "appointments",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    patient_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    clinic_patient_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    appointment_date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    start_time = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    end_time = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    appointment_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    confirmed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    completed_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    cancelled_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    cancel_reason = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    recurring_rule = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    recurring_group_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_appointments", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "time_slots",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    day_of_week = table.Column<int>(type: "int", nullable: false),
                    start_time = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    end_time = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    slot_duration = table.Column<int>(type: "int", nullable: false, defaultValue: 30),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_time_slots", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_appointment_date",
                table: "appointments",
                column: "appointment_date");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_clinic_id",
                table: "appointments",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_clinic_id_appointment_date",
                table: "appointments",
                columns: new[] { "clinic_id", "appointment_date" });

            migrationBuilder.CreateIndex(
                name: "IX_appointments_clinic_patient_id",
                table: "appointments",
                column: "clinic_patient_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_patient_user_id",
                table: "appointments",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_physician_id",
                table: "appointments",
                column: "physician_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_recurring_group_id",
                table: "appointments",
                column: "recurring_group_id");

            migrationBuilder.CreateIndex(
                name: "IX_appointments_status",
                table: "appointments",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "IX_time_slots_clinic_id",
                table: "time_slots",
                column: "clinic_id");

            migrationBuilder.CreateIndex(
                name: "IX_time_slots_day_of_week",
                table: "time_slots",
                column: "day_of_week");

            migrationBuilder.CreateIndex(
                name: "IX_time_slots_physician_id",
                table: "time_slots",
                column: "physician_id");

            migrationBuilder.CreateIndex(
                name: "IX_time_slots_physician_id_day_of_week_is_active",
                table: "time_slots",
                columns: new[] { "physician_id", "day_of_week", "is_active" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "appointments");

            migrationBuilder.DropTable(
                name: "time_slots");
        }
    }
}
