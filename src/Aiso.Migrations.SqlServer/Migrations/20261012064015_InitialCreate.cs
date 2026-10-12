using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiso.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class InitialCreate : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AISO_ROLE_MAPPING",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AD_GROUP_CN = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    APP_ROLE = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    IS_ENABLED = table.Column<bool>(type: "bit", nullable: false),
                    NOTES = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_ROLE_MAPPING", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "AISO_SYSTEM_SETTING",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SETTING_KEY = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    SETTING_VALUE = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    DESCRIPTION = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    UPDATED_BY = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_SYSTEM_SETTING", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "AISO_TARGET_PROFILE",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CODE = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DISPLAY_NAME = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: false),
                    BASE_URL = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: false),
                    CHANNEL_KIND = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    BRAND_KEYWORDS = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    PRIMARY_LANGUAGE_CODE = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    IS_ACTIVE = table.Column<bool>(type: "bit", nullable: false),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_TARGET_PROFILE", x => x.ID);
                });

            migrationBuilder.CreateTable(
                name: "AISO_AUDIT_LOG",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TARGET_PROFILE_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    RUN_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    STATUS = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    HTTP_STATUS_CODE = table.Column<int>(type: "int", nullable: true),
                    ON_PAGE_SCORE = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    PROMPT_VISIBILITY_SCORE = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    OVERALL_SCORE = table.Column<decimal>(type: "decimal(5,2)", precision: 5, scale: 2, nullable: false),
                    PROMPTS_EVALUATED = table.Column<int>(type: "int", nullable: false),
                    PROMPTS_MENTIONING_BRAND = table.Column<int>(type: "int", nullable: false),
                    SNAPSHOT_PATH = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    METRICS_JSON = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ERROR_MESSAGE = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DURATION_MS = table.Column<long>(type: "bigint", nullable: false),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_AUDIT_LOG", x => x.ID);
                    table.ForeignKey(
                        name: "FK_AUDIT_LOG_TARGET_PROFILE",
                        column: x => x.TARGET_PROFILE_ID,
                        principalTable: "AISO_TARGET_PROFILE",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AISO_TEST_PROMPT",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TARGET_PROFILE_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PROMPT_TEXT = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    LANGUAGE_CODE = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    CATEGORY = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    IS_ACTIVE = table.Column<bool>(type: "bit", nullable: false),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_TEST_PROMPT", x => x.ID);
                    table.ForeignKey(
                        name: "FK_TEST_PROMPT_TARGET_PROFILE",
                        column: x => x.TARGET_PROFILE_ID,
                        principalTable: "AISO_TARGET_PROFILE",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "AISO_REMEDIATION_PLAN",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TARGET_PROFILE_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AUDIT_LOG_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    TITLE = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    LANGUAGE_CODE = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    STATUS = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    RAW_AGENT_OUTPUT = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    MARKDOWN_CONTENT = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    REPORT_FILE_PATH = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    ITEM_COUNT = table.Column<int>(type: "int", nullable: false),
                    AGENT_SESSION_ID = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_REMEDIATION_PLAN", x => x.ID);
                    table.ForeignKey(
                        name: "FK_REMEDIATION_PLAN_AUDIT_LOG",
                        column: x => x.AUDIT_LOG_ID,
                        principalTable: "AISO_AUDIT_LOG",
                        principalColumn: "ID");
                    table.ForeignKey(
                        name: "FK_REMEDIATION_PLAN_TAR_0FC6DE",
                        column: x => x.TARGET_PROFILE_ID,
                        principalTable: "AISO_TARGET_PROFILE",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AUDIT_LOG_CREATED_AT_UTC",
                table: "AISO_AUDIT_LOG",
                column: "CREATED_AT_UTC");

            migrationBuilder.CreateIndex(
                name: "IX_AUDIT_LOG_RUN_ID",
                table: "AISO_AUDIT_LOG",
                column: "RUN_ID");

            migrationBuilder.CreateIndex(
                name: "IX_AUDIT_LOG_TARGET_PRO_885377",
                table: "AISO_AUDIT_LOG",
                columns: new[] { "TARGET_PROFILE_ID", "CREATED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_REMEDIATION_PLAN_AUD_B55098",
                table: "AISO_REMEDIATION_PLAN",
                column: "AUDIT_LOG_ID");

            migrationBuilder.CreateIndex(
                name: "IX_REMEDIATION_PLAN_TAR_6FE4ED",
                table: "AISO_REMEDIATION_PLAN",
                columns: new[] { "TARGET_PROFILE_ID", "CREATED_AT_UTC" });

            migrationBuilder.CreateIndex(
                name: "IX_ROLE_MAPPING_AD_GROU_A2553E",
                table: "AISO_ROLE_MAPPING",
                columns: new[] { "AD_GROUP_CN", "APP_ROLE" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SYSTEM_SETTING_SETTING_KEY",
                table: "AISO_SYSTEM_SETTING",
                column: "SETTING_KEY",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TARGET_PROFILE_CODE",
                table: "AISO_TARGET_PROFILE",
                column: "CODE",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TEST_PROMPT_TARGET_P_9AA7FD",
                table: "AISO_TEST_PROMPT",
                columns: new[] { "TARGET_PROFILE_ID", "LANGUAGE_CODE" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AISO_REMEDIATION_PLAN");

            migrationBuilder.DropTable(
                name: "AISO_ROLE_MAPPING");

            migrationBuilder.DropTable(
                name: "AISO_SYSTEM_SETTING");

            migrationBuilder.DropTable(
                name: "AISO_TEST_PROMPT");

            migrationBuilder.DropTable(
                name: "AISO_AUDIT_LOG");

            migrationBuilder.DropTable(
                name: "AISO_TARGET_PROFILE");
        }
    }
}
