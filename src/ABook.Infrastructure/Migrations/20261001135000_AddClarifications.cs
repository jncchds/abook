using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClarifications : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ClarificationsStatus",
                table: "Books",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "Clarifications",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Source = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    Question = table.Column<string>(type: "text", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    AnsweredAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    IsArchived = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Clarifications", x => x.Id);
                    table.ForeignKey(
                        name: "FK_Clarifications_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Clarifications_BookId",
                table: "Clarifications",
                column: "BookId");

            // Backfill: every answered planning question from the chat (the clarification round and the
            // human-assisted notes after each phase) becomes an Imported clarification, timed like the
            // messages it came from. A question's answer is the next message of the same role — kept only
            // when it is an Answer, not another question.
            migrationBuilder.Sql("""
                INSERT INTO "Clarifications" ("BookId", "Source", "Question", "Answer", "AnsweredAt", "IsArchived", "CreatedAt", "UpdatedAt")
                SELECT q."BookId", 'Imported',
                       CASE WHEN NOT q."IsOptional" THEN q."Content"
                            ELSE 'Anything to add after the ' || CASE q."AgentRole"
                                WHEN 'StoryBibleAgent' THEN 'Story Bible'
                                WHEN 'CharactersAgent' THEN 'Characters'
                                WHEN 'PlotThreadsAgent' THEN 'Plot Threads'
                                ELSE 'Chapter Outlines' END || '?'
                       END,
                       btrim(a."Content"), a."CreatedAt", false, q."CreatedAt", a."CreatedAt"
                FROM "AgentMessages" q
                CROSS JOIN LATERAL (
                    SELECT m."MessageType", m."Content", m."CreatedAt"
                    FROM "AgentMessages" m
                    WHERE m."BookId" = q."BookId" AND m."AgentRole" = q."AgentRole"
                      AND m."ChapterId" IS NULL AND NOT m."IsDeleted" AND m."Id" > q."Id"
                      AND m."MessageType" IN ('Question', 'Answer')
                    ORDER BY m."Id"
                    LIMIT 1
                ) a
                WHERE q."MessageType" = 'Question' AND q."ChapterId" IS NULL AND NOT q."IsDeleted"
                  AND q."AgentRole" IN ('Planner', 'StoryBibleAgent', 'CharactersAgent', 'PlotThreadsAgent')
                  AND a."MessageType" = 'Answer' AND btrim(a."Content") <> ''
                ORDER BY q."Id";
                """);

            // Books that already went through the round (or past it) don't ask again.
            migrationBuilder.Sql("""
                UPDATE "Books" b SET "ClarificationsStatus" = 1
                WHERE b."StoryBibleStatus" = 1
                   OR EXISTS (SELECT 1 FROM "AgentMessages" m
                              WHERE m."BookId" = b."Id" AND m."AgentRole" = 'Planner'
                                AND m."MessageType" = 'Question' AND NOT m."IsOptional" AND m."ChapterId" IS NULL);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Clarifications");

            migrationBuilder.DropColumn(
                name: "ClarificationsStatus",
                table: "Books");
        }
    }
}
