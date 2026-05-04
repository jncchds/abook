using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSnapshotSourceAndSoftDelete : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "TokenUsageRecords",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "PlotThreadsSnapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "phase-reset");

            migrationBuilder.AddColumn<string>(
                name: "Source",
                table: "CharactersSnapshots",
                type: "character varying(100)",
                maxLength: 100,
                nullable: false,
                defaultValue: "phase-reset");

            migrationBuilder.AddColumn<bool>(
                name: "IsDeleted",
                table: "AgentMessages",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "TokenUsageRecords");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "PlotThreadsSnapshots");

            migrationBuilder.DropColumn(
                name: "Source",
                table: "CharactersSnapshots");

            migrationBuilder.DropColumn(
                name: "IsDeleted",
                table: "AgentMessages");
        }
    }
}
