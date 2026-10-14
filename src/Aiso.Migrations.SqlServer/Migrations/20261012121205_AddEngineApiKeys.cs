using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiso.Migrations.SqlServer.Migrations
{
    /// <inheritdoc />
    public partial class AddEngineApiKeys : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "API_KEY_CIPHERTEXT",
                table: "AISO_ANSWER_ENGINE",
                type: "nvarchar(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "API_KEY_HINT",
                table: "AISO_ANSWER_ENGINE",
                type: "nvarchar(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "API_KEY_UPDATED_AT_UTC",
                table: "AISO_ANSWER_ENGINE",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "API_KEY_UPDATED_BY",
                table: "AISO_ANSWER_ENGINE",
                type: "nvarchar(256)",
                maxLength: 256,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "API_KEY_CIPHERTEXT",
                table: "AISO_ANSWER_ENGINE");

            migrationBuilder.DropColumn(
                name: "API_KEY_HINT",
                table: "AISO_ANSWER_ENGINE");

            migrationBuilder.DropColumn(
                name: "API_KEY_UPDATED_AT_UTC",
                table: "AISO_ANSWER_ENGINE");

            migrationBuilder.DropColumn(
                name: "API_KEY_UPDATED_BY",
                table: "AISO_ANSWER_ENGINE");
        }
    }
}
