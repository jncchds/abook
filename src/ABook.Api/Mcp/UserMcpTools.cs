using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for user-level operations: profile, LLM configuration, presets, and book generation.</summary>
public class UserMcpTools
{
    private readonly IBookRepository _repo;
    private readonly IUserRepository _users;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunStateService _runState;
    private readonly IHttpContextAccessor _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UserMcpTools(
        IBookRepository repo,
        IUserRepository users,
        IServiceScopeFactory scopeFactory,
        AgentRunStateService runState,
        IHttpContextAccessor http)
    {
        _repo = repo;
        _users = users;
        _scopeFactory = scopeFactory;
        _runState = runState;
        _http = http;
    }

    private int CurrentUserId() =>
        int.Parse(_http.HttpContext!.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

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
        }, _json);
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
            return JsonSerializer.Serialize(new { exists = false }, _json);
        return JsonSerializer.Serialize(new
        {
            config.Id,
            Provider = config.Provider.ToString(),
            config.ModelName,
            config.Endpoint,
            hasApiKey = !string.IsNullOrEmpty(config.ApiKey),
            config.EmbeddingModelName,
            isUserDefault = config.UserId == userId,
            isGlobal = config.UserId == null && config.BookId == null
        }, _json);
    }

    [McpServerTool(Name = "set_llm_config")]
    [Description("Set or update the user-level default LLM configuration. This applies to all books that do not have a per-book LLM override. Valid providers: Ollama, OpenAI, AzureOpenAI, Anthropic, GoogleAIStudio.")]
    public async Task<string> SetLlmConfig(
        [Description("LLM provider name. Valid values: Ollama, OpenAI, AzureOpenAI, Anthropic, GoogleAIStudio.")] string provider,
        [Description("Model name to use (e.g. llama3, gpt-4o, gemini-2.0-flash).")] string modelName,
        [Description("API endpoint URL. Required for Ollama and Anthropic; optional for OpenAI-compatible providers.")] string endpoint = "",
        [Description("API key. Required for OpenAI, GoogleAIStudio, and Anthropic.")] string? apiKey = null,
        [Description("Embedding model name (e.g. nomic-embed-text, text-embedding-3-small). Leave null to use the generation model.")] string? embeddingModelName = null)
    {
        if (!Enum.TryParse<LlmProvider>(provider, ignoreCase: true, out var providerEnum))
            throw new McpException($"Unknown provider '{provider}'. Valid values: Ollama, OpenAI, AzureOpenAI, Anthropic, GoogleAIStudio.");

        var userId = CurrentUserId();
        var existing = await _repo.GetLlmConfigAsync(null, userId);
        var config = existing ?? new LlmConfiguration { UserId = userId };
        config.Provider = providerEnum;
        config.ModelName = modelName;
        config.Endpoint = endpoint;
        if (apiKey is not null) config.ApiKey = apiKey;
        if (embeddingModelName is not null) config.EmbeddingModelName = embeddingModelName;
        await _repo.UpsertLlmConfigAsync(config);
        return JsonSerializer.Serialize(new
        {
            saved = true,
            Provider = config.Provider.ToString(),
            config.ModelName,
            config.Endpoint
        }, _json);
    }

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
            isOwned = p.UserId == userId,
            isGlobal = p.UserId == null
        });
        return JsonSerializer.Serialize(result, _json);
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
        await _repo.UpsertLlmConfigAsync(config);
        return JsonSerializer.Serialize(new
        {
            applied = true,
            presetName = preset.Name,
            Provider = config.Provider.ToString(),
            config.ModelName
        }, _json);
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

        // Start the full workflow in background
        var ct = _runState.CreateRunCts(book.Id);
        _ = RunInBackground(book.Id, (o, c) => o.StartWorkflowAsync(book.Id, c), ct);

        return JsonSerializer.Serialize(new
        {
            book.Id,
            book.Title,
            book.Genre,
            book.Premise,
            book.Language,
            book.TargetChapterCount,
            workflowStarted = true,
            message = $"Book created with ID {book.Id}. Full workflow started. Use get_agent_status with bookId={book.Id} to monitor progress. The agent will pause at WaitingForInput state when it needs clarification — use answer_agent_question to respond."
        }, _json);
    }

    private async Task RunInBackground(int bookId, Func<IAgentOrchestrator, CancellationToken, Task> action, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        try { await action(orchestrator, ct); }
        catch (OperationCanceledException) { /* stopped by user */ }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<UserMcpTools>>();
            logger.LogError(ex, "Agent error during generate_book for book {BookId}", bookId);
        }
    }
}
