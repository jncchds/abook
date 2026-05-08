using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPerItemVersioning : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "PlotThreads",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsArchived",
                table: "CharacterCards",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.CreateTable(
                name: "CharacterCardVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CharacterCardId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    PhysicalDescription = table.Column<string>(type: "text", nullable: false),
                    Personality = table.Column<string>(type: "text", nullable: false),
                    Backstory = table.Column<string>(type: "text", nullable: false),
                    GoalMotivation = table.Column<string>(type: "text", nullable: false),
                    Arc = table.Column<string>(type: "text", nullable: false),
                    FirstAppearanceChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    Notes = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CharacterCardVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_CharacterCardVersions_CharacterCards_CharacterCardId",
                        column: x => x.CharacterCardId,
                        principalTable: "CharacterCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "PlotThreadVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    PlotThreadId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Name = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    Type = table.Column<string>(type: "text", nullable: false),
                    IntroducedChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    ResolvedChapterNumber = table.Column<int>(type: "integer", nullable: true),
                    Status = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlotThreadVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_PlotThreadVersions_PlotThreads_PlotThreadId",
                        column: x => x.PlotThreadId,
                        principalTable: "PlotThreads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CharacterCardVersions_CharacterCardId_VersionNumber",
                table: "CharacterCardVersions",
                columns: new[] { "CharacterCardId", "VersionNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlotThreadVersions_PlotThreadId_VersionNumber",
                table: "PlotThreadVersions",
                columns: new[] { "PlotThreadId", "VersionNumber" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CharacterCardVersions");

            migrationBuilder.DropTable(
                name: "PlotThreadVersions");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "PlotThreads");

            migrationBuilder.DropColumn(
                name: "IsArchived",
                table: "CharacterCards");
        }
    }
}
