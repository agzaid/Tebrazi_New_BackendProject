using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Patients.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialPatients : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "patient_profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    nationality = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    blood_type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    emergency_contact = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    emergency_phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    whatsapp_number = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_patient_profiles", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "family_subprofiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    patient_profile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    date_of_birth = table.Column<DateTime>(type: "datetime2", nullable: true),
                    gender = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    relation = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    blood_type = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_family_subprofiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_family_subprofiles_patient_profiles_patient_profile_id",
                        column: x => x.patient_profile_id,
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "allergies",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    allergen = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    severity = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    reaction = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    patient_profile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_allergies", x => x.id);
                    table.ForeignKey(
                        name: "FK_allergies_family_subprofiles_subprofile_id",
                        column: x => x.subprofile_id,
                        principalTable: "family_subprofiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_allergies_patient_profiles_patient_profile_id",
                        column: x => x.patient_profile_id,
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "chronic_conditions",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    condition = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    diagnosed_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    notes = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    patient_profile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_chronic_conditions", x => x.id);
                    table.ForeignKey(
                        name: "FK_chronic_conditions_family_subprofiles_subprofile_id",
                        column: x => x.subprofile_id,
                        principalTable: "family_subprofiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_chronic_conditions_patient_profiles_patient_profile_id",
                        column: x => x.patient_profile_id,
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "current_medications",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    drug_name = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    dosage = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    frequency = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    prescribed_by = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    start_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    end_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    is_active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    patient_profile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    deleted_at = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_current_medications", x => x.id);
                    table.ForeignKey(
                        name: "FK_current_medications_family_subprofiles_subprofile_id",
                        column: x => x.subprofile_id,
                        principalTable: "family_subprofiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_current_medications_patient_profiles_patient_profile_id",
                        column: x => x.patient_profile_id,
                        principalTable: "patient_profiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "external_doctors",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    patient_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    specialty = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    clinic_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_doctors", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_doctors_family_subprofiles_subprofile_id",
                        column: x => x.subprofile_id,
                        principalTable: "family_subprofiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "external_visits",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    patient_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    subprofile_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    doctor_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    clinic_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    specialty = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: true),
                    visit_date = table.Column<DateTime>(type: "datetime2", nullable: false),
                    chief_complaint = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    diagnosis = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    medications = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    follow_up_date = table.Column<DateTime>(type: "datetime2", nullable: true),
                    follow_up_notes = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_external_visits", x => x.id);
                    table.ForeignKey(
                        name: "FK_external_visits_family_subprofiles_subprofile_id",
                        column: x => x.subprofile_id,
                        principalTable: "family_subprofiles",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_allergies_patient_profile_id",
                table: "allergies",
                column: "patient_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_allergies_subprofile_id",
                table: "allergies",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_chronic_conditions_patient_profile_id",
                table: "chronic_conditions",
                column: "patient_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_chronic_conditions_subprofile_id",
                table: "chronic_conditions",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_current_medications_patient_profile_id",
                table: "current_medications",
                column: "patient_profile_id");

            migrationBuilder.CreateIndex(
                name: "IX_current_medications_subprofile_id",
                table: "current_medications",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_doctors_patient_user_id",
                table: "external_doctors",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_doctors_subprofile_id",
                table: "external_doctors",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_visits_patient_user_id",
                table: "external_visits",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_visits_subprofile_id",
                table: "external_visits",
                column: "subprofile_id");

            migrationBuilder.CreateIndex(
                name: "IX_external_visits_visit_date",
                table: "external_visits",
                column: "visit_date");

            migrationBuilder.CreateIndex(
                name: "IX_family_subprofiles_patient_profile_id_is_active",
                table: "family_subprofiles",
                columns: new[] { "patient_profile_id", "is_active" });

            migrationBuilder.CreateIndex(
                name: "UX_patient_profiles_user_id",
                table: "patient_profiles",
                column: "user_id",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "allergies");

            migrationBuilder.DropTable(
                name: "chronic_conditions");

            migrationBuilder.DropTable(
                name: "current_medications");

            migrationBuilder.DropTable(
                name: "external_doctors");

            migrationBuilder.DropTable(
                name: "external_visits");

            migrationBuilder.DropTable(
                name: "family_subprofiles");

            migrationBuilder.DropTable(
                name: "patient_profiles");
        }
    }
}
