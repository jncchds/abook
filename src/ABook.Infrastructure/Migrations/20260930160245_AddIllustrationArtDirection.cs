using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIllustrationArtDirection : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "IllustrationNegativePrompt",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IllustrationPortraitFraming",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IllustrationStyle",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "IllustrationStyleSource",
                table: "Books",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "IllustrationNegativePrompt",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IllustrationPortraitFraming",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IllustrationStyle",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IllustrationStyleSource",
                table: "Books");
        }
    }
}
