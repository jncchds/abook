using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace ABook.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class PerUserDefaultPresets : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "DefaultPresetId",
                table: "Users",
                type: "integer",
                nullable: true);

            // ── Data: the global and user-level configurations go away ──────────────
            // 1. Keep one configuration per scope (the newest), so the unique BookId index can be created.
            // 2. Every book without its own configuration gets a copy of what it used to resolve to:
            //    its owner's user-level configuration, otherwise the global one.
            // 3. Every user gets a private "Default" preset copied from their user-level configuration
            //    (otherwise the global one) and it becomes their default preset.
            // 4. The global and user-level rows are deleted.
            const string settings = """
                "Provider", "ModelName", "Endpoint", "ApiKey", "EmbeddingModelName", "Temperature", "TimeoutMs",
                "ReasoningEffort", "MaxTokens", "ImageProvider", "ImageEndpoint", "ImageApiKey", "ImageModel",
                "ImageSteps", "ImageCfg", "ImagePortraitWidth", "ImagePortraitHeight", "ImageLandscapeWidth",
                "ImageLandscapeHeight", "ImageTimeoutMs", "ImageVisualStyle", "ImagesPerChapter", "UnloadLlmBeforeRender"
                """;
            const string sourceSettings = """
                src."Provider", src."ModelName", src."Endpoint", src."ApiKey", src."EmbeddingModelName", src."Temperature", src."TimeoutMs",
                src."ReasoningEffort", src."MaxTokens", src."ImageProvider", src."ImageEndpoint", src."ImageApiKey", src."ImageModel",
                src."ImageSteps", src."ImageCfg", src."ImagePortraitWidth", src."ImagePortraitHeight", src."ImageLandscapeWidth",
                src."ImageLandscapeHeight", src."ImageTimeoutMs", src."ImageVisualStyle", src."ImagesPerChapter", src."UnloadLlmBeforeRender"
                """;
            // The configuration a user (or a book's owner) used to fall back to: their own, else the global one.
            static string FallbackFor(string userIdExpr) => $"""
                SELECT * FROM "LlmConfigurations" c
                WHERE c."BookId" IS NULL AND (c."UserId" = {userIdExpr} OR c."UserId" IS NULL)
                ORDER BY c."UserId" IS NULL, c."Id" DESC
                LIMIT 1
                """;

            migrationBuilder.Sql("""
                DELETE FROM "LlmConfigurations" a USING "LlmConfigurations" b
                WHERE a."Id" < b."Id"
                  AND a."BookId" IS NOT DISTINCT FROM b."BookId"
                  AND (a."BookId" IS NOT NULL OR a."UserId" IS NOT DISTINCT FROM b."UserId");
                """);

            migrationBuilder.Sql($"""
                INSERT INTO "LlmConfigurations" ("BookId", {settings})
                SELECT b."Id", {sourceSettings}
                FROM "Books" b
                CROSS JOIN LATERAL ({FallbackFor("b.\"UserId\"")}) src
                WHERE NOT EXISTS (SELECT 1 FROM "LlmConfigurations" x WHERE x."BookId" = b."Id");
                """);

            migrationBuilder.Sql($"""
                WITH created AS (
                    INSERT INTO "LlmPresets" ("UserId", "Name", "CreatedAt", "UpdatedAt", {settings})
                    SELECT u."Id", 'Default', now(), now(), {sourceSettings}
                    FROM "Users" u
                    CROSS JOIN LATERAL ({FallbackFor("u.\"Id\"")}) src
                    RETURNING "Id", "UserId"
                )
                UPDATE "Users" u SET "DefaultPresetId" = created."Id"
                FROM created WHERE created."UserId" = u."Id";
                """);

            migrationBuilder.Sql("""DELETE FROM "LlmConfigurations" WHERE "BookId" IS NULL;""");

            migrationBuilder.DropForeignKey(
                name: "FK_LlmConfigurations_Users_UserId",
                table: "LlmConfigurations");

            migrationBuilder.DropForeignKey(
                name: "FK_LlmPresets_Users_UserId",
                table: "LlmPresets");

            migrationBuilder.DropIndex(
                name: "IX_LlmConfigurations_BookId",
                table: "LlmConfigurations");

            migrationBuilder.DropIndex(
                name: "IX_LlmConfigurations_UserId",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "UserId",
                table: "LlmConfigurations");

            migrationBuilder.AlterColumn<int>(
                name: "BookId",
                table: "LlmConfigurations",
                type: "integer",
                nullable: false,
                defaultValue: 0,
                oldClrType: typeof(int),
                oldType: "integer",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_DefaultPresetId",
                table: "Users",
                column: "DefaultPresetId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmConfigurations_BookId",
                table: "LlmConfigurations",
                column: "BookId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_LlmPresets_Users_UserId",
                table: "LlmPresets",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.Cascade);

            migrationBuilder.AddForeignKey(
                name: "FK_Users_LlmPresets_DefaultPresetId",
                table: "Users",
                column: "DefaultPresetId",
                principalTable: "LlmPresets",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_LlmPresets_Users_UserId",
                table: "LlmPresets");

            migrationBuilder.DropForeignKey(
                name: "FK_Users_LlmPresets_DefaultPresetId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_DefaultPresetId",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_LlmConfigurations_BookId",
                table: "LlmConfigurations");

            migrationBuilder.DropColumn(
                name: "DefaultPresetId",
                table: "Users");

            migrationBuilder.AlterColumn<int>(
                name: "BookId",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true,
                oldClrType: typeof(int),
                oldType: "integer");

            migrationBuilder.AddColumn<int>(
                name: "UserId",
                table: "LlmConfigurations",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LlmConfigurations_BookId",
                table: "LlmConfigurations",
                column: "BookId");

            migrationBuilder.CreateIndex(
                name: "IX_LlmConfigurations_UserId",
                table: "LlmConfigurations",
                column: "UserId");

            migrationBuilder.AddForeignKey(
                name: "FK_LlmConfigurations_Users_UserId",
                table: "LlmConfigurations",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);

            migrationBuilder.AddForeignKey(
                name: "FK_LlmPresets_Users_UserId",
                table: "LlmPresets",
                column: "UserId",
                principalTable: "Users",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }
    }
}
