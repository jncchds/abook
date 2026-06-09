using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveLlmConfigSeedData : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DeleteData(
                table: "LlmConfigurations",
                keyColumn: "Id",
                keyValue: 1);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.InsertData(
                table: "LlmConfigurations",
                columns: new[] { "Id", "ApiKey", "BookId", "EmbeddingModelName", "Endpoint", "ModelName", "Provider", "UserId" },
                values: new object[] { 1, null, null, "nomic-embed-text", "http://host.docker.internal:11434", "llama3", "Ollama", null });
        }
    }
}
