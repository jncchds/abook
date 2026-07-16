using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLlmExecutionParameters : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "MaxTokens",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "LlmPresets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Temperature",
                table: "LlmPresets",
                type: "real",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutMs",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxTokens",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReasoningEffort",
                table: "LlmConfigurations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<float>(
                name: "Temperature",
                table: "LlmConfigurations",
                type: "real",
                nullable: false,
                defaultValue: 0f);

            migrationBuilder.AddColumn<int>(
                name: "TimeoutMs",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "MaxTokens",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "TimeoutMs",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "MaxTokens",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ReasoningEffort",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "Temperature",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "TimeoutMs",
                table: "LlmConfigurations");
        }
    }
}
