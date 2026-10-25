using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// Manages the planning phase completion status for Clarifications, Story Bible, Characters, Plot Threads, and Chapters
/// (the sections and their chapters). Supports three actions per phase:
///   POST /{phase}/complete  — mark phase as Complete (data kept)
///   POST /{phase}/reopen    — reset phase to NotStarted (data kept)
///   DELETE /{phase}         — delete phase data AND reset to NotStarted
/// Valid phase values: clarifications | storybible | characters | plotthreads | chapters.
/// The logic lives in <see cref="PlanningPhaseService"/>, shared with MCP.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/planning-phases")]
[Authorize]
public class PlanningPhasesController(IBookRepository repo, PlanningPhaseService phases) : ControllerBase
{
    [HttpPost("{phase}/complete")]
    public Task<IActionResult> Complete(int bookId, string phase) =>
        SetPhaseStatusAsync(bookId, phase, PlanningPhaseStatus.Complete);

    [HttpPost("{phase}/reopen")]
    public Task<IActionResult> Reopen(int bookId, string phase) =>
        SetPhaseStatusAsync(bookId, phase, PlanningPhaseStatus.NotStarted);

    private async Task<IActionResult> SetPhaseStatusAsync(int bookId, string phase, PlanningPhaseStatus status)
    {
        var (error, book) = await this.RequireBookOwnershipAndLoadAsync(bookId, repo);
        if (error is not null) return error;
        return await phases.SetStatusAsync(book!, phase, status)
            ? Ok()
            : BadRequest(new { message = $"Unknown planning phase: {phase}" });
    }

    [HttpDelete("{phase}")]
    public async Task<IActionResult> Clear(int bookId, string phase)
    {
        var (error, book) = await this.RequireBookOwnershipAndLoadAsync(bookId, repo);
        if (error is not null) return error;
        try
        {
            await phases.ClearAsync(book!, phase);
            return Ok();
        }
        catch (StoryBibleLockedException ex) { return Conflict(new { message = ex.Message }); }
        catch (ArgumentException ex) { return BadRequest(new { message = ex.Message }); }
    }
}
