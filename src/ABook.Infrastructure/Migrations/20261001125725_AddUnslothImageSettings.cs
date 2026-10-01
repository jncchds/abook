using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUnslothImageSettings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ImageApiKey",
                table: "LlmPresets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageModel",
                table: "LlmPresets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageApiKey",
                table: "LlmConfigurations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageModel",
                table: "LlmConfigurations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ImageApiKey",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageModel",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageApiKey",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageModel",
                table: "LlmConfigurations");
        }
    }
}
