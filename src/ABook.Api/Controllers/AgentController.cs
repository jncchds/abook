using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/agent")]
[Authorize]
public class AgentController : ControllerBase
{
    private readonly AgentRunnerService _runner;
    private readonly AgentRunStateService _runState;
    private readonly IBookRepository _repo;

    public AgentController(AgentRunnerService runner, AgentRunStateService runState, IBookRepository repo)
    {
        _runner = runner;
        _runState = runState;
        _repo = repo;
    }

    /// <summary>
    /// Checks ownership (and, for chapter runs, that the chapter exists and is not archived — archived
    /// chapters are display-only), then claims the book and starts the run in the background.
    /// </summary>
    private async Task<IActionResult> StartAsync(
        int bookId, AgentRole role, int? chapterId, Func<IAgentOrchestrator, CancellationToken, Task> action)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;

        if (chapterId is int id)
        {
            var chapter = await _repo.GetChapterAsync(bookId, id);
            if (chapter is null) return NotFound(new { message = $"Chapter {id} not found." });
            if (chapter.IsArchived)
                return BadRequest(new { message = "This chapter is archived. Restore it before running an agent on it." });
        }

        var result = _runner.TryStart(bookId, role, chapterId, action);
        return result == RunStartResult.Success ? Accepted() : Conflict(new { message = result.ToMessage() });
    }

    /// <summary>The planning pipeline and the workflow belong to Autonomous books; Interactive books grow in the chat.</summary>
    private async Task<IActionResult> StartAutonomousAsync(
        int bookId, AgentRole role, Func<IAgentOrchestrator, CancellationToken, Task> action)
    {
        if (await _repo.GetByIdAsync(bookId) is { Mode: BookMode.Interactive })
            return Conflict(new { message = "This is an Interactive book: it is planned and written in the book chat. Switch its mode in Book Settings to use the automatic pipeline." });
        return await StartAsync(bookId, role, null, action);
    }

    [HttpPost("plan")]
    public Task<IActionResult> Plan(int bookId) =>
        StartAutonomousAsync(bookId, AgentRole.Planner, (o, c) => o.StartPlanningAsync(bookId, c));

    [HttpPost("plan/continue")]
    public Task<IActionResult> ContinuePlanning(int bookId) =>
        StartAutonomousAsync(bookId, AgentRole.Planner, (o, c) => o.ContinuePlanningAsync(bookId, c));

    [HttpPost("write/{chapterId:int}")]
    public Task<IActionResult> Write(int bookId, int chapterId) =>
        StartAsync(bookId, AgentRole.Writer, chapterId, (o, c) => o.StartWritingAsync(bookId, chapterId, c));

    [HttpPost("edit/{chapterId:int}")]
    public Task<IActionResult> Edit(int bookId, int chapterId) =>
        StartAsync(bookId, AgentRole.Editor, chapterId, (o, c) => o.StartEditingAsync(bookId, chapterId, c));

    [HttpPost("continuity")]
    public Task<IActionResult> Continuity(int bookId) =>
        StartAsync(bookId, AgentRole.ContinuityChecker, null, (o, c) => o.StartContinuityCheckAsync(bookId, null, c));

    [HttpPost("workflow/start")]
    public Task<IActionResult> StartWorkflow(int bookId) =>
        StartAutonomousAsync(bookId, AgentRole.Planner, (o, c) => o.StartWorkflowAsync(bookId, c));

    [HttpPost("workflow/continue")]
    public Task<IActionResult> ContinueWorkflow(int bookId) =>
        StartAutonomousAsync(bookId, AgentRole.Writer, (o, c) => o.ContinueWorkflowAsync(bookId, c));

    [HttpPost("workflow/stop")]
    public async Task<IActionResult> StopWorkflow(int bookId)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        _runState.CancelRun(bookId);
        return Ok();
    }

    /// <summary>
    /// The book's run status, plus what a page loaded mid-run needs to catch up: the latest progress
    /// message and the content streamed so far by the call in progress. 204 when the book never ran.
    /// </summary>
    [HttpGet("status")]
    public async Task<IActionResult> Status(int bookId)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        var status = _runState.GetStatus(bookId);
        if (status is null) return NoContent();
        if (!status.IsActive) return Ok(new { status.Role, status.State, status.ChapterId });
        return Ok(new
        {
            status.Role,
            status.State,
            status.ChapterId,
            Step = _runState.GetLastProgress(bookId),
            Stream = _runState.GetLiveStream(bookId),
        });
    }
}
