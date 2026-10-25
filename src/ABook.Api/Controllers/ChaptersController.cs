using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/chapters")]
[Authorize]
public class ChaptersController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly IVectorStoreService _vectorStore;
    private readonly ChapterIndexingService _indexing;
    private readonly ABook.Api.Telegram.TelegramEventSink _telegram;

    public ChaptersController(IBookRepository repo, IVectorStoreService vectorStore, ChapterIndexingService indexing,
        ABook.Api.Telegram.TelegramEventSink telegram)
    {
        _repo = repo;
        _vectorStore = vectorStore;
        _indexing = indexing;
        _telegram = telegram;
    }

    /// <summary>
    /// A manual edit: the Telegram bot sends the chapter's file once the edits settle. Only the bot is told — the
    /// web UI already has the saved chapter, and its ChapterUpdated handler would clear a live stream.
    /// </summary>
    private void TellTelegram(int bookId, int chapterId) =>
        _telegram.Post(new ABook.Api.Telegram.TgChapterEvent(bookId, chapterId));

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
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;

        var chapter = new Chapter
        {
            BookId = bookId,
            SectionId = req.SectionId,
            Title = req.Title,
            Outline = req.Outline
        };
        Chapter created;
        try
        {
            if (req.SectionId is int sid && await _repo.GetSectionAsync(bookId, sid) is null)
                return BadRequest(new { message = $"Section {sid} not found." });
            if (req.AtStart || req.AfterChapterId is not null)
            {
                if (req.AfterChapterId is int after && await _repo.GetChapterAsync(bookId, after) is null)
                    return BadRequest(new { message = $"Chapter {after} not found." });
                created = await _repo.InsertChapterAsync(chapter, req.AtStart ? null : req.AfterChapterId, req.SameSection);
            }
            else created = await _repo.AddChapterAsync(chapter);   // at the end (of its section, or in a new one)
        }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
        return CreatedAtAction(nameof(GetById), new { bookId, chapterId = created.Id }, created);
    }

    /// <summary>Moves a chapter into a section, after one of its chapters (or first); an emptied section is removed.</summary>
    [HttpPost("{chapterId:int}/move-to-section")]
    public async Task<IActionResult> MoveToSection(int bookId, int chapterId, [FromBody] MoveToSectionRequest req,
        [FromServices] ABook.Agents.BookContentService content)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (await _repo.GetChapterAsync(bookId, chapterId) is null) return NotFound();
        try
        {
            content.EnsureNoActiveRun(bookId);
            var change = await content.MoveChapterToSectionAsync(bookId, chapterId, req.SectionId, req.AfterChapterId);
            return Ok(new
            {
                note = change.Note,
                sections = await _repo.GetSectionsAsync(bookId, includeArchived: true),
                chapters = await _repo.GetChaptersAsync(bookId, includeArchived: true),
            });
        }
        catch (ABook.Agents.BookContentService.BookBusyException ex) { return Conflict(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>Moves a chapter (archived ones too) to right after another one, or to the start; the book is renumbered.</summary>
    [HttpPost("{chapterId:int}/move")]
    public async Task<IActionResult> Move(int bookId, int chapterId, [FromBody] MoveChapterRequest req,
        [FromServices] ABook.Agents.BookContentService content)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (await _repo.GetChapterAsync(bookId, chapterId) is null) return NotFound();
        try
        {
            content.EnsureNoActiveRun(bookId);
            var change = await content.MoveChapterAsync(bookId, chapterId, req.AtStart ? null : req.AfterChapterId);
            return Ok(new
            {
                note = change.Note,
                sections = await _repo.GetSectionsAsync(bookId, includeArchived: true),
                chapters = await _repo.GetChaptersAsync(bookId, includeArchived: true),
            });
        }
        catch (ABook.Agents.BookContentService.BookBusyException ex) { return Conflict(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    /// <summary>
    /// Splits a chapter in two by its outline: it keeps its title with <c>firstOutline</c>, a new chapter right after
    /// it gets <c>secondTitle</c>/<c>secondOutline</c>; both go back to Outlined to be written again.
    /// </summary>
    [HttpPost("{chapterId:int}/split")]
    public async Task<IActionResult> Split(int bookId, int chapterId, [FromBody] SplitChapterRequest req,
        [FromServices] ABook.Agents.BookContentService content)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (await _repo.GetChapterAsync(bookId, chapterId) is null) return NotFound();
        try
        {
            content.EnsureNoActiveRun(bookId);
            var (first, second) = await content.SplitChapterAsync(bookId, chapterId, req.FirstOutline, req.SecondTitle, req.SecondOutline);
            TellTelegram(bookId, first.Id);
            return Ok(new { first, second });
        }
        catch (ABook.Agents.BookContentService.BookBusyException ex) { return Conflict(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }

    [HttpPut("{chapterId:int}")]
    public async Task<IActionResult> Update(int bookId, int chapterId, [FromBody] UpdateChapterRequest req)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;

        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();

        var contentCleared = string.IsNullOrEmpty(req.Content) && !string.IsNullOrEmpty(chapter.Content);
        var contentChanged = req.Content != chapter.Content && !string.IsNullOrEmpty(req.Content);
        var anyChange = req.Content != chapter.Content || req.Title != chapter.Title
                        || req.Outline != chapter.Outline || req.Status != chapter.Status;

        // Every edit is a version (an edit on top of the author's own last version amends it — see EditSources).
        if (anyChange)
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
            var saved = await _repo.AddChapterVersionAsync(version);
            // The active version answers searches: new prose is embedded in the background (unchanged prose keeps
            // its embeddings), cleared prose leaves RAG.
            if (contentCleared)
                try { await _vectorStore.DeleteChapterChunksAsync(bookId, chapterId); } catch { /* non-fatal */ }
            else if (contentChanged || (!saved.HasEmbeddings && !string.IsNullOrEmpty(saved.Content)))
                _indexing.QueueIndex(bookId, chapterId, saved.Id);
            TellTelegram(bookId, chapterId);
        }
        // AddChapterVersionAsync already syncs fields to Chapter, so just return it
        return Ok(await _repo.GetChapterAsync(bookId, chapterId));
    }

    [HttpPost("{chapterId:int}/archive")]
    public async Task<IActionResult> Archive(int bookId, int chapterId)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;

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
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;

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
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;

        var chapter = await _repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return NotFound();

        ChapterVersion version;
        try { version = await _repo.ActivateChapterVersionAsync(bookId, chapterId, versionId); }
        catch (InvalidOperationException) { return NotFound(); }

        // A version that was never embedded (an author edit, or one saved before indexing existed)
        // has no chunks of its own — index it now that it answers searches.
        if (!version.HasEmbeddings && !string.IsNullOrEmpty(version.Content))
            _indexing.QueueIndex(bookId, chapterId, version.Id);

        var updatedChapter = await _repo.GetChapterAsync(bookId, chapterId);
        TellTelegram(bookId, chapterId);
        return Ok(new { chapter = updatedChapter, version = new { version.Id, version.VersionNumber, version.IsActive, version.HasEmbeddings, version.CreatedBy, version.CreatedAt } });
    }
}

/// <param name="Number">Ignored (kept for old clients): the number follows from the place.</param>
/// <param name="AfterChapterId">Insert right after this chapter; with neither this nor <paramref name="AtStart"/>, the chapter goes at the end.</param>
/// <param name="SameSection">Join the anchor's section instead of starting a new section after it.</param>
/// <param name="SectionId">Put it in this section (at its end, or after <paramref name="AfterChapterId"/> of it).</param>
public record CreateChapterRequest(int Number, string Title, string Outline, int? AfterChapterId = null, bool AtStart = false,
    bool SameSection = false, int? SectionId = null);
public record MoveToSectionRequest(int SectionId, int? AfterChapterId);
public record MoveChapterRequest(int? AfterChapterId, bool AtStart = false);
public record SplitChapterRequest(string FirstOutline, string SecondTitle, string SecondOutline);
public record UpdateChapterRequest(string Title, string Outline, string Content, ChapterStatus Status);
