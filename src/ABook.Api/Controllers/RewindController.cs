using ABook.Api.Services;
using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// Rewinding a whole book (and its chat) to an earlier moment: the moments to choose from, the preview, and the
/// rewind itself — which writes the state of that moment as a new state and hides later chat messages.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/rewind")]
[Authorize]
public class RewindController(IBookRepository repo, IBookRewinder rewinder, RewindService rewinds) : ControllerBase
{
    /// <summary>Recent moments, newest first; <paramref name="before"/> pages back.</summary>
    [HttpGet("points")]
    public async Task<IActionResult> Points(int bookId, [FromQuery] DateTime? before = null, [FromQuery] int limit = 30)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        return Ok(new
        {
            Earliest = await rewinder.EarliestAsync(bookId),
            Points = await rewinder.PointsAsync(bookId, before?.ToUniversalTime(), Math.Clamp(limit, 1, 100)),
        });
    }

    [HttpGet("preview")]
    public async Task<IActionResult> Preview(int bookId, [FromQuery] DateTime at)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        try { return Ok(await rewinder.PreviewAsync(bookId, at.ToUniversalTime())); }
        catch (RewindTooEarlyException ex) { return BadRequest(new { message = ex.Message, ex.Earliest }); }
    }

    [HttpPost]
    public async Task<IActionResult> Rewind(int bookId, [FromBody] RewindRequest req)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, repo);
        if (denied is not null) return denied;
        var outcome = await rewinds.StartAsync(bookId, req.At.ToUniversalTime(), req.Fingerprint, origin: null);
        return outcome.Result switch
        {
            RewindStartResult.Started => Accepted(new { outcome.Message }),
            RewindStartResult.NotFound => NotFound(new { outcome.Message }),
            RewindStartResult.TooEarly or RewindStartResult.NothingToDo => BadRequest(new { outcome.Message }),
            _ => Conflict(new { outcome.Message, Result = outcome.Result.ToString() }),
        };
    }
}

public record RewindRequest(DateTime At, string? Fingerprint);
