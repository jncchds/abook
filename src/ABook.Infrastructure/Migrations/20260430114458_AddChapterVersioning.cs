using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterId_ChunkIndex",
                table: "ChapterEmbeddings");

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "Chapters",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<int>(
                name: "ChapterVersionId",
                table: "ChapterEmbeddings",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Premise = table.Column<string>(type: "text", nullable: false),
                    Genre = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    TargetChapterCount = table.Column<int>(type: "integer", nullable: false),
                    Language = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    HumanAssisted = table.Column<bool>(type: "boolean", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ChapterVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ChapterId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Title = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    Outline = table.Column<string>(type: "text", nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    Status = table.Column<string>(type: "text", nullable: false),
                    PovCharacter = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    CharactersInvolvedJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    PlotThreadsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    ForeshadowingNotes = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    PayoffNotes = table.Column<string>(type: "text", nullable: false, defaultValue: ""),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    IsActive = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    HasEmbeddings = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChapterVersions_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "CharactersSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharactersSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlotThreadsSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    DataJson = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotThreadsSnapshots", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "StoryBibleSnapshots",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    SettingDescription = table.Column<string>(type: "text", nullable: false),
                    TimePeriod = table.Column<string>(type: "text", nullable: false),
                    Themes = table.Column<string>(type: "text", nullable: false),
                    ToneAndStyle = table.Column<string>(type: "text", nullable: false),
                    WorldRules = table.Column<string>(type: "text", nullable: false),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    Reason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryBibleSnapshots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterId_ChunkIndex",
                table: "ChapterEmbeddings",
                columns: new[] { "BookId", "ChapterId", "ChunkIndex" },
                unique: true,
                filter: "\"ChapterVersionId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterVersionId_ChunkIndex",
                table: "ChapterEmbeddings",
                columns: new[] { "BookId", "ChapterVersionId", "ChunkIndex" },
                unique: true,
                filter: "\"ChapterVersionId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_BookSnapshots_BookId",
                table: "BookSnapshots",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterVersions_ChapterId_IsActive",
                table: "ChapterVersions",
                columns: new[] { "ChapterId", "IsActive" });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterVersions_ChapterId_VersionNumber",
                table: "ChapterVersions",
                columns: new[] { "ChapterId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CharactersSnapshots_BookId",
                table: "CharactersSnapshots",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_PlotThreadsSnapshots_BookId",
                table: "PlotThreadsSnapshots",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryBibleSnapshots_BookId",
                table: "StoryBibleSnapshots",
                column: "BookId");

            // Data migration: create version 1 for every existing chapter
            migrationBuilder.Sql("""
                INSERT INTO "ChapterVersions" (
                    "ChapterId", "BookId", "VersionNumber",
                    "Title", "Outline", "Content", "Status",
                    "PovCharacter", "CharactersInvolvedJson", "PlotThreadsJson",
                    "ForeshadowingNotes", "PayoffNotes",
                    "CreatedBy", "IsActive", "HasEmbeddings", "CreatedAt"
                )
                SELECT
                    c."Id", c."BookId", 1,
                    c."Title", c."Outline", c."Content", c."Status",
                    c."PovCharacter", c."CharactersInvolvedJson", c."PlotThreadsJson",
                    c."ForeshadowingNotes", c."PayoffNotes",
                    'migration', TRUE,
                    EXISTS (
                        SELECT 1 FROM "ChapterEmbeddings" ce WHERE ce."ChapterId" = c."Id"
                    ),
                    NOW()
                FROM "Chapters" c;
                """);

            // Link existing embeddings to the newly created version 1 rows
            migrationBuilder.Sql("""
                UPDATE "ChapterEmbeddings" ce
                SET "ChapterVersionId" = cv."Id"
                FROM "ChapterVersions" cv
                WHERE cv."ChapterId" = ce."ChapterId"
                  AND cv."VersionNumber" = 1;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "BookSnapshots");

            migrationBuilder.DropTable(
                name: "ChapterVersions");

            migrationBuilder.DropTable(
                name: "CharactersSnapshots");

            migrationBuilder.DropTable(
                name: "PlotThreadsSnapshots");

            migrationBuilder.DropTable(
                name: "StoryBibleSnapshots");

            migrationBuilder.DropIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterId_ChunkIndex",
                table: "ChapterEmbeddings");

            migrationBuilder.DropIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterVersionId_ChunkIndex",
                table: "ChapterEmbeddings");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ChapterVersionId",
                table: "ChapterEmbeddings");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterId_ChunkIndex",
                table: "ChapterEmbeddings",
                columns: new[] { "BookId", "ChapterId", "ChunkIndex" },
                unique: true);
        }
    }
}
