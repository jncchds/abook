using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddIllustrations : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "ImageCfg",
                table: "LlmPresets",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageEndpoint",
                table: "LlmPresets",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageLandscapeHeight",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageLandscapeWidth",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagePortraitHeight",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagePortraitWidth",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageProvider",
                table: "LlmPresets",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageSteps",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageTimeoutMs",
                table: "LlmPresets",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageVisualStyle",
                table: "LlmPresets",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UnloadLlmBeforeRender",
                table: "LlmPresets",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<double>(
                name: "ImageCfg",
                table: "LlmConfigurations",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageEndpoint",
                table: "LlmConfigurations",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageLandscapeHeight",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageLandscapeWidth",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagePortraitHeight",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImagePortraitWidth",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageProvider",
                table: "LlmConfigurations",
                type: "character varying(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageSteps",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ImageTimeoutMs",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ImageVisualStyle",
                table: "LlmConfigurations",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "UnloadLlmBeforeRender",
                table: "LlmConfigurations",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "ImageSeed",
                table: "CharacterCards",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "VisualPrompt",
                table: "CharacterCards",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<string>(
                name: "VisualPromptSourceHash",
                table: "CharacterCards",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IllustrationsEnabled",
                table: "Books",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<string>(
                name: "IllustratorSystemPrompt",
                table: "Books",
                type: "text",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BookImages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Data = table.Column<byte[]>(type: "bytea", nullable: false),
                    ContentType = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Width = table.Column<int>(type: "integer", nullable: false),
                    Height = table.Column<int>(type: "integer", nullable: false),
                    Seed = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookImages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookImages_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "Illustrations",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ChapterId = table.Column<int>(type: "integer", nullable: true),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CharacterCardId = table.Column<int>(type: "integer", nullable: true),
                    ParagraphNumber = table.Column<int>(type: "integer", nullable: false),
                    AnchorText = table.Column<string>(type: "text", nullable: false),
                    Caption = table.Column<string>(type: "text", nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    NegativePrompt = table.Column<string>(type: "text", nullable: false),
                    Orientation = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    FocusCharacterId = table.Column<int>(type: "integer", nullable: true),
                    CharacterNamesJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    Seed = table.Column<long>(type: "bigint", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Error = table.Column<string>(type: "text", nullable: true),
                    ImageId = table.Column<int>(type: "integer", nullable: true),
                    SortOrder = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Illustrations", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Illustrations_BookImages_ImageId",
                        column: x => x.ImageId,
                        principalTable: "BookImages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_Illustrations_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Illustrations_Chapters_ChapterId",
                        column: x => x.ChapterId,
                        principalTable: "Chapters",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Illustrations_CharacterCards_CharacterCardId",
                        column: x => x.CharacterCardId,
                        principalTable: "CharacterCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_Illustrations_CharacterCards_FocusCharacterId",
                        column: x => x.FocusCharacterId,
                        principalTable: "CharacterCards",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookImages_BookId",
                table: "BookImages",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_Illustrations_BookId_ChapterId",
                table: "Illustrations",
                columns: new[] { "BookId", "ChapterId" });

            migrationBuilder.CreateIndex(
                name: "IX_Illustrations_ChapterId",
                table: "Illustrations",
                column: "ChapterId");

            migrationBuilder.CreateIndex(
                name: "IX_Illustrations_CharacterCardId",
                table: "Illustrations",
                column: "CharacterCardId");

            migrationBuilder.CreateIndex(
                name: "IX_Illustrations_FocusCharacterId",
                table: "Illustrations",
                column: "FocusCharacterId");

            migrationBuilder.CreateIndex(
                name: "IX_Illustrations_ImageId",
                table: "Illustrations",
                column: "ImageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Illustrations");

            migrationBuilder.DropTable(
                name: "BookImages");

            migrationBuilder.DropColumn(
                name: "ImageCfg",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageEndpoint",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageLandscapeHeight",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageLandscapeWidth",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImagePortraitHeight",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImagePortraitWidth",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageProvider",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageSteps",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageTimeoutMs",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageVisualStyle",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "UnloadLlmBeforeRender",
                table: "LlmPresets");

            migrationBuilder.DropColumn(
                name: "ImageCfg",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageEndpoint",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageLandscapeHeight",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageLandscapeWidth",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImagePortraitHeight",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImagePortraitWidth",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageProvider",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageSteps",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageTimeoutMs",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageVisualStyle",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "UnloadLlmBeforeRender",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "ImageSeed",
                table: "CharacterCards");

            migrationBuilder.DropColumn(
                name: "VisualPrompt",
                table: "CharacterCards");

            migrationBuilder.DropColumn(
                name: "VisualPromptSourceHash",
                table: "CharacterCards");

            migrationBuilder.DropColumn(
                name: "IllustrationsEnabled",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "IllustratorSystemPrompt",
                table: "Books");
        }
    }
}
