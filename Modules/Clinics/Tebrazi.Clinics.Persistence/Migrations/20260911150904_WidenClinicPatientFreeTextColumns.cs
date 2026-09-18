using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Clinics.Persistence.Migrations
{
    /// <summary>
    /// Widens the free-text columns POST /api/connections/clinic-patients writes, so a value Node
    /// stores in an unbounded Postgres `text` (schema.prisma:575-581) is not a truncation error —
    /// and therefore a 500 — here. See ClinicPatientConfiguration for the full reasoning.
    ///
    /// The two indexes over `name` and `phone` are dropped and recreated by hand around the
    /// ALTERs. EF scaffolds neither, and SQL Server refuses ALTER COLUMN on a key column of a
    /// live index in enough cases (error 5074) that relying on the widening being permitted is
    /// not worth the failed deployment. Dropping them changes nothing in the model, so
    /// `migrations has-pending-model-changes` stays clean.
    /// </summary>
    public partial class WidenClinicPatientFreeTextColumns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_clinic_patients_name",
                table: "clinic_patients");

            migrationBuilder.DropIndex(
                name: "IX_clinic_patients_phone",
                table: "clinic_patients");

            migrationBuilder.AlterColumn<string>(
                name: "phone",
                table: "clinic_patients",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(32)",
                oldMaxLength: 32,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "national_id",
                table: "clinic_patients",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(64)",
                oldMaxLength: 64,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "clinic_patients",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(200)",
                oldMaxLength: 200);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "clinic_patients",
                type: "nvarchar(450)",
                maxLength: 450,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(320)",
                oldMaxLength: 320,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "blood_type",
                table: "clinic_patients",
                type: "nvarchar(max)",
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(10)",
                oldMaxLength: 10,
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_name",
                table: "clinic_patients",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_phone",
                table: "clinic_patients",
                column: "phone");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_clinic_patients_name",
                table: "clinic_patients");

            migrationBuilder.DropIndex(
                name: "IX_clinic_patients_phone",
                table: "clinic_patients");

            migrationBuilder.AlterColumn<string>(
                name: "phone",
                table: "clinic_patients",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "national_id",
                table: "clinic_patients",
                type: "nvarchar(64)",
                maxLength: 64,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "name",
                table: "clinic_patients",
                type: "nvarchar(200)",
                maxLength: 200,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450);

            migrationBuilder.AlterColumn<string>(
                name: "email",
                table: "clinic_patients",
                type: "nvarchar(320)",
                maxLength: 320,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(450)",
                oldMaxLength: 450,
                oldNullable: true);

            migrationBuilder.AlterColumn<string>(
                name: "blood_type",
                table: "clinic_patients",
                type: "nvarchar(10)",
                maxLength: 10,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "nvarchar(max)",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_name",
                table: "clinic_patients",
                column: "name");

            migrationBuilder.CreateIndex(
                name: "IX_clinic_patients_phone",
                table: "clinic_patients",
                column: "phone");
        }
    }
}
