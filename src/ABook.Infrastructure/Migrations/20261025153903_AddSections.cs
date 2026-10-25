using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddSections : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "ArchivedWithSection",
                table: "Chapters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "SectionId",
                table: "Chapters",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "ShowSectionsInOutput",
                table: "Books",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "Sections",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Number = table.Column<int>(type: "integer", nullable: false),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Outline = table.Column<string>(type: "text", nullable: false),
                    PovCharacter = table.Column<string>(type: "text", nullable: false),
                    CharactersInvolvedJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    PlotThreadsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    ForeshadowingNotes = table.Column<string>(type: "text", nullable: false),
                    PayoffNotes = table.Column<string>(type: "text", nullable: false),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Sections", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Sections_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_SectionId",
                table: "Chapters",
                column: "SectionId");

            migrationBuilder.CreateIndex(
                name: "IX_Sections_BookId_SortOrder",
                table: "Sections",
                columns: new[] { "BookId", "SortOrder" });

            // Every existing chapter becomes a section of its own (the default: one chapter per section) with the
            // chapter's plan, place and number. An archived chapter is an archived section in its place.
            migrationBuilder.Sql("""
                ALTER TABLE "Sections" ADD COLUMN migrated_chapter_id integer;

                INSERT INTO "Sections" ("BookId", "Number", "SortOrder", "Title", "Outline", "PovCharacter",
                    "CharactersInvolvedJson", "PlotThreadsJson", "ForeshadowingNotes", "PayoffNotes", "IsArchived",
                    "CreatedAt", "UpdatedAt", migrated_chapter_id)
                SELECT c."BookId", c."Number", c."SortOrder", c."Title", c."Outline", c."PovCharacter",
                       c."CharactersInvolvedJson", c."PlotThreadsJson", c."ForeshadowingNotes", c."PayoffNotes",
                       c."IsArchived", c."CreatedAt", c."UpdatedAt", c."Id"
                FROM "Chapters" c;

                UPDATE "Chapters" c SET "SectionId" = s."Id", "ArchivedWithSection" = c."IsArchived"
                FROM "Sections" s WHERE s.migrated_chapter_id = c."Id";

                ALTER TABLE "Sections" DROP COLUMN migrated_chapter_id;
                """);

            migrationBuilder.AddForeignKey(
                name: "FK_Chapters_Sections_SectionId",
                table: "Chapters",
                column: "SectionId",
                principalTable: "Sections",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Chapters_Sections_SectionId",
                table: "Chapters");

            migrationBuilder.DropTable(
                name: "Sections");

            migrationBuilder.DropIndex(
                name: "IX_Chapters_SectionId",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ArchivedWithSection",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "SectionId",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ShowSectionsInOutput",
                table: "Books");
        }
    }
}
