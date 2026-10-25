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
    [Description("Get LLM settings. Without bookId: the current user's default preset, which is copied into every new book. With bookId: that book's own settings (a book never follows a preset after it is created).")]
    public async Task<string> GetLlmConfig(
        [Description("A book ID to read that book's settings; omit for the user's default preset.")] int? bookId = null)
    {
        if (bookId is int id)
        {
            await EnsureBookOwnershipAsync(id, _repo);
            var config = await _repo.GetLlmConfigAsync(id);
            if (config is null)
                return JsonSerializer.Serialize(new { exists = false, message = LlmConfiguration.MissingMessage }, JsonOptions);
            return JsonSerializer.Serialize(new
            {
                bookId = id,
                Provider = config.Provider.ToString(),
                config.ModelName,
                config.Endpoint,
                hasApiKey = !string.IsNullOrEmpty(config.ApiKey),
                config.EmbeddingModelName,
                image = ImageSettingsView(config),
            }, JsonOptions);
        }

        var userId = CurrentUserId();
        var preset = await _repo.GetDefaultPresetAsync(userId);
        if (preset is null)
            return JsonSerializer.Serialize(new { exists = false, message = "No default preset — new books get no LLM settings. Pick one with apply_preset or create one with set_llm_config." }, JsonOptions);
        return JsonSerializer.Serialize(new
        {
            defaultPresetId = preset.Id,
            presetName = preset.Name,
            isShared = preset.UserId == null,
            Provider = preset.Provider.ToString(),
            preset.ModelName,
            preset.Endpoint,
            hasApiKey = !string.IsNullOrEmpty(preset.ApiKey),
            preset.EmbeddingModelName,
            image = ImageSettingsView(preset),
        }, JsonOptions);
    }

    [McpServerTool(Name = "set_llm_config")]
    [Description("Set LLM settings. Without bookId: updates the current user's default preset (copied into every new book; existing books keep their own settings). If the default is a shared preset or there is none, a private preset named 'Default' is created from it and made the default. With bookId: updates only that book's settings. Valid providers: Ollama, OpenAI, GoogleAIStudio.")]
    public async Task<string> SetLlmConfig(
        [Description("LLM provider name. Valid values: Ollama, OpenAI, GoogleAIStudio, OpenAICompatible (LM Studio, OpenRouter, vLLM and similar; reads reasoning_content).")] string provider,
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
        [Description("Base number of illustrations planned per chapter (default 2); the planner may go over it when a chapter needs more. 0 resets to the default; null keeps the current value.")] int? imagesPerChapter = null,
        [Description("A book ID to change only that book's settings; omit to change the user's default preset.")] int? bookId = null)
    {
        if (!Enum.TryParse<LlmProvider>(provider, ignoreCase: true, out var providerEnum))
            throw new McpException($"Unknown provider '{provider}'. Valid values: {string.Join(", ", Enum.GetNames<LlmProvider>())}.");
        ImageProvider? imageProviderEnum = null;
        if (!string.IsNullOrWhiteSpace(imageProvider))
        {
            if (!Enum.TryParse<ImageProvider>(imageProvider, ignoreCase: true, out var ip))
                throw new McpException($"Unknown image provider '{imageProvider}'. Valid values: {string.Join(", ", Enum.GetNames<ImageProvider>())}.");
            imageProviderEnum = ip;
        }

        void ApplyImageSettings(IEditableImageGenSettings s)
        {
            if (imageEndpoint is not null)
            {
                s.ImageEndpoint = string.IsNullOrWhiteSpace(imageEndpoint) ? null : imageEndpoint.Trim();
                s.ImageProvider ??= ImageProvider.ZImage;
            }
            if (imageProviderEnum.HasValue) s.ImageProvider = imageProviderEnum;
            if (imageApiKey is not null) s.ImageApiKey = string.IsNullOrWhiteSpace(imageApiKey) ? null : imageApiKey.Trim();
            if (imageModel is not null) s.ImageModel = string.IsNullOrWhiteSpace(imageModel) ? null : imageModel.Trim();
            if (imageVisualStyle is not null) s.ImageVisualStyle = imageVisualStyle;
            if (imageSteps.HasValue) s.ImageSteps = imageSteps;
            if (imageCfg.HasValue) s.ImageCfg = imageCfg;
            if (unloadLlmBeforeRender.HasValue) s.UnloadLlmBeforeRender = unloadLlmBeforeRender.Value;
            if (imagesPerChapter.HasValue) s.ImagesPerChapter = imagesPerChapter > 0 ? imagesPerChapter : null;
        }

        if (bookId is int id)
        {
            await EnsureBookOwnershipAsync(id, _repo);
            var config = await _repo.GetLlmConfigAsync(id) ?? new LlmConfiguration { BookId = id };
            config.Provider = providerEnum;
            config.ModelName = modelName;
            config.Endpoint = endpoint;
            if (apiKey is not null) config.ApiKey = apiKey;
            if (embeddingModelName is not null) config.EmbeddingModelName = embeddingModelName;
            ApplyImageSettings(config);
            await _repo.UpsertLlmConfigAsync(config);
            return JsonSerializer.Serialize(new
            {
                saved = true,
                bookId = id,
                Provider = config.Provider.ToString(),
                config.ModelName,
                config.Endpoint,
                image = ImageSettingsView(config)
            }, JsonOptions);
        }

        var userId = CurrentUserId();
        var current = await _repo.GetDefaultPresetAsync(userId);
        var preset = current;
        if (preset is null || preset.UserId != userId)
        {
            // Never edit a shared preset on a user's behalf: fork it (or start fresh) as their own default.
            preset = new LlmPreset { UserId = userId, Name = UserProvisioningService.DefaultPresetName };
            if (current is not null) preset.CopyFrom(current);
        }
        preset.Provider = providerEnum;
        preset.ModelName = modelName;
        preset.Endpoint = endpoint;
        if (apiKey is not null) preset.ApiKey = apiKey;
        if (embeddingModelName is not null) preset.EmbeddingModelName = embeddingModelName;
        ApplyImageSettings(preset);
        if (preset.Id == 0)
        {
            preset = await _repo.CreatePresetAsync(preset);
            await SetDefaultPresetAsync(userId, preset.Id);
        }
        else
        {
            await _repo.UpdatePresetAsync(preset);
        }
        return JsonSerializer.Serialize(new
        {
            saved = true,
            defaultPresetId = preset.Id,
            presetName = preset.Name,
            Provider = preset.Provider.ToString(),
            preset.ModelName,
            preset.Endpoint,
            image = ImageSettingsView(preset)
        }, JsonOptions);
    }

    private async Task SetDefaultPresetAsync(int userId, int presetId)
    {
        var user = await _users.GetByIdAsync(userId) ?? throw new McpException("Current user not found.");
        user.DefaultPresetId = presetId;
        await _users.UpdateAsync(user);
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
    [Description("List all LLM presets visible to the current user: their own presets and shared presets (usable by everyone, changed only by admins). isDefault marks the one copied into new books.")]
    public async Task<string> ListPresets()
    {
        var userId = CurrentUserId();
        var presets = await _repo.GetPresetsAsync(userId);
        var defaultId = (await _repo.GetDefaultPresetAsync(userId))?.Id;
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
            isShared = p.UserId == null,
            isDefault = p.Id == defaultId
        });
        return JsonSerializer.Serialize(result, JsonOptions);
    }

    [McpServerTool(Name = "apply_preset")]
    [Description("Without bookId: make a preset the current user's default, which is copied into every new book (existing books keep their settings). With bookId: copy the preset's values into that book's settings once — the book does not follow later preset changes. Use list_presets to find preset IDs.")]
    public async Task<string> ApplyPreset(
        [Description("The numeric ID of the preset to apply.")] int presetId,
        [Description("A book ID to copy the preset into; omit to make the preset the user's default.")] int? bookId = null)
    {
        var userId = CurrentUserId();
        var preset = await _repo.GetPresetAsync(presetId);
        if (preset is null || (preset.UserId is not null && preset.UserId != userId))
            throw new McpException($"Preset {presetId} not found.");

        if (bookId is int id)
        {
            await EnsureBookOwnershipAsync(id, _repo);
            var config = new LlmConfiguration { BookId = id };
            config.CopyFrom(preset);
            await _repo.UpsertLlmConfigAsync(config);
            return JsonSerializer.Serialize(new
            {
                applied = true,
                bookId = id,
                presetName = preset.Name,
                Provider = preset.Provider.ToString(),
                preset.ModelName
            }, JsonOptions);
        }

        await SetDefaultPresetAsync(userId, preset.Id);
        return JsonSerializer.Serialize(new
        {
            defaultPresetId = preset.Id,
            presetName = preset.Name,
            Provider = preset.Provider.ToString(),
            preset.ModelName
        }, JsonOptions);
    }

    // ── Generate book ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "generate_book")]
    [Description("Create a new book project and immediately start the full autonomous writing workflow: Story Bible → Characters → Plot Threads → Chapter Outlines → Write each chapter → Continuity check → Edit each chapter. The planner may ask clarifying questions first — when get_agent_status shows WaitingForInput, answer the open ones from list_clarifications with update_clarification (the last answer resumes planning); other pauses are answered with answer_agent_question. Returns the new book ID to use with other tools.")]
    public async Task<string> GenerateBook(
        [Description("The book title.")] string title,
        [Description("A concise synopsis of the book's premise and plot.")] string premise,
        [Description("The genre (e.g. Fantasy, Thriller, Romance, Sci-Fi).")] string genre,
        [Description("Target number of chapters.")] int targetChapterCount,
        [Description("Language for the book text (e.g. English, Spanish). Defaults to English.")] string language = "English")
    {
        var userId = CurrentUserId();
        if (await _repo.GetDefaultPresetAsync(userId) is null)
            throw new McpException("You have no default preset, so a new book would have no LLM settings. Pick one with apply_preset or create one with set_llm_config first.");

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
                ? $"Book created with ID {book.Id}. Full workflow started. Use get_agent_status with bookId={book.Id} to monitor progress. The agent will pause at WaitingForInput state when it needs clarification — answer the open questions from list_clarifications with update_clarification."
                : $"Book created with ID {book.Id}, but the workflow was not started: {start.ToMessage()} Start it later with start_workflow."
        }, JsonOptions);
    }

}
