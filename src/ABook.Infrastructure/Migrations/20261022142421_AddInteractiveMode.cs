using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractiveMode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "BookSnapshots",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Autonomous");

            migrationBuilder.AddColumn<string>(
                name: "ChatMemory",
                table: "Books",
                type: "text",
                nullable: false,
                defaultValue: "");

            migrationBuilder.AddColumn<int>(
                name: "ChatMemoryThroughMessageId",
                table: "Books",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Mode",
                table: "Books",
                type: "character varying(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Autonomous");

            migrationBuilder.AddColumn<DateTime>(
                name: "StoryBibleLockedAt",
                table: "Books",
                type: "timestamp with time zone",
                nullable: true);

            // Interactive mode replaces human-assisted mode. Converted books that already have prose start in their
            // writing stage (premise and Story Bible locked); the others start in their foundation stage.
            migrationBuilder.Sql("""
                UPDATE "Books" SET "Mode" = 'Interactive' WHERE "HumanAssisted";
                UPDATE "Books" b SET "StoryBibleLockedAt" = now()
                WHERE b."Mode" = 'Interactive' AND EXISTS (
                    SELECT 1 FROM "Chapters" c WHERE c."BookId" = b."Id" AND NOT c."IsArchived" AND c."Content" <> '');
                UPDATE "BookSnapshots" SET "Mode" = 'Interactive' WHERE "HumanAssisted";
                """);

            migrationBuilder.DropColumn(
                name: "HumanAssisted",
                table: "Books");

            migrationBuilder.CreateTable(
                name: "BookChatMessages",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Role = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Content = table.Column<string>(type: "text", nullable: false),
                    ConflictsJson = table.Column<string>(type: "text", nullable: false, defaultValue: "[]"),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BookChatMessages", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BookChatMessages_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ChatProposals",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Type = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: false),
                    Label = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    PayloadJson = table.Column<string>(type: "text", nullable: false),
                    Target = table.Column<string>(type: "character varying(400)", maxLength: 400, nullable: true),
                    DependsOnProposalId = table.Column<int>(type: "integer", nullable: true),
                    OptionGroup = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: true),
                    ConflictNote = table.Column<string>(type: "text", nullable: true),
                    Status = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    PreviewBefore = table.Column<string>(type: "text", nullable: false),
                    PreviewAfter = table.Column<string>(type: "text", nullable: false),
                    BeforeJson = table.Column<string>(type: "text", nullable: true),
                    AfterJson = table.Column<string>(type: "text", nullable: true),
                    BeforeVersionId = table.Column<int>(type: "integer", nullable: true),
                    AfterVersionId = table.Column<int>(type: "integer", nullable: true),
                    ResultNote = table.Column<string>(type: "text", nullable: true),
                    AppliedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChatProposals", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ChatProposals_BookChatMessages_MessageId",
                        column: x => x.MessageId,
                        principalTable: "BookChatMessages",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BookChatMessages_BookId",
                table: "BookChatMessages",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ChatProposals_BookId_Status",
                table: "ChatProposals",
                columns: new[] { "BookId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ChatProposals_MessageId",
                table: "ChatProposals",
                column: "MessageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChatProposals");

            migrationBuilder.DropTable(
                name: "BookChatMessages");

            migrationBuilder.AddColumn<bool>(
                name: "HumanAssisted",
                table: "Books",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.Sql("""UPDATE "Books" SET "HumanAssisted" = TRUE WHERE "Mode" = 'Interactive';""");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "BookSnapshots");

            migrationBuilder.DropColumn(
                name: "ChatMemory",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "ChatMemoryThroughMessageId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "Mode",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "StoryBibleLockedAt",
                table: "Books");
        }
    }
}
