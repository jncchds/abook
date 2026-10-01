using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Services;

/// <summary>
/// Creates users. A new user gets a private "Default" preset built from the <c>LlmDefaults</c> section
/// (env <c>LlmDefaults__*</c>) as their default preset, when that section names a provider. The server
/// settings are only a starting point: there is no global LLM configuration that books fall back to.
/// </summary>
public class UserProvisioningService(
    IUserRepository users, IBookRepository repo, IConfiguration configuration, ILogger<UserProvisioningService> logger)
{
    public const string DefaultPresetName = "Default";

    public async Task<AppUser> CreateAsync(AppUser user)
    {
        user = await users.AddAsync(user);

        var preset = BuildServerDefaultPreset();
        if (preset is null) return user;

        preset.UserId = user.Id;
        preset = await repo.CreatePresetAsync(preset);
        user.DefaultPresetId = preset.Id;
        await users.UpdateAsync(user);
        logger.LogInformation("Created default preset {PresetId} ({Provider}/{Model}) for new user {UserId}.",
            preset.Id, preset.Provider, preset.ModelName, user.Id);
        return user;
    }

    /// <summary>A preset from the <c>LlmDefaults</c> settings, or null when they name no valid provider.</summary>
    private LlmPreset? BuildServerDefaultPreset()
    {
        var section = configuration.GetSection("LlmDefaults");
        if (!Enum.TryParse<LlmProvider>(section["Provider"], ignoreCase: true, out var provider)) return null;

        string? Value(string key) => string.IsNullOrWhiteSpace(section[key]) ? null : section[key]!.Trim();

        var preset = new LlmPreset
        {
            Name = DefaultPresetName,
            Provider = provider,
            ModelName = Value("ModelName") ?? "llama3",
            Endpoint = Value("Endpoint") ?? "http://localhost:11434",
            ApiKey = Value("ApiKey"),
            EmbeddingModelName = Value("EmbeddingModelName"),
            ImageEndpoint = Value("ImageEndpoint"),
            ImageApiKey = Value("ImageApiKey"),
            ImageModel = Value("ImageModel"),
            ImageVisualStyle = Value("ImageVisualStyle"),
        };
        if (preset.ImageEndpoint is not null)
            preset.ImageProvider = Enum.TryParse<ImageProvider>(section["ImageProvider"], true, out var ip) ? ip : ImageProvider.ZImage;
        if (int.TryParse(section["ImagesPerChapter"], out var imagesPerChapter) && imagesPerChapter > 0)
            preset.ImagesPerChapter = imagesPerChapter;
        return preset;
    }
}
