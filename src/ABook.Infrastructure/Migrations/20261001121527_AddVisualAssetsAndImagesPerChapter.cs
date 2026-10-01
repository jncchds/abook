using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddVisualAssetsAndImagesPerChapter : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ImagesPerChapter",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagesPerChapter",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "VisualAssets",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    CharacterCardId = table.Column<int>(type: "integer", nullable: true),
                    FirstChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_VisualAssets", x => x.Id);
                    table.ForeignKey(
                        name: "FK_VisualAssets_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_VisualAssets_CharacterCards_CharacterCardId",
                        column: x => x.CharacterCardId,
                        principalTable: "CharacterCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_VisualAssets_BookId_Name",
                table: "VisualAssets",
                columns: new[] { "BookId", "Name" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_VisualAssets_CharacterCardId",
                table: "VisualAssets",
                column: "CharacterCardId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "VisualAssets");

            migrationBuilder.DropColumn(
                name: "ImagesPerChapter",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImagesPerChapter",
                table: "LlmConfigurations");
        }
    }
}
