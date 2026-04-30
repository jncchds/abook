using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/chapters")]
public class ChaptersController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly IVectorStoreService _vectorStore;

    public ChaptersController(IBookRepository repo, IVectorStoreService vectorStore)
    {
        _repo = repo;
        _vectorStore = vectorStore;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId, [FromQuery] bool includeArchived = false) =>
        Ok(await _repo.GetChaptersAsync(bookId, includeArchived));

    [HttpGet("{chapterId:int}")]
    public async Task<IActionResult> GetById(int bookId, int chapterId)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        return chapter is null ? NotFound() : Ok(chapter);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] CreateChapterRequest req)
    {
        var chapter = new Chapter
        {
            BookId = bookId,
            Number = req.Number,
            Title = req.Title,
            Outline = req.Outline
        };
        var created = await _repo.AddChapterAsync(chapter);
        return CreatedAtAction(nameof(GetById), new { bookId, chapterId = created.Id }, created);
    }

    [HttpPut("{chapterId:int}")]
    public async Task<IActionResult> Update(int bookId, int chapterId, [FromBody] UpdateChapterRequest req)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();

        var contentCleared = string.IsNullOrEmpty(req.Content) && !string.IsNullOrEmpty(chapter.Content);
        var contentChanged = req.Content != chapter.Content && !string.IsNullOrEmpty(req.Content);

        // If content is being updated by the user, save a version snapshot
        if (contentChanged)
        {
            var version = new ChapterVersion
            {
                ChapterId = chapterId,
                BookId = bookId,
                Title = req.Title,
                Outline = req.Outline,
                Content = req.Content,
                Status = req.Status,
                PovCharacter = chapter.PovCharacter,
                CharactersInvolvedJson = chapter.CharactersInvolvedJson,
                PlotThreadsJson = chapter.PlotThreadsJson,
                ForeshadowingNotes = chapter.ForeshadowingNotes,
                PayoffNotes = chapter.PayoffNotes,
                CreatedBy = "user",
                HasEmbeddings = false
            };
            await _repo.AddChapterVersionAsync(version);
            // AddChapterVersionAsync already syncs fields to Chapter, so just return it
            var updated = await _repo.GetChapterAsync(bookId, chapterId);
            return Ok(updated);
        }

        chapter.Title = req.Title;
        chapter.Outline = req.Outline;
        chapter.Content = req.Content;
        chapter.Status = req.Status;
        await _repo.UpdateChapterAsync(chapter);

        if (contentCleared)
            try { await _vectorStore.DeleteChapterChunksAsync(bookId, chapterId); } catch { /* non-fatal */ }

        return Ok(chapter);
    }

    [HttpPost("{chapterId:int}/archive")]
    public async Task<IActionResult> Archive(int bookId, int chapterId)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();
        await _repo.ArchiveChapterAsync(bookId, chapterId);
        // Remove the active version's embeddings from RAG (non-fatal)
        try { await _vectorStore.DeleteChapterChunksAsync(bookId, chapterId); } catch { /* non-fatal */ }
        return NoContent();
    }

    [HttpPost("{chapterId:int}/restore")]
    public async Task<IActionResult> Restore(int bookId, int chapterId)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();
        await _repo.RestoreChapterAsync(bookId, chapterId);
        return Ok(await _repo.GetChapterAsync(bookId, chapterId));
    }

    // ── Version history ───────────────────────────────────────────────────────

    [HttpGet("{chapterId:int}/versions")]
    public async Task<IActionResult> GetVersions(int bookId, int chapterId)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();
        var versions = await _repo.GetChapterVersionsAsync(chapterId);
        return Ok(versions.Select(v => new
        {
            v.Id,
            v.VersionNumber,
            v.CreatedBy,
            v.IsActive,
            v.HasEmbeddings,
            WordCount = string.IsNullOrEmpty(v.Content) ? 0 : v.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            v.CreatedAt
        }));
    }

    [HttpGet("{chapterId:int}/versions/{versionId:int}")]
    public async Task<IActionResult> GetVersion(int bookId, int chapterId, int versionId)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();
        var version = await _repo.GetChapterVersionAsync(chapterId, versionId);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost("{chapterId:int}/versions/{versionId:int}/activate")]
    public async Task<IActionResult> ActivateVersion(int bookId, int chapterId, int versionId)
    {
        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();

        ChapterVersion version;
        try { version = await _repo.ActivateChapterVersionAsync(bookId, chapterId, versionId); }
        catch (InvalidOperationException) { return NotFound(); }

        var updatedChapter = await _repo.GetChapterAsync(bookId, chapterId);
        return Ok(new { chapter = updatedChapter, version = new { version.Id, version.VersionNumber, version.IsActive, version.HasEmbeddings, version.CreatedBy, version.CreatedAt } });
    }
}

public record CreateChapterRequest(int Number, string Title, string Outline);
public record UpdateChapterRequest(string Title, string Outline, string Content, ChapterStatus Status);
