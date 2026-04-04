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

    public BooksController(IBookRepository repo, IVectorStoreService vectorStore)
    {
        _repo = repo;
        _vectorStore = vectorStore;
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
        book.PlannerSystemPrompt = req.PlannerSystemPrompt;
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

    [HttpGet("{id:int}/default-prompts")]
    public async Task<IActionResult> GetDefaultPrompts(int id)
    {
        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();

        var lang = string.IsNullOrWhiteSpace(book.Language) ? "English" : book.Language;

        return Ok(new
        {
            plannerSystemPrompt = $"You are a creative writing Planner. Your task is to outline a book in detail.\nFor each chapter, output a JSON array of objects with fields:\n  \"number\" (int), \"title\" (string), \"outline\" (string, 2-4 sentences synopsis).\nOutput ONLY the JSON array, no additional text.\nWrite all content in {lang}.",

            writerSystemPrompt = $"You are a creative fiction Writer. Write compelling, immersive prose in markdown.\nBook title: {book.Title}\nGenre: {book.Genre}\nPremise: {book.Premise}\nWrite all content in {lang}.",

            editorSystemPrompt = $"You are a professional fiction Editor. Your job is to improve prose quality, fix grammar,\nenhance pacing, and strengthen character voice. Preserve the author's style.\nOutput the complete improved chapter in markdown, followed by a brief\n\"## Editorial Notes\" section listing key changes made.\nBook: {book.Title} | Genre: {book.Genre} | Language: {lang}",

            continuityCheckerSystemPrompt = $"You are a Continuity Checker for fiction manuscripts. Your job is to identify plot holes,\ncharacter inconsistencies, timeline errors, and factual contradictions across chapters.\nOutput a JSON array of issues, each with:\n  \"description\" (string), \"chapterNumbers\" (int[]), \"suggestion\" (string).\nIf no issues found, output an empty array [].\nBook: {book.Title} | Genre: {book.Genre} | Language: {lang}"
        });
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
    string? PlannerSystemPrompt = null,
    string? WriterSystemPrompt = null,
    string? EditorSystemPrompt = null,
    string? ContinuityCheckerSystemPrompt = null);

