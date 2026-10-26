using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books")]
[Authorize]
public class BooksController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly IVectorStoreService _vectorStore;
    private readonly ILlmProviderFactory _llmFactory;
    private readonly ILogger<BooksController> _logger;

    public BooksController(
        IBookRepository repo,
        IVectorStoreService vectorStore,
        ILlmProviderFactory llmFactory,
        ILogger<BooksController> logger)
    {
        _repo = repo;
        _vectorStore = vectorStore;
        _llmFactory = llmFactory;
        _logger = logger;
    }

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value)
            : (int?)null;

    private static string NormalizeGenre(string g) =>
        string.Join(", ", g.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0));

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _repo.GetAllAsync(CurrentUserId));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;

        var book = await _repo.GetByIdWithDetailsAsync(id);
        return Ok(book);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookRequest req)
    {
        Book? baseBook = null;
        if (req.BaseBookId is int baseBookId)
        {
            baseBook = await _repo.GetByIdWithDetailsAsync(baseBookId);
            if (baseBook is null) return NotFound($"Base book {baseBookId} not found.");
            if (baseBook.UserId is not null && baseBook.UserId != CurrentUserId) return Forbid();
        }

        var mode = req.Mode ?? baseBook?.Mode ?? BookMode.Autonomous;
        var target = req.TargetChapterCount ?? baseBook?.TargetChapterCount ?? 0;
        if (mode == BookMode.Autonomous && target < 1)
            return BadRequest(new { message = "An Autonomous book needs a target chapter count." });

        var book = new Book
        {
            Title = req.Title,
            Premise = req.Premise,
            Genre = NormalizeGenre(req.Genre),
            TargetChapterCount = Math.Max(0, target),
            Language = baseBook?.Language ?? req.Language ?? "English",
            Mode = mode,
            UserId = CurrentUserId
        };

        if (baseBook is not null)
        {
            book.BaseBookId = baseBook.Id;
            book.SettingsCopiedAt = DateTime.UtcNow;
            book.StoryBibleSystemPrompt = baseBook.StoryBibleSystemPrompt;
            book.CharactersSystemPrompt = baseBook.CharactersSystemPrompt;
            book.PlotThreadsSystemPrompt = baseBook.PlotThreadsSystemPrompt;
            book.ChapterOutlinesSystemPrompt = baseBook.ChapterOutlinesSystemPrompt;
            book.WriterSystemPrompt = baseBook.WriterSystemPrompt;
            book.EditorSystemPrompt = baseBook.EditorSystemPrompt;
            book.ContinuityCheckerSystemPrompt = baseBook.ContinuityCheckerSystemPrompt;
            book.IllustratorSystemPrompt = baseBook.IllustratorSystemPrompt;
            book.IllustrationsEnabled = baseBook.IllustrationsEnabled;
            book.ChatPartRequestsEnabled = baseBook.ChatPartRequestsEnabled;
        }

        var created = await _repo.AddAsync(book);

        // A sequel keeps the base book's LLM settings rather than the owner's default preset.
        if (baseBook is not null && await _repo.GetLlmConfigAsync(baseBook.Id) is { } sourceConfig)
        {
            var copy = new LlmConfiguration { BookId = created.Id };
            copy.CopyFrom(sourceConfig);
            await _repo.UpsertLlmConfigAsync(copy);
        }

        // An Interactive book's chat is not started here: the author first reviews the premise and LLM
        // settings, then presses Start in the book chat (BookChatController.Kickoff).
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateBookRequest req, [FromServices] BookContentService content)
    {
        var ownershipResult = await ControllerExtensions.RequireBookOwnershipAndLoadAsync(this, id, _repo);
        if (ownershipResult.Error is not null) return ownershipResult.Error;
        var book = ownershipResult.Book!;

        var before = BookContentService.SnapshotOf(book, EditSources.UserEdit);
        var mode = req.Mode ?? book.Mode;
        if (mode == BookMode.Autonomous && req.TargetChapterCount < 1)
            return BadRequest(new { message = "An Autonomous book needs a target chapter count." });
        if (BookContentService.IsBibleLocked(book) && req.Premise.Trim() != book.Premise.Trim())
            return Conflict(new { message = new StoryBibleLockedException().Message });

        book.Title = req.Title;
        book.Premise = req.Premise;
        book.Genre = NormalizeGenre(req.Genre);
        book.TargetChapterCount = Math.Max(0, req.TargetChapterCount);
        book.Status = req.Status;
        book.Language = req.Language ?? book.Language;
        book.StoryBibleSystemPrompt = req.StoryBibleSystemPrompt;
        book.CharactersSystemPrompt = req.CharactersSystemPrompt;
        book.PlotThreadsSystemPrompt = req.PlotThreadsSystemPrompt;
        book.ChapterOutlinesSystemPrompt = req.ChapterOutlinesSystemPrompt;
        book.WriterSystemPrompt = req.WriterSystemPrompt;
        book.EditorSystemPrompt = req.EditorSystemPrompt;
        book.ContinuityCheckerSystemPrompt = req.ContinuityCheckerSystemPrompt;
        book.IllustratorSystemPrompt = req.IllustratorSystemPrompt;
        book.Mode = mode;
        if (req.IllustrationsEnabled is bool illustrate) book.IllustrationsEnabled = illustrate;
        if (req.ShowSectionsInOutput is bool showSections) book.ShowSectionsInOutput = showSections;
        if (req.ChatPartRequestsEnabled is bool partRequests) book.ChatPartRequestsEnabled = partRequests;
        await _repo.UpdateAsync(book);
        await content.RecordBookEditAsync(before, book, EditSources.UserEdit);
        return Ok(book);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id, [FromServices] ABook.Api.Telegram.TelegramBindingService telegram)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;

        // Tell the book's Telegram chats; their bindings are deleted with the book.
        try { await telegram.NotifyBookDeletedAsync(id); }
        catch (Exception ex) { _logger.LogWarning(ex, "[Book {BookId}] Telegram unbind notices failed.", id); }
        await _repo.DeleteAsync(id);
        try { await _vectorStore.DeleteCollectionAsync(id); } catch { /* non-fatal */ }
        return NoContent();
    }

    [HttpGet("{id:int}/token-usage")]
    public async Task<IActionResult> GetTokenUsage(int id)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;

        var records = await _repo.GetTokenUsageAsync(id);
        return Ok(records.Select(r => new
        {
            r.Id,
            r.ChapterId,
            agentRole = r.AgentRole.ToString(),
            r.PromptTokens,
            r.CompletionTokens,
            r.StepLabel,
            r.Endpoint,
            r.ModelName,
            r.Failed,
            r.FailureReason,
            r.CreatedAt
        }));
    }

    [HttpDelete("{id:int}/token-usage")]
    public async Task<IActionResult> DeleteTokenUsage(int id)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;

        await _repo.DeleteTokenUsageAsync(id);
        return NoContent();
    }

    /// <summary>
    /// The built-in system prompts the agents use when a book has no override. They are returned as
    /// templates with their {PLACEHOLDER} tokens intact — an override copied from them is interpolated
    /// at run time exactly like the default, so it stays in sync with the book's current details.
    /// </summary>
    [HttpGet("{id:int}/default-prompts")]
    public async Task<IActionResult> GetDefaultPrompts(int id)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;

        return Ok(new
        {
            storyBibleSystemPrompt = DefaultPrompts.StoryBible,
            charactersSystemPrompt = DefaultPrompts.Characters,
            plotThreadsSystemPrompt = DefaultPrompts.PlotThreads,
            chapterOutlinesSystemPrompt = DefaultPrompts.ChapterOutlines,
            writerSystemPrompt = DefaultPrompts.Writer,
            editorSystemPrompt = DefaultPrompts.Editor,
            // One override serves both check modes; the per-chapter prompt is the one used during writing.
            continuityCheckerSystemPrompt = DefaultPrompts.ContinuityCheckerPerChapter,
            illustratorSystemPrompt = DefaultPrompts.Illustrator,
        });
    }

    /// <summary>
    /// Diagnostic endpoint: reports embedding configuration and optionally runs a RAG search.
    /// GET /api/books/{id}/rag/search?query=the+protagonist+woke+up&topK=5
    /// Returns 200 with diagnostic info. The "results" array is empty when no chapters have been indexed
    /// or when no query is provided; "error" is non-null when the embedding call itself failed.
    /// </summary>
    [HttpGet("{id:int}/rag/search")]
    public async Task<IActionResult> RagSearch(int id, [FromQuery] string? query = null, [FromQuery] int topK = 5)
    {
        var ownershipResult = await ControllerExtensions.RequireBookOwnershipAndLoadAsync(this, id, _repo);
        if (ownershipResult.Error is not null) return ownershipResult.Error;
        var book = ownershipResult.Book!;

        var config = await _repo.GetLlmConfigAsync(id);
        var embeddingModelConfigured = config is not null && !string.IsNullOrWhiteSpace(config.EmbeddingModelName);
        var indexedChunkCount = await _vectorStore.CountChunksAsync(id);

        if (string.IsNullOrWhiteSpace(query) || config is null || !embeddingModelConfigured)
        {
            return Ok(new
            {
                embeddingModelConfigured,
                embeddingModel = config?.EmbeddingModelName,
                provider = config?.Provider.ToString(),
                indexedChunkCount,
                query = (string?)null,
                results = Array.Empty<object>(),
                error = (string?)(embeddingModelConfigured ? null : "Set EmbeddingModelName in LLM settings to enable RAG.")
            });
        }

        try
        {
            var embedder = _llmFactory.CreateEmbeddingGeneration(config);
            var embeddings = await embedder.GenerateAsync([query]);
            var embedding = embeddings[0].Vector;
            var ancestryIds = await _repo.GetAncestryBookIdsAsync(id);
            var chunks = (await _vectorStore.SearchAsync(id, embedding, topK, ancestryIds)).ToList();

            return Ok(new
            {
                embeddingModelConfigured,
                embeddingModel = config.EmbeddingModelName,
                provider = config.Provider.ToString(),
                indexedChunkCount,
                query,
                results = chunks.Select(c => new
                {
                    c.ChapterId,
                    c.ChapterNumber,
                    c.ChunkIndex,
                    c.Score,
                    textPreview = c.Text.Length > 300 ? c.Text[..300] + "…" : c.Text
                }),
                error = (string?)null
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Book {BookId}] Vector search failed.", id);
            return Ok(new
            {
                embeddingModelConfigured,
                embeddingModel = config.EmbeddingModelName,
                provider = config.Provider.ToString(),
                indexedChunkCount,
                query,
                results = Array.Empty<object>(),
                error = "Vector search unavailable."
            });
        }
    }

    [HttpGet("{id:int}/history")]
    public async Task<IActionResult> GetHistory(int id)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;

        return Ok(await _repo.GetBookSnapshotsAsync(id));
    }

    [HttpGet("{id:int}/history/{snapshotId:int}")]
    public async Task<IActionResult> GetHistorySnapshot(int id, int snapshotId)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, id, _repo);
        if (ownershipError is not null) return ownershipError;
        var snapshot = await _repo.GetBookSnapshotAsync(id, snapshotId);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }
}

public record CreateBookRequest(
    string Title,
    string Premise,
    string Genre,
    int? TargetChapterCount = null,
    string? Language = "English",
    BookMode? Mode = null,
    int? BaseBookId = null);

public record UpdateBookRequest(
    string Title,
    string Premise,
    string Genre,
    int TargetChapterCount,
    BookStatus Status,
    string? Language = null,
    string? StoryBibleSystemPrompt = null,
    string? CharactersSystemPrompt = null,
    string? PlotThreadsSystemPrompt = null,
    string? ChapterOutlinesSystemPrompt = null,
    string? WriterSystemPrompt = null,
    string? EditorSystemPrompt = null,
    string? ContinuityCheckerSystemPrompt = null,
    BookMode? Mode = null,
    string? IllustratorSystemPrompt = null,
    bool? IllustrationsEnabled = null,
    bool? ShowSectionsInOutput = null,
    bool? ChatPartRequestsEnabled = null);

