using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books")]
public class BooksController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly IVectorStoreService _vectorStore;
    private readonly ILlmProviderFactory _llmFactory;

    public BooksController(IBookRepository repo, IVectorStoreService vectorStore, ILlmProviderFactory llmFactory)
    {
        _repo = repo;
        _vectorStore = vectorStore;
        _llmFactory = llmFactory;
    }

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value)
            : (int?)null;

    [HttpGet]
    public async Task<IActionResult> GetAll() =>
        Ok(await _repo.GetAllAsync(CurrentUserId));

    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetById(int id)
    {
        var book = await _repo.GetByIdWithDetailsAsync(id);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();
        return Ok(book);
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateBookRequest req)
    {
        var book = new Book
        {
            Title = req.Title,
            Premise = req.Premise,
            Genre = req.Genre,
            TargetChapterCount = req.TargetChapterCount,
            Language = req.Language ?? "English",
            UserId = CurrentUserId
        };
        var created = await _repo.AddAsync(book);
        return CreatedAtAction(nameof(GetById), new { id = created.Id }, created);
    }

    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int id, [FromBody] UpdateBookRequest req)
    {
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        book.Title = req.Title;
        book.Premise = req.Premise;
        book.Genre = req.Genre;
        book.TargetChapterCount = req.TargetChapterCount;
        book.Status = req.Status;
        book.Language = req.Language ?? book.Language;
        book.StoryBibleSystemPrompt = req.StoryBibleSystemPrompt;
        book.CharactersSystemPrompt = req.CharactersSystemPrompt;
        book.PlotThreadsSystemPrompt = req.PlotThreadsSystemPrompt;
        book.ChapterOutlinesSystemPrompt = req.ChapterOutlinesSystemPrompt;
        book.WriterSystemPrompt = req.WriterSystemPrompt;
        book.EditorSystemPrompt = req.EditorSystemPrompt;
        book.ContinuityCheckerSystemPrompt = req.ContinuityCheckerSystemPrompt;
        await _repo.UpdateAsync(book);
        return Ok(book);
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int id)
    {
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();
        await _repo.DeleteAsync(id);
        try { await _vectorStore.DeleteCollectionAsync(id); } catch { /* non-fatal */ }
        return NoContent();
    }

    [HttpGet("{id:int}/token-usage")]
    public async Task<IActionResult> GetTokenUsage(int id)
    {
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();
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
            r.CreatedAt
        }));
    }

    [HttpDelete("{id:int}/token-usage")]
    public async Task<IActionResult> DeleteTokenUsage(int id)
    {
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();
        await _repo.DeleteTokenUsageAsync(id);
        return NoContent();
    }

    [HttpGet("{id:int}/default-prompts")]
    public async Task<IActionResult> GetDefaultPrompts(int id)    {
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();

        var lang = string.IsNullOrWhiteSpace(book.Language) ? "English" : book.Language;

        return Ok(new
        {
            storyBibleSystemPrompt = $"You are a world-building expert creating a Story Bible for a book project.\nOutput a JSON object with fields: \"settingDescription\", \"timePeriod\", \"themes\", \"toneAndStyle\", \"worldRules\", \"notes\".\nOutput ONLY the JSON object, no additional text.\nWrite all content in {lang}.",

            charactersSystemPrompt = $"You are a character development expert creating character profiles.\nOutput a JSON array where each element has: \"name\", \"role\" (Protagonist|Antagonist|Supporting|Minor),\n\"physicalDescription\", \"personality\", \"backstory\", \"goalMotivation\", \"arc\",\n\"firstAppearanceChapterNumber\" (int or null), \"notes\".\nOutput ONLY the JSON array, no additional text.\nWrite all content in {lang}.",

            plotThreadsSystemPrompt = $"You are a story structure expert mapping all plot threads for a book.\nOutput a JSON array where each element has: \"name\", \"description\",\n\"type\" (MainPlot|Subplot|CharacterArc|Mystery|Foreshadowing|WorldBuilding|ThematicThread),\n\"introducedChapterNumber\" (int or null), \"resolvedChapterNumber\" (int or null),\n\"status\" (Active|Resolved|Dormant).\nOutput ONLY the JSON array, no additional text.\nWrite all content in {lang}.",

            chapterOutlinesSystemPrompt = $"You are a creative writing Planner outlining each chapter of a book.\nOutput a JSON array where each element has:\n  \"number\" (int), \"title\" (string),\n  \"outline\" (string - 3-6 sentence synopsis including key events and decisions),\n  \"povCharacter\" (string), \"charactersInvolved\" (array of strings),\n  \"plotThreads\" (array of strings),\n  \"foreshadowingNotes\" (string), \"payoffNotes\" (string).\nOutput ONLY the JSON array, no additional text.\nWrite all content in {lang}.",

            writerSystemPrompt = $"You are a creative fiction Writer. Write compelling, immersive prose in markdown.\nBook title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nWrite all content in {lang}.",

            editorSystemPrompt = $"You are a professional fiction Editor. Your job is to improve prose quality, fix grammar,\nenhance pacing, and strengthen character voice. Preserve the author's style.\nOutput the complete improved chapter in markdown, followed by a brief\n\"## Editorial Notes\" section listing key changes made.\nBook: {book.Title} | Genre: {book.Genre} | Language: {lang}",

            continuityCheckerSystemPrompt = $"You are a Continuity Checker for fiction manuscripts. Your job is to identify plot holes,\ncharacter inconsistencies, timeline errors, and factual contradictions across chapters.\nOutput a JSON array of issues, each with:\n  \"description\" (string), \"chapterNumbers\" (int[]), \"suggestion\" (string).\nIf no issues found, output an empty array [].\nBook: {book.Title} | Genre: {book.Genre} | Language: {lang}"
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
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        var config = await _repo.GetLlmConfigAsync(id, book.UserId);
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
            var chunks = (await _vectorStore.SearchAsync(id, embedding, topK)).ToList();

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
            return Ok(new
            {
                embeddingModelConfigured,
                embeddingModel = config.EmbeddingModelName,
                provider = config.Provider.ToString(),
                indexedChunkCount,
                query,
                results = Array.Empty<object>(),
                error = ex.Message
            });
        }
    }
}

public record CreateBookRequest(
    string Title,
    string Premise,
    string Genre,
    int TargetChapterCount,
    string? Language = "English");

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
    string? ContinuityCheckerSystemPrompt = null);

