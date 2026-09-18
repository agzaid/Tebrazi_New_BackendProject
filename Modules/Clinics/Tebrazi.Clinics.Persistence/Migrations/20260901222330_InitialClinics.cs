using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Clinics.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialClinics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "clinics",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    organization_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    city = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    country = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    working_hours = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    specialty = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    logo = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    allow_patient_booking = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    twilio_phone_number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    twilio_whatsapp_number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    consultation_fee = table.Column<double>(type: "float", nullable: true, defaultValue: 300.0),
                    follow_up_fee = table.Column<double>(type: "float", nullable: true, defaultValue: 200.0),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clinics", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "clinic_staff",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_clinic_staff", x => x.id);
                    table.ForeignKey(
                        name: "FK_clinic_staff_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_invitations",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    invited_by_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    permissions = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    token = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    accepted_by_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_invitations", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_invitations_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "staff_pins",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    clinic_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    generated_by_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    role = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    pin = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    used_by_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_staff_pins", x => x.id);
                    table.ForeignKey(
                        name: "FK_staff_pins_clinics_clinic_id",
                        column: x => x.clinic_id,
                        principalTable: "clinics",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_clinic_staff_user_id_is_active",
                table: "clinic_staff",
                columns: new[] { "user_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "UX_clinic_staff_clinic_user",
                table: "clinic_staff",
                columns: new[] { "clinic_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_clinics_is_active_specialty",
                table: "clinics",
                columns: new[] { "is_active", "specialty" });

            migrationBuilder.CreateIndex(
                name: "IX_clinics_organization_id",
                table: "clinics",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_clinics_physician_id",
                table: "clinics",
                column: "physician_id");

            migrationBuilder.CreateIndex(
                name: "IX_clinics_physician_id_is_active",
                table: "clinics",
                columns: new[] { "physician_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_invitations_clinic_id_status",
                table: "staff_invitations",
                columns: new[] { "clinic_id", "status" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_invitations_email",
                table: "staff_invitations",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "UX_staff_invitations_token",
                table: "staff_invitations",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_staff_pins_clinic_id_expires_at",
                table: "staff_pins",
                columns: new[] { "clinic_id", "expires_at" });

            migrationBuilder.CreateIndex(
                name: "IX_staff_pins_pin",
                table: "staff_pins",
                column: "pin");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "clinic_staff");

            migrationBuilder.DropTable(
                name: "staff_invitations");

            migrationBuilder.DropTable(
                name: "staff_pins");

            migrationBuilder.DropTable(
                name: "clinics");
        }
    }
}
