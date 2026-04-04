using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books")]
public class BooksController : ControllerBase
{
    private readonly IBookRepository _repo;

    public BooksController(IBookRepository repo) => _repo = repo;

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
        return NoContent();
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

