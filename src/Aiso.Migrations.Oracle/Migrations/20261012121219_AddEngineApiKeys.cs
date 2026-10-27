using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Aiso.Migrations.Oracle.Migrations
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
                type: "NVARCHAR2(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "API_KEY_HINT",
                table: "AISO_ANSWER_ENGINE",
                type: "NVARCHAR2(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "API_KEY_UPDATED_AT_UTC",
                table: "AISO_ANSWER_ENGINE",
                type: "TIMESTAMP(7)",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "API_KEY_UPDATED_BY",
                table: "AISO_ANSWER_ENGINE",
                type: "NVARCHAR2(256)",
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
