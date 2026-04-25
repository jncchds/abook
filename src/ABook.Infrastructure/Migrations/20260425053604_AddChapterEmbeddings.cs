using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;
using Pgvector;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddChapterEmbeddings : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            migrationBuilder.CreateTable(
                name: "ChapterEmbeddings",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BookId = table.Column<int>(type: "integer", nullable: false),
                    ChapterId = table.Column<int>(type: "integer", nullable: false),
                    ChapterNumber = table.Column<int>(type: "integer", nullable: false),
                    ChunkIndex = table.Column<int>(type: "integer", nullable: false),
                    Text = table.Column<string>(type: "text", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ChapterEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_ChapterEmbeddings_BookId",
                table: "ChapterEmbeddings",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_ChapterEmbeddings_BookId_ChapterId_ChunkIndex",
                table: "ChapterEmbeddings",
                columns: new[] { "BookId", "ChapterId", "ChunkIndex" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ChapterEmbeddings");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
