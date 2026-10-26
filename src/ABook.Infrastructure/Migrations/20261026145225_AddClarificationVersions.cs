using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddClarificationVersions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "ClarificationVersions",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ClarificationId = table.Column<int>(type: "integer", nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    VersionNumber = table.Column<int>(type: "integer", nullable: false),
                    Question = table.Column<string>(type: "text", nullable: false),
                    Answer = table.Column<string>(type: "text", nullable: false),
                    CreatedBy = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ClarificationVersions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ClarificationVersions_Clarifications_ClarificationId",
                        column: x => x.ClarificationId,
                        principalTable: "Clarifications",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ClarificationVersions_ClarificationId_VersionNumber",
                table: "ClarificationVersions",
                columns: new[] { "ClarificationId", "VersionNumber" },
                unique: true);

            // Every existing clarification starts with its current text as version 1.
            migrationBuilder.Sql("""
                INSERT INTO "ClarificationVersions" ("ClarificationId", "BookId", "VersionNumber", "Question", "Answer", "CreatedBy", "CreatedAt")
                SELECT "Id", "BookId", 1, "Question", "Answer", 'initial', "UpdatedAt" FROM "Clarifications";
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ClarificationVersions");
        }
    }
}
