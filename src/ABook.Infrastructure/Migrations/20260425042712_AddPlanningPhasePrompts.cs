using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningPhasePrompts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.RenameColumn(
                name: "PlannerSystemPrompt",
                table: "Books",
                newName: "StoryBibleSystemPrompt");

            migrationBuilder.AddColumn<string>(
                name: "ChapterOutlinesSystemPrompt",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CharactersSystemPrompt",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PlotThreadsSystemPrompt",
                table: "Books",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChapterOutlinesSystemPrompt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "CharactersSystemPrompt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "PlotThreadsSystemPrompt",
                table: "Books");

            migrationBuilder.RenameColumn(
                name: "StoryBibleSystemPrompt",
                table: "Books",
                newName: "PlannerSystemPrompt");
        }
    }
}
