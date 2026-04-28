using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddWorkflowStepFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Endpoint",
                table: "TokenUsageRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ModelName",
                table: "TokenUsageRecords",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "StepLabel",
                table: "TokenUsageRecords",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Endpoint",
                table: "TokenUsageRecords");

            migrationBuilder.DropColumn(
                name: "ModelName",
                table: "TokenUsageRecords");

            migrationBuilder.DropColumn(
                name: "StepLabel",
                table: "TokenUsageRecords");
        }
    }
}
