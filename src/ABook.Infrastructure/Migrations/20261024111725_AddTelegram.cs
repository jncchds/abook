using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTelegram : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "Clarifications",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "BookChatMessages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Origin",
                table: "AgentMessages",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TelegramBindCodes",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    CodeHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    ExpiresAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UsedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramBindCodes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramBindCodes_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramBindings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ChatId = table.Column<long>(type: "bigint", nullable: false),
                    ThreadId = table.Column<int>(type: "integer", nullable: true),
                    ChatType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ChatTitle = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    Level = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    ControlAdminsOnly = table.Column<bool>(type: "boolean", nullable: false),
                    FileFormat = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false, defaultValue: "epub"),
                    LiveDrafts = table.Column<bool>(type: "boolean", nullable: false, defaultValue: true),
                    StatusMessageId = table.Column<int>(type: "integer", nullable: true),
                    CreatedByUserId = table.Column<int>(type: "integer", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    RevokedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true),
                    RevokedReason = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramBindings", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramBindings_Books_BookId",
                        column: x => x.BookId,
                        principalTable: "Books",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "TelegramMessageLinks",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BindingId = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    RefId = table.Column<int>(type: "integer", nullable: false),
                    MessageId = table.Column<int>(type: "integer", nullable: false),
                    ContentHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TelegramMessageLinks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TelegramMessageLinks_TelegramBindings_BindingId",
                        column: x => x.BindingId,
                        principalTable: "TelegramBindings",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramBindCodes_BookId",
                table: "TelegramBindCodes",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramBindCodes_CodeHash",
                table: "TelegramBindCodes",
                column: "CodeHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramBindings_BookId",
                table: "TelegramBindings",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_TelegramBindings_ChatId_ThreadId",
                table: "TelegramBindings",
                columns: new[] { "ChatId", "ThreadId" },
                unique: true,
                filter: "\"RevokedAt\" IS NULL")
                .Annotation("Npgsql:NullsDistinct", false);

            migrationBuilder.CreateIndex(
                name: "IX_TelegramMessageLinks_BindingId_Kind_RefId",
                table: "TelegramMessageLinks",
                columns: new[] { "BindingId", "Kind", "RefId" });

            migrationBuilder.CreateIndex(
                name: "IX_TelegramMessageLinks_BindingId_MessageId",
                table: "TelegramMessageLinks",
                columns: new[] { "BindingId", "MessageId" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "TelegramBindCodes");

            migrationBuilder.DropTable(
                name: "TelegramMessageLinks");

            migrationBuilder.DropTable(
                name: "TelegramBindings");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "Clarifications");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "BookChatMessages");

            migrationBuilder.DropColumn(
                name: "Origin",
                table: "AgentMessages");
        }
    }
}
