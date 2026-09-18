using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Tebrazi.Identity.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class InitialIdentity : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "invitations",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    organization_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: false),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    token = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    accepted_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    invited_by = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_invitations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "organizations",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    slug = table.Column<string>(type: "nvarchar(220)", maxLength: 220, nullable: false),
                    logo = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    settings = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organizations", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "otp_codes",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    code = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    purpose = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    attempts = table.Column<int>(type: "int", nullable: false, defaultValue: 0),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_otp_codes", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "users",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    email = table.Column<string>(type: "nvarchar(320)", maxLength: 320, nullable: true),
                    password_hash = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    display_name = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    phone = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: true),
                    profile_picture_url = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    user_type = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    active = table.Column<bool>(type: "bit", nullable: false, defaultValue: true),
                    current_organization_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: true),
                    subscription_tier = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false, defaultValue: "FREE"),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_users", x => x.id);
                });

            migrationBuilder.CreateTable(
                name: "subscriptions",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    organization_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    plan = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    current_period_start = table.Column<DateTime>(type: "datetime2", nullable: false),
                    current_period_end = table.Column<DateTime>(type: "datetime2", nullable: false),
                    cancel_at_period_end = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    stripe_customer_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    stripe_subscription_id = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_subscriptions", x => x.id);
                    table.ForeignKey(
                        name: "FK_subscriptions_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "organization_members",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    organization_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    role = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_organization_members", x => x.id);
                    table.ForeignKey(
                        name: "FK_organization_members_organizations_organization_id",
                        column: x => x.organization_id,
                        principalTable: "organizations",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_organization_members_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "password_reset_tokens",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    token = table.Column<string>(type: "nvarchar(128)", maxLength: 128, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    used_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_password_reset_tokens", x => x.id);
                    table.ForeignKey(
                        name: "FK_password_reset_tokens_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

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
                    table.ForeignKey(
                        name: "FK_patient_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "physician_profiles",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    license_number = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    specialty = table.Column<string>(type: "nvarchar(120)", maxLength: 120, nullable: false),
                    qualifications = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    bio = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    scratchpad_notes = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    years_of_experience = table.Column<int>(type: "int", nullable: true),
                    verified = table.Column<bool>(type: "bit", nullable: false, defaultValue: false),
                    verified_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false),
                    updated_at = table.Column<DateTime>(type: "datetime2", nullable: true),
                    updated_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_physician_profiles", x => x.id);
                    table.ForeignKey(
                        name: "FK_physician_profiles_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "sessions",
                columns: table => new
                {
                    id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    user_id = table.Column<string>(type: "nvarchar(36)", maxLength: 36, nullable: false),
                    token = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    expires_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    device_info = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    ip_address = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    created_at = table.Column<DateTime>(type: "datetime2", nullable: false),
                    created_by = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_sessions", x => x.id);
                    table.ForeignKey(
                        name: "FK_sessions_users_user_id",
                        column: x => x.user_id,
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_invitations_email",
                table: "invitations",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "IX_invitations_organization_id",
                table: "invitations",
                column: "organization_id");

            migrationBuilder.CreateIndex(
                name: "UX_invitations_token",
                table: "invitations",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_organization_members_user_id",
                table: "organization_members",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "UX_organization_members_org_user",
                table: "organization_members",
                columns: new[] { "organization_id", "user_id" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_organizations_slug",
                table: "organizations",
                column: "slug",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_expires_at",
                table: "otp_codes",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_otp_codes_phone_purpose",
                table: "otp_codes",
                columns: new[] { "phone", "purpose" });

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_expires_at",
                table: "password_reset_tokens",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_password_reset_tokens_user_id",
                table: "password_reset_tokens",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "UX_password_reset_tokens_token",
                table: "password_reset_tokens",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "UX_patient_profiles_user_id",
                table: "patient_profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_physician_profiles_specialty",
                table: "physician_profiles",
                column: "specialty");

            migrationBuilder.CreateIndex(
                name: "UX_physician_profiles_user_id",
                table: "physician_profiles",
                column: "user_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_sessions_expires_at",
                table: "sessions",
                column: "expires_at");

            migrationBuilder.CreateIndex(
                name: "IX_sessions_user_id",
                table: "sessions",
                column: "user_id");

            migrationBuilder.CreateIndex(
                name: "UX_sessions_token",
                table: "sessions",
                column: "token",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_plan",
                table: "subscriptions",
                column: "plan");

            migrationBuilder.CreateIndex(
                name: "IX_subscriptions_status",
                table: "subscriptions",
                column: "status");

            migrationBuilder.CreateIndex(
                name: "UX_subscriptions_organization_id",
                table: "subscriptions",
                column: "organization_id",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_users_current_organization_id",
                table: "users",
                column: "current_organization_id");

            migrationBuilder.CreateIndex(
                name: "IX_users_email",
                table: "users",
                column: "email");

            migrationBuilder.CreateIndex(
                name: "IX_users_role",
                table: "users",
                column: "role");

            migrationBuilder.CreateIndex(
                name: "IX_users_user_type",
                table: "users",
                column: "user_type");

            migrationBuilder.CreateIndex(
                name: "UX_users_email_user_type",
                table: "users",
                columns: new[] { "email", "user_type" },
                unique: true,
                filter: "[email] IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "UX_users_phone",
                table: "users",
                column: "phone",
                unique: true,
                filter: "[phone] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "invitations");

            migrationBuilder.DropTable(
                name: "organization_members");

            migrationBuilder.DropTable(
                name: "otp_codes");

            migrationBuilder.DropTable(
                name: "password_reset_tokens");

            migrationBuilder.DropTable(
                name: "patient_profiles");

            migrationBuilder.DropTable(
                name: "physician_profiles");

            migrationBuilder.DropTable(
                name: "sessions");

            migrationBuilder.DropTable(
                name: "subscriptions");

            migrationBuilder.DropTable(
                name: "users");

            migrationBuilder.DropTable(
                name: "organizations");
        }
    }
}
