using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterSortOrder : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chapters_BookId",
                table: "Chapters");

            migrationBuilder.AddColumn<int>(
                name: "SortOrder",
                table: "Chapters",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Places from today's numbers: an archived chapter sits just before the active chapter that took its
            // number (it was replaced by it). Then active chapters are numbered 1..N without gaps, and the chapter
            // numbers that plot threads and characters point at move with them (an archived chapter's number goes to
            // the next active chapter after it).
            migrationBuilder.Sql("""
                UPDATE "Chapters" c SET "SortOrder" = o.rn
                FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "BookId" ORDER BY "Number", "IsArchived" DESC, "Id") AS rn
                      FROM "Chapters") o
                WHERE c."Id" = o."Id";

                CREATE TEMP TABLE chapter_renumber AS
                SELECT DISTINCT ON ("BookId", "Number") "BookId", "Number" AS old_number, new_number
                FROM (SELECT "BookId", "Number", "SortOrder",
                             ROW_NUMBER() OVER (PARTITION BY "BookId" ORDER BY "SortOrder") AS new_number
                      FROM "Chapters" WHERE NOT "IsArchived") a
                ORDER BY "BookId", "Number", "SortOrder";

                INSERT INTO chapter_renumber
                SELECT * FROM (
                    SELECT DISTINCT a."BookId", a."Number" AS old_number, COALESCE(
                        (SELECT r.new_number FROM chapter_renumber r
                         WHERE r."BookId" = a."BookId" AND r.old_number > a."Number" ORDER BY r.old_number LIMIT 1),
                        (SELECT MAX(r.new_number) FROM chapter_renumber r WHERE r."BookId" = a."BookId")) AS new_number
                    FROM "Chapters" a
                    WHERE a."IsArchived" AND NOT EXISTS (
                        SELECT 1 FROM chapter_renumber r WHERE r."BookId" = a."BookId" AND r.old_number = a."Number")
                ) x WHERE x.new_number IS NOT NULL;

                UPDATE "PlotThreads" t SET "IntroducedChapterNumber" = r.new_number
                FROM chapter_renumber r
                WHERE r."BookId" = t."BookId" AND r.old_number = t."IntroducedChapterNumber" AND r.new_number <> r.old_number;

                UPDATE "PlotThreads" t SET "ResolvedChapterNumber" = r.new_number
                FROM chapter_renumber r
                WHERE r."BookId" = t."BookId" AND r.old_number = t."ResolvedChapterNumber" AND r.new_number <> r.old_number;

                UPDATE "CharacterCards" k SET "FirstAppearanceChapterNumber" = r.new_number
                FROM chapter_renumber r
                WHERE r."BookId" = k."BookId" AND r.old_number = k."FirstAppearanceChapterNumber" AND r.new_number <> r.old_number;

                UPDATE "Chapters" c SET "Number" = a.new_number
                FROM (SELECT "Id", ROW_NUMBER() OVER (PARTITION BY "BookId" ORDER BY "SortOrder") AS new_number
                      FROM "Chapters" WHERE NOT "IsArchived") a
                WHERE c."Id" = a."Id" AND c."Number" <> a.new_number;

                DROP TABLE chapter_renumber;
                """);

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_BookId_SortOrder",
                table: "Chapters",
                columns: new[] { "BookId", "SortOrder" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Chapters_BookId_SortOrder",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "SortOrder",
                table: "Chapters");

            migrationBuilder.CreateIndex(
                name: "IX_Chapters_BookId",
                table: "Chapters",
                column: "BookId");
        }
    }
}
