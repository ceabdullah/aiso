using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiso.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class ContentLoop : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AISO_CONTENT_ITEM",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TARGET_PROFILE_ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    KIND = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    STATUS = table.Column<string>(type: "nvarchar(32)", maxLength: 32, nullable: false),
                    TITLE = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: false),
                    QUESTION = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    LANGUAGE_CODE = table.Column<string>(type: "nvarchar(16)", maxLength: 16, nullable: false),
                    SOURCE = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    RATIONALE = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    DRAFT = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    IMAGE_PROMPT = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    PLATFORM = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: true),
                    APPROVED_BY = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    UPDATED_BY = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    LIVE_URL = table.Column<string>(type: "nvarchar(1024)", maxLength: 1024, nullable: true),
                    LIVE_CHECKED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LIVE_NOTE = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    CITED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CITED_BY = table.Column<string>(type: "nvarchar(512)", maxLength: 512, nullable: true),
                    ERROR = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_CONTENT_ITEM", x => x.ID);
                    table.ForeignKey(
                        name: "FK_CONTENT_ITEM_TARGET_PROFILE",
                        column: x => x.TARGET_PROFILE_ID,
                        principalTable: "AISO_TARGET_PROFILE",
                        principalColumn: "ID",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CONTENT_ITEM_TARGET__465BA6",
                table: "AISO_CONTENT_ITEM",
                columns: new[] { "TARGET_PROFILE_ID", "STATUS" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AISO_CONTENT_ITEM");
        }
    }
}
