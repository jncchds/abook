using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for user-level operations: profile, LLM configuration, presets, and book generation.</summary>
public class UserMcpTools : McpToolBase
{
    private readonly IBookRepository _repo;
    private readonly IUserRepository _users;
    private readonly AgentRunnerService _runner;

    public UserMcpTools(
        IBookRepository repo,
        IUserRepository users,
        AgentRunnerService runner,
        IHttpContextAccessor http)
        : base(http)
    {
        _repo = repo;
        _users = users;
        _runner = runner;
    }



    // ── User profile ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_current_user", ReadOnly = true)]
    [Description("Get the profile of the currently authenticated user, including their username, admin status, and API token availability.")]
    public async Task<string> GetCurrentUser()
    {
        var userId = CurrentUserId();
        var user = await _users.GetByIdAsync(userId)
            ?? throw new McpException("Current user not found.");
        return JsonSerializer.Serialize(new
        {
            user.Id,
            user.Username,
            user.IsAdmin,
            hasApiToken = user.ApiToken is not null,
            user.CreatedAt
        }, JsonOptions);
    }

    // ── LLM configuration ─────────────────────────────────────────────────────

    [McpServerTool(Name = "get_llm_config", ReadOnly = true)]
    [Description("Get the effective LLM configuration for the current user. Returns the user-level default config if set, otherwise the global default. Does not return per-book overrides.")]
    public async Task<string> GetLlmConfig()
    {
        var userId = CurrentUserId();
        // User-level default first, then global fallback
        var config = await _repo.GetLlmConfigAsync(null, userId)
                  ?? await _repo.GetLlmConfigAsync(null, null);
        if (config is null)
            return JsonSerializer.Serialize(new { exists = false }, JsonOptions);
        return JsonSerializer.Serialize(new
        {
            config.Id,
            Provider = config.Provider.ToString(),
            config.ModelName,
            config.Endpoint,
            hasApiKey = !string.IsNullOrEmpty(config.ApiKey),
            config.EmbeddingModelName,
            image = ImageSettingsView(config),
            isUserDefault = config.UserId == userId,
            isGlobal = config.UserId == null && config.BookId == null
        }, JsonOptions);
    }

    [McpServerTool(Name = "set_llm_config")]
    [Description("Set or update the user-level default LLM configuration. This applies to all books that do not have a per-book LLM override. Valid providers: Ollama, OpenAI, GoogleAIStudio.")]
    public async Task<string> SetLlmConfig(
        [Description("LLM provider name. Valid values: Ollama, OpenAI, GoogleAIStudio.")] string provider,
        [Description("Model name to use (e.g. llama3, gpt-4o, gemini-2.0-flash).")] string modelName,
        [Description("API endpoint URL. Required for Ollama; optional for OpenAI-compatible providers.")] string endpoint = "",
        [Description("API key. Required for OpenAI and GoogleAIStudio.")] string? apiKey = null,
        [Description("Embedding model name (e.g. nomic-embed-text, text-embedding-3-small). Leave null to use the generation model.")] string? embeddingModelName = null,
        [Description("Image generation server URL, e.g. http://gpu-box:8000 (Z-Image) or http://gpu-box:8888 (Unsloth Studio). Empty string disables image generation; null keeps the current value.")] string? imageEndpoint = null,
        [Description("Image provider: ZImage or UnslothStudio. Null keeps the current value (ZImage when none is set).")] string? imageProvider = null,
        [Description("API key for the image server (Unsloth Studio: an sk-unsloth-… key). Empty string clears it; null keeps the current value.")] string? imageApiKey = null,
        [Description("Unsloth Studio only: the image model to load when none is loaded — a Hub repo id, optionally ':' plus a checkpoint file in it (e.g. unsloth/Z-Image-Turbo-GGUF:z-image-turbo-Q8_0.gguf). Empty string clears it; null keeps the current value.")] string? imageModel = null,
        [Description("Visual style the illustration planner writes into every image prompt (e.g. 'moody watercolour, muted palette'). Null keeps the current value.")] string? imageVisualStyle = null,
        [Description("Image sampling steps (Z-Image Turbo: 8). Null keeps the current value.")] int? imageSteps = null,
        [Description("Image CFG scale (Z-Image Turbo: 1.0; negative prompts only take effect above 1). Null keeps the current value.")] double? imageCfg = null,
        [Description("Unload the Ollama model before rendering images, to free VRAM (Unsloth Studio frees it by itself). Null keeps the current value.")] bool? unloadLlmBeforeRender = null,
        [Description("Base number of illustrations planned per chapter (default 2); the planner may go over it when a chapter needs more. 0 resets to the default; null keeps the current value.")] int? imagesPerChapter = null)
    {
        if (!Enum.TryParse<LlmProvider>(provider, ignoreCase: true, out var providerEnum))
            throw new McpException($"Unknown provider '{provider}'. Valid values: Ollama, OpenAI, GoogleAIStudio.");
        ImageProvider? imageProviderEnum = null;
        if (!string.IsNullOrWhiteSpace(imageProvider))
        {
            if (!Enum.TryParse<ImageProvider>(imageProvider, ignoreCase: true, out var ip))
                throw new McpException($"Unknown image provider '{imageProvider}'. Valid values: {string.Join(", ", Enum.GetNames<ImageProvider>())}.");
            imageProviderEnum = ip;
        }

        var userId = CurrentUserId();
        var existing = await _repo.GetLlmConfigAsync(null, userId);
        var config = existing ?? new LlmConfiguration { UserId = userId };
        config.Provider = providerEnum;
        config.ModelName = modelName;
        config.Endpoint = endpoint;
        if (apiKey is not null) config.ApiKey = apiKey;
        if (embeddingModelName is not null) config.EmbeddingModelName = embeddingModelName;
        if (imageEndpoint is not null)
        {
            config.ImageEndpoint = string.IsNullOrWhiteSpace(imageEndpoint) ? null : imageEndpoint.Trim();
            config.ImageProvider ??= ImageProvider.ZImage;
        }
        if (imageProviderEnum.HasValue) config.ImageProvider = imageProviderEnum;
        if (imageApiKey is not null) config.ImageApiKey = string.IsNullOrWhiteSpace(imageApiKey) ? null : imageApiKey.Trim();
        if (imageModel is not null) config.ImageModel = string.IsNullOrWhiteSpace(imageModel) ? null : imageModel.Trim();
        if (imageVisualStyle is not null) config.ImageVisualStyle = imageVisualStyle;
        if (imageSteps.HasValue) config.ImageSteps = imageSteps;
        if (imageCfg.HasValue) config.ImageCfg = imageCfg;
        if (unloadLlmBeforeRender.HasValue) config.UnloadLlmBeforeRender = unloadLlmBeforeRender.Value;
        if (imagesPerChapter.HasValue) config.ImagesPerChapter = imagesPerChapter > 0 ? imagesPerChapter : null;
        await _repo.UpsertLlmConfigAsync(config);
        return JsonSerializer.Serialize(new
        {
            saved = true,
            Provider = config.Provider.ToString(),
            config.ModelName,
            config.Endpoint,
            image = ImageSettingsView(config)
        }, JsonOptions);
    }

    private static object? ImageSettingsView(IImageGenSettings s) => s.HasImageGeneration()
        ? new
        {
            provider = (s.ImageProvider ?? ImageProvider.ZImage).ToString(),
            endpoint = s.ImageEndpoint,
            hasApiKey = !string.IsNullOrEmpty(s.ImageApiKey),
            model = s.ImageModel,
            steps = s.ImageSteps,
            cfg = s.ImageCfg,
            visualStyle = s.ImageVisualStyle,
            imagesPerChapter = s.ImagesPerChapterOrDefault(),
            s.UnloadLlmBeforeRender,
        }
        : null;

    // ── Presets ───────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_presets", ReadOnly = true)]
    [Description("List all LLM presets visible to the current user: their own presets and global presets. Global presets have userId=null.")]
    public async Task<string> ListPresets()
    {
        var userId = CurrentUserId();
        var presets = await _repo.GetPresetsAsync(userId);
        var result = presets.Select(p => new
        {
            p.Id,
            p.Name,
            Provider = p.Provider.ToString(),
            p.ModelName,
            p.Endpoint,
            hasApiKey = !string.IsNullOrEmpty(p.ApiKey),
            p.EmbeddingModelName,
            image = ImageSettingsView(p),
            isOwned = p.UserId == userId,
            isGlobal = p.UserId == null
        });
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "apply_preset")]
    [Description("Apply a saved LLM preset as the user-level default configuration. The preset values are copied into the user's LLM config. Use list_presets to find preset IDs.")]
    public async Task<string> ApplyPreset(
        [Description("The numeric ID of the preset to apply.")] int presetId)
    {
        var userId = CurrentUserId();
        var preset = await _repo.GetPresetAsync(presetId);
        if (preset is null || (preset.UserId is not null && preset.UserId != userId))
            throw new McpException($"Preset {presetId} not found.");

        var existing = await _repo.GetLlmConfigAsync(null, userId);
        var config = existing ?? new LlmConfiguration { UserId = userId };
        config.Provider = preset.Provider;
        config.ModelName = preset.ModelName;
        config.Endpoint = preset.Endpoint;
        config.ApiKey = preset.ApiKey;
        config.EmbeddingModelName = preset.EmbeddingModelName;
        config.Temperature = preset.Temperature ?? 0.8f;
        config.TimeoutMs = preset.TimeoutMs;
        config.ReasoningEffort = preset.ReasoningEffort;
        config.MaxTokens = preset.MaxTokens;
        config.CopyImageSettingsFrom(preset);
        await _repo.UpsertLlmConfigAsync(config);
        return JsonSerializer.Serialize(new
        {
            applied = true,
            presetName = preset.Name,
            Provider = config.Provider.ToString(),
            config.ModelName
        }, JsonOptions);
    }

    // ── Generate book ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "generate_book")]
    [Description("Create a new book project and immediately start the full autonomous writing workflow: Story Bible → Characters → Plot Threads → Chapter Outlines → Write each chapter → Continuity check → Edit each chapter. The agent will ask clarifying questions during planning — use get_agent_status to check for WaitingForInput and answer_agent_question to respond. Returns the new book ID to use with other tools.")]
    public async Task<string> GenerateBook(
        [Description("The book title.")] string title,
        [Description("A concise synopsis of the book's premise and plot.")] string premise,
        [Description("The genre (e.g. Fantasy, Thriller, Romance, Sci-Fi).")] string genre,
        [Description("Target number of chapters.")] int targetChapterCount,
        [Description("Language for the book text (e.g. English, Spanish). Defaults to English.")] string language = "English")
    {
        var userId = CurrentUserId();

        // Create the book
        var book = await _repo.AddAsync(new Book
        {
            Title = title,
            Premise = premise,
            Genre = genre,
            TargetChapterCount = targetChapterCount,
            Language = language,
            UserId = userId
        });

        // Start the full workflow in background (a brand-new book can only be refused for capacity)
        var start = _runner.TryStart(book.Id, AgentRole.Planner, null, (o, c) => o.StartWorkflowAsync(book.Id, c));
        var workflowStarted = start == RunStartResult.Success;

        return JsonSerializer.Serialize(new
        {
            book.Id,
            book.Title,
            book.Genre,
            book.Premise,
            book.Language,
            book.TargetChapterCount,
            workflowStarted,
            message = workflowStarted
                ? $"Book created with ID {book.Id}. Full workflow started. Use get_agent_status with bookId={book.Id} to monitor progress. The agent will pause at WaitingForInput state when it needs clarification — use answer_agent_question to respond."
                : $"Book created with ID {book.Id}, but the workflow was not started: {start.ToMessage()} Start it later with start_workflow."
        }, JsonOptions);
    }

}
