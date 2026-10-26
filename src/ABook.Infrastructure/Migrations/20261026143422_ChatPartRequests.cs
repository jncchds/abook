using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ChatPartRequests : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ChatPartRequestsEnabled",
                table: "Books",
                type: "boolean",
                nullable: false,
                defaultValue: false);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChatPartRequestsEnabled",
                table: "Books");
        }
    }
}
