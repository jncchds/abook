using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/agent")]
public class AgentController : ControllerBase
{
    private readonly IAgentOrchestrator _orchestrator;

    public AgentController(IAgentOrchestrator orchestrator) => _orchestrator = orchestrator;

    [HttpPost("plan")]
    public async Task<IActionResult> Plan(int bookId)
    {
        await _orchestrator.StartPlanningAsync(bookId);
        return Accepted();
    }

    [HttpPost("write/{chapterId:int}")]
    public async Task<IActionResult> Write(int bookId, int chapterId)
    {
        await _orchestrator.StartWritingAsync(bookId, chapterId);
        return Accepted();
    }

    [HttpPost("edit/{chapterId:int}")]
    public async Task<IActionResult> Edit(int bookId, int chapterId)
    {
        await _orchestrator.StartEditingAsync(bookId, chapterId);
        return Accepted();
    }

    [HttpPost("continuity")]
    public async Task<IActionResult> Continuity(int bookId)
    {
        await _orchestrator.StartContinuityCheckAsync(bookId);
        return Accepted();
    }

    [HttpGet("status")]
    public async Task<IActionResult> Status(int bookId)
    {
        var status = await _orchestrator.GetRunStatusAsync(bookId);
        return status is null ? NoContent() : Ok(status);
    }
}
