using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

/// <summary>
/// Shared ownership-check helper used by all content controllers.
/// Replaces the duplicate CheckOwnershipAsync method previously defined on StoryBibleController, CharactersController, ChaptersController, PlotThreadsController.
/// </summary>
public static class ControllerExtensions
{
    /// <summary>
    /// Verifies the current user owns (or has unrestricted access to) a book.
    /// Returns null on success, or a ready-to-return IActionResult (NotFound / Forbid).
    /// </summary>
    public static async Task<IActionResult?> RequireBookOwnershipAsync(
        this ControllerBase controller, int bookId, IBookRepository repo)
    {
        var currentUserId = controller.User.Identity?.IsAuthenticated == true
            ? int.Parse(controller.User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : (int?)null;

        var book = await repo.GetByIdAsync(bookId);
        if (book is null) return controller.NotFound();
        if (book.UserId is not null && book.UserId != currentUserId) return controller.Forbid();
        return null;
    }
}
