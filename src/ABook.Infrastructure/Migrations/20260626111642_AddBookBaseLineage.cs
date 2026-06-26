using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddBookBaseLineage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "BaseBookId",
                table: "Books",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "SettingsCopiedAt",
                table: "Books",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Books_BaseBookId",
                table: "Books",
                column: "BaseBookId");

            migrationBuilder.AddForeignKey(
                name: "FK_Books_Books_BaseBookId",
                table: "Books",
                column: "BaseBookId",
                principalTable: "Books",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Books_Books_BaseBookId",
                table: "Books");

            migrationBuilder.DropIndex(
                name: "IX_Books_BaseBookId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "BaseBookId",
                table: "Books");

            migrationBuilder.DropColumn(
                name: "SettingsCopiedAt",
                table: "Books");
        }
    }
}
