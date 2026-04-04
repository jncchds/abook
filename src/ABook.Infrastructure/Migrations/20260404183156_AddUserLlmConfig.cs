using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddUserLlmConfig : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.UpdateData(
                table: "LlmConfigurations",
                keyColumn: "Id",
                keyValue: 1,
                column: "UserId",
                value: null);

            migrationBuilder.CreateIndex(
                name: "IX_LlmConfigurations_UserId",
                table: "LlmConfigurations",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_LlmConfigurations_Users_UserId",
                table: "LlmConfigurations",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LlmConfigurations_Users_UserId",
                table: "LlmConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_LlmConfigurations_UserId",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "LlmConfigurations");
        }
    }
}
