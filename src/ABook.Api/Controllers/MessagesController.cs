using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/messages")]
[Authorize]
public class MessagesController : ControllerBase
{
    private readonly IBookRepository _repo;

    public MessagesController(IBookRepository repo) => _repo = repo;

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : (int?)null;

    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId, [FromQuery] int? chapterId) =>
        Ok(await _repo.GetMessagesAsync(bookId, chapterId));

    [HttpDelete]
    public async Task<IActionResult> DeleteAll(int bookId)
    {
        await _repo.DeleteMessagesAsync(bookId);
        return NoContent();
    }

    [HttpPost("answer")]
    public async Task<IActionResult> PostAnswer(int bookId,
        [FromBody] PostAnswerRequest req,
        [FromServices] IAgentOrchestrator orchestrator)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        var msg = await _repo.FindMessageByIdAsync(req.MessageId);
        if (msg is null || msg.BookId != bookId) return NotFound();

        await orchestrator.ResumeWithAnswerAsync(req.MessageId, req.Answer);
        return Ok();
    }
}

public record PostAnswerRequest(int MessageId, string Answer);
