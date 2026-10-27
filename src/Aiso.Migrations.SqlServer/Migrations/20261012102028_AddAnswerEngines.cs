using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiso.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddAnswerEngines : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "OWNED_DOMAINS",
                table: "AISO_TARGET_PROFILE",
                type: "nvarchar(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PROMPTS_CITING_DOMAIN",
                table: "AISO_AUDIT_LOG",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "AISO_ENGINE_PROBE",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AUDIT_LOG_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PROMPT_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ENGINE_KEY = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    ENGINE_NAME = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    LANGUAGE_CODE = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    PROMPT_TEXT = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    MENTIONS_BRAND = table.Column<bool>(type: "bit", nullable: false),
                    CITES_OWNED_DOMAIN = table.Column<bool>(type: "bit", nullable: false),
                    MATCHED_KEYWORDS = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CITATIONS_JSON = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ANSWER_EXCERPT = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    ERROR_MESSAGE = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    LATENCY_MS = table.Column<long>(type: "bigint", nullable: false),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_ENGINE_PROBE", x => x.ID);
                    table.ForeignKey(
                        name: "FK_ENGINE_PROBE_AUDIT_LOG",
                        column: x => x.AUDIT_LOG_ID,
                        principalTable: "AISO_AUDIT_LOG",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ENGINE_PROBE_AUDIT_L_C686ED",
                table: "AISO_ENGINE_PROBE",
                columns: new[] { "AUDIT_LOG_ID", "ENGINE_KEY" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AISO_ENGINE_PROBE");

            migrationBuilder.DropColumn(
                name: "OWNED_DOMAINS",
                table: "AISO_TARGET_PROFILE");

            migrationBuilder.DropColumn(
                name: "PROMPTS_CITING_DOMAIN",
                table: "AISO_AUDIT_LOG");
        }
    }
}
