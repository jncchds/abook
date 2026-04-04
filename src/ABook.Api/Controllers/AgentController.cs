using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/agent")]
public class AgentController : ControllerBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunStateService _runState;

    public AgentController(IServiceScopeFactory scopeFactory, AgentRunStateService runState)
    {
        _scopeFactory = scopeFactory;
        _runState = runState;
    }

    [HttpPost("plan")]
    public IActionResult Plan(int bookId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        _ = RunInBackground(bookId, (o, ct) => o.StartPlanningAsync(bookId, ct));
        return Accepted();
    }

    [HttpPost("write/{chapterId:int}")]
    public IActionResult Write(int bookId, int chapterId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        _ = RunInBackground(bookId, (o, ct) => o.StartWritingAsync(bookId, chapterId, ct));
        return Accepted();
    }

    [HttpPost("edit/{chapterId:int}")]
    public IActionResult Edit(int bookId, int chapterId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        _ = RunInBackground(bookId, (o, ct) => o.StartEditingAsync(bookId, chapterId, ct));
        return Accepted();
    }

    [HttpPost("continuity")]
    public IActionResult Continuity(int bookId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        _ = RunInBackground(bookId, (o, ct) => o.StartContinuityCheckAsync(bookId, ct));
        return Accepted();
    }

    [HttpPost("workflow/start")]
    public IActionResult StartWorkflow(int bookId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        var ct = _runState.CreateRunCts(bookId);
        _ = RunInBackground(bookId, (o, c) => o.StartWorkflowAsync(bookId, c), ct);
        return Accepted();
    }

    [HttpPost("workflow/continue")]
    public IActionResult ContinueWorkflow(int bookId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return Conflict(new { message = "An agent run is already in progress for this book." });

        var ct = _runState.CreateRunCts(bookId);
        _ = RunInBackground(bookId, (o, c) => o.ContinueWorkflowAsync(bookId, c), ct);
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

    private async Task RunInBackground(int bookId, Func<IAgentOrchestrator, CancellationToken, Task> action, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        try
        {
            await action(orchestrator, ct);
        }
        catch (OperationCanceledException)
        {
            // Expected when stopped by the user — state already set by orchestrator
        }
        catch (Exception ex)
        {
            // Ensure state isn't stuck on Running
            var current = _runState.GetStatus(bookId);
            if (current is { State: "Running" or "WaitingForInput" })
                _runState.SetStatus(bookId, new AgentRunStatus(current.Role, "Failed", current.ChapterId));
            _ = ex;
        }
    }
}

