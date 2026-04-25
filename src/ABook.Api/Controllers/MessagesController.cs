using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/messages")]
public class MessagesController : ControllerBase
{
    private readonly IBookRepository _repo;

    public MessagesController(IBookRepository repo) => _repo = repo;

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
        await orchestrator.ResumeWithAnswerAsync(req.MessageId, req.Answer);
        return Ok();
    }
}

public record PostAnswerRequest(int MessageId, string Answer);
