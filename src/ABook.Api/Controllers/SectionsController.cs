using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// The book's sections: parts of the plan spanning one or more chapters. Structure changes (move, archive, restore)
/// renumber the book and are refused while an agent run works on it.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/sections")]
[Authorize]
public class SectionsController(IBookRepository repo, BookContentService content) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId, [FromQuery] bool includeArchived = false) =>
        Ok(await repo.GetSectionsAsync(bookId, includeArchived));

    /// <summary>
    /// Sets the section's plan. A section with a single chapter has that chapter's plan: the chapter's outline is saved
    /// (as a chapter version) and the section follows it.
    /// </summary>
    [HttpPut("{sectionId:int}")]
    public Task<IActionResult> Update(int bookId, int sectionId, [FromBody] UpdateSectionRequest req) =>
        Run(bookId, sectionId, busyCheck: false, () => content.UpdateSectionPlanAsync(bookId, sectionId, new Dictionary<string, string>
        {
            ["title"] = req.Title ?? "",
            ["outline"] = req.Outline ?? "",
            ["povCharacter"] = req.PovCharacter ?? "",
            ["charactersInvolved"] = req.CharactersInvolved ?? "",
            ["plotThreads"] = req.PlotThreads ?? "",
            ["foreshadowingNotes"] = req.ForeshadowingNotes ?? "",
            ["payoffNotes"] = req.PayoffNotes ?? "",
        }, EditSources.User));

    [HttpPost("{sectionId:int}/move")]
    public Task<IActionResult> Move(int bookId, int sectionId, [FromBody] MoveSectionRequest req) =>
        Run(bookId, sectionId, busyCheck: true,
            () => content.MoveSectionAsync(bookId, sectionId, req.AtStart ? null : req.AfterSectionId));

    [HttpPost("{sectionId:int}/archive")]
    public Task<IActionResult> Archive(int bookId, int sectionId) =>
        Run(bookId, sectionId, busyCheck: true, () => content.ArchiveSectionAsync(bookId, sectionId));

    [HttpPost("{sectionId:int}/restore")]
    public Task<IActionResult> Restore(int bookId, int sectionId) =>
        Run(bookId, sectionId, busyCheck: true, () => content.RestoreSectionAsync(bookId, sectionId));

    private async Task<IActionResult> Run(int bookId, int sectionId, bool busyCheck, Func<Task<ContentChange>> action)
    {
        var ownershipError = await ControllerExtensions.RequireBookOwnershipAsync(this, bookId, repo);
        if (ownershipError is not null) return ownershipError;
        if (await repo.GetSectionAsync(bookId, sectionId) is null) return NotFound();
        try
        {
            if (busyCheck) content.EnsureNoActiveRun(bookId);
            var change = await action();
            return Ok(new
            {
                note = change.Note,
                sections = await repo.GetSectionsAsync(bookId, includeArchived: true),
                chapters = await repo.GetChaptersAsync(bookId, includeArchived: true),
            });
        }
        catch (BookContentService.BookBusyException ex) { return Conflict(new { message = ex.Message }); }
        catch (InvalidOperationException ex) { return BadRequest(new { message = ex.Message }); }
    }
}

/// <param name="CharactersInvolved">Comma-separated names.</param>
/// <param name="PlotThreads">Comma-separated plot thread names.</param>
public record UpdateSectionRequest(string? Title, string? Outline, string? PovCharacter, string? CharactersInvolved,
    string? PlotThreads, string? ForeshadowingNotes, string? PayoffNotes);
public record MoveSectionRequest(int? AfterSectionId, bool AtStart = false);
