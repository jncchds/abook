using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/agent")]
public class AgentController : ControllerBase
{
    private readonly AgentRunnerService _runner;
    private readonly AgentRunStateService _runState;

    public AgentController(AgentRunnerService runner, AgentRunStateService runState)
    {
        _runner = runner;
        _runState = runState;
    }

    private IActionResult? EnsureCanStart(int bookId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        if (_runState.IsAtCapacity())
            return Conflict(new { message = "Server is at maximum concurrent agent capacity. Please try again when a run completes." });

        return null;
    }

    [HttpPost("plan")]
    public IActionResult Plan(int bookId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.StartPlanningAsync(bookId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("plan/continue")]
    public IActionResult ContinuePlanning(int bookId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.ContinuePlanningAsync(bookId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("write/{chapterId:int}")]
    public IActionResult Write(int bookId, int chapterId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.StartWritingAsync(bookId, chapterId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("edit/{chapterId:int}")]
    public IActionResult Edit(int bookId, int chapterId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.StartEditingAsync(bookId, chapterId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("continuity")]
    public IActionResult Continuity(int bookId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.StartContinuityCheckAsync(bookId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("workflow/start")]
    public IActionResult StartWorkflow(int bookId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.StartWorkflowAsync(bookId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("workflow/continue")]
    public IActionResult ContinueWorkflow(int bookId)
    {
        var blocked = EnsureCanStart(bookId);
        if (blocked is not null) return blocked;
        _ = _runner.RunAsync(bookId, (o, c) => o.ContinueWorkflowAsync(bookId, c), _runState.CreateRunCts(bookId));
        return Accepted();
    }

    [HttpPost("workflow/stop")]
    public IActionResult StopWorkflow(int bookId)
    {
        _runState.CancelRun(bookId);
        return Ok();
    }

    [HttpGet("status")]
    public IActionResult Status(int bookId)
    {
        var status = _runState.GetStatus(bookId);
        return status is null ? NoContent() : Ok(status);
    }

    /// <summary>
    /// Returns the accumulated stream buffer for a specific agent role / chapter.
    /// Used by the UI on hard-refresh to restore in-progress generation.
    /// Returns { content: "" } when there is no active buffer.
    /// </summary>
    [HttpGet("stream-buffer")]
    public IActionResult StreamBuffer(int bookId, [FromQuery] string? agentRole, [FromQuery] int? chapterId)
    {
        var content = _runState.GetStreamBufferContent(bookId, chapterId, agentRole) ?? string.Empty;
        return Ok(new { content });
    }

}

