using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// The book's clarifications: answered planning questions and author notes. Active ones are appended to
/// the premise in every prompt; archived ones are kept for reference only.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/clarifications")]
[Authorize]
public class ClarificationsController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly ClarificationService _clarifications;

    public ClarificationsController(IBookRepository repo, ClarificationService clarifications)
    {
        _repo = repo;
        _clarifications = clarifications;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId, [FromQuery] bool includeArchived = false)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        return Ok(await _repo.GetClarificationsAsync(bookId, includeArchived));
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] ClarificationRequest req)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (Invalid(req) is { } bad) return bad;

        var created = await _repo.AddClarificationAsync(new Clarification
        {
            BookId = bookId, Question = req.Question.Trim(), Answer = req.Answer.Trim(),
        });
        return Ok(created);
    }

    [HttpPut("{clarificationId:int}")]
    public async Task<IActionResult> Update(int bookId, int clarificationId, [FromBody] ClarificationRequest req)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;
        if (Invalid(req) is { } bad) return bad;

        var clarification = await _repo.GetClarificationAsync(bookId, clarificationId);
        if (clarification is null) return NotFound();
        // Answering the last open question closes the round and resumes a waiting planning run.
        return Ok(await _clarifications.UpdateAsync(clarification, req.Question, req.Answer, archived: null));
    }

    [HttpPost("{clarificationId:int}/archive")]
    public Task<IActionResult> Archive(int bookId, int clarificationId) => SetArchivedAsync(bookId, clarificationId, true);

    [HttpPost("{clarificationId:int}/unarchive")]
    public Task<IActionResult> Unarchive(int bookId, int clarificationId) => SetArchivedAsync(bookId, clarificationId, false);

    private async Task<IActionResult> SetArchivedAsync(int bookId, int clarificationId, bool archived)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo);
        if (ownershipError is not null) return ownershipError;

        var clarification = await _repo.GetClarificationAsync(bookId, clarificationId);
        if (clarification is null) return NotFound();
        // Archiving an open question skips it, which can close the round too.
        return Ok(await _clarifications.UpdateAsync(clarification, question: null, answer: null, archived));
    }

    private BadRequestObjectResult? Invalid(ClarificationRequest req) =>
        string.IsNullOrWhiteSpace(req.Question) || string.IsNullOrWhiteSpace(req.Answer)
            ? BadRequest(new { message = "Both the question and the answer are required." })
            : null;
}

public record ClarificationRequest(string Question, string Answer);
