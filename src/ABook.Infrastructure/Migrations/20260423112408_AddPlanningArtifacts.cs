using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningArtifacts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "CharactersInvolvedJson",
                table: "Chapters",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "ForeshadowingNotes",
                table: "Chapters",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PayoffNotes",
                table: "Chapters",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "PlotThreadsJson",
                table: "Chapters",
                type: "text",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<string>(
                name: "PovCharacter",
                table: "Chapters",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.CreateTable(
                name: "CharacterCards",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    PhysicalDescription = table.Column<string>(type: "text", nullable: false),
                    Personality = table.Column<string>(type: "text", nullable: false),
                    Backstory = table.Column<string>(type: "text", nullable: false),
                    GoalMotivation = table.Column<string>(type: "text", nullable: false),
                    Arc = table.Column<string>(type: "text", nullable: false),
                    FirstAppearanceChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCards", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCards_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlotThreads",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    IntroducedChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    ResolvedChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotThreads", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlotThreads_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "StoryBibles",
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
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryBibles", x => x.Id);
                    table.ForeignKey(
                        name: "FK_StoryBibles_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCards_BookId",
                table: "CharacterCards",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_PlotThreads_BookId",
                table: "PlotThreads",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_StoryBibles_BookId",
                table: "StoryBibles",
                column: "BookId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterCards");

            migrationBuilder.DropTable(
                name: "PlotThreads");

            migrationBuilder.DropTable(
                name: "StoryBibles");

            migrationBuilder.DropColumn(
                name: "CharactersInvolvedJson",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "ForeshadowingNotes",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PayoffNotes",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PlotThreadsJson",
                table: "Chapters");

            migrationBuilder.DropColumn(
                name: "PovCharacter",
                table: "Chapters");
        }
    }
}
