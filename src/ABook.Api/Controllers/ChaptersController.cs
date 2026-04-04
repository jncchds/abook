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
    public async Task<IActionResult> GetAll(int bookId) =>
        Ok(await _repo.GetChaptersAsync(bookId));

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
        chapter.Title = req.Title;
        chapter.Outline = req.Outline;
        chapter.Content = req.Content;
        chapter.Status = req.Status;
        await _repo.UpdateChapterAsync(chapter);
        if (contentCleared)
            try { await _vectorStore.DeleteChapterChunksAsync(bookId, chapterId); } catch { /* non-fatal */ }
        return Ok(chapter);
    }
}

public record CreateChapterRequest(int Number, string Title, string Outline);
public record UpdateChapterRequest(string Title, string Outline, string Content, ChapterStatus Status);
