using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LinkAnswersToQuestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "QuestionMessageId",
                table: "Clarifications",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "QuestionMessageId",
                table: "AgentMessages",
                type: "integer",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "QuestionMessageId",
                table: "Clarifications");

            migrationBuilder.DropColumn(
                name: "QuestionMessageId",
                table: "AgentMessages");
        }
    }
}
