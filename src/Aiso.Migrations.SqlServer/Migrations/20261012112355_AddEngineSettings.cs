using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiso.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEngineSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "AISO_ANSWER_ENGINE",
                columns: table => new
                {
                    ID = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ENGINE_KEY = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    DISPLAY_NAME = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    KIND = table.Column<string>(type: "nvarchar(64)", maxLength: 64, nullable: false),
                    IS_ENABLED = table.Column<bool>(type: "bit", nullable: false),
                    DEFAULT_MODEL = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    MODEL_OVERRIDE = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    MAX_SEARCHES_OVERRIDE = table.Column<int>(type: "int", nullable: true),
                    KEY_CONFIGURED = table.Column<bool>(type: "bit", nullable: false),
                    LAST_SYNCED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UPDATED_BY = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    CREATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UPDATED_AT_UTC = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CONCURRENCY_STAMP = table.Column<Guid>(type: "uniqueidentifier", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AISO_ANSWER_ENGINE", x => x.ID);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ANSWER_ENGINE_ENGINE_KEY",
                table: "AISO_ANSWER_ENGINE",
                column: "ENGINE_KEY",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AISO_ANSWER_ENGINE");
        }
    }
}
