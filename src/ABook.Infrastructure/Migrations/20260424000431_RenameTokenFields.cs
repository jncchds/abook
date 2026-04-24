using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RenameTokenFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PromptTokens",
                table: "TokenUsageRecords",
                newName: "OutputTokens");

            migrationBuilder.RenameColumn(
                name: "CompletionTokens",
                table: "TokenUsageRecords",
                newName: "InputTokens");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "OutputTokens",
                table: "TokenUsageRecords",
                newName: "PromptTokens");

            migrationBuilder.RenameColumn(
                name: "InputTokens",
                table: "TokenUsageRecords",
                newName: "CompletionTokens");
        }
    }
}
