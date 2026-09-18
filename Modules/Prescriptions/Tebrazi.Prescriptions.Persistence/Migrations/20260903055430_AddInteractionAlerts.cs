using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Prescriptions.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractionAlerts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "interaction_alerts",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    physician_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    patient_user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    visit_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    drugs = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    interactions = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    alert_count = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    checked_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_interaction_alerts", x => x.id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_interaction_alerts_patient_user_id",
                table: "interaction_alerts",
                column: "patient_user_id");

            migrationBuilder.CreateIndex(
                name: "IX_interaction_alerts_physician_id",
                table: "interaction_alerts",
                column: "physician_id");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "interaction_alerts");
        }
    }
}
