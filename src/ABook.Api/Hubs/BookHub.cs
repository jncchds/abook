using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace ABook.Api.Hubs;

/// <summary>
/// Real-time channel for a book's agent activity (streamed content, questions, status). A connection
/// only joins the group of a book its user may access — the stream carries the book's full text.
/// </summary>
[Authorize]
public class BookHub(IBookRepository repo) : Hub
{
    public async Task JoinBook(string bookId)
    {
        if (!int.TryParse(bookId, out var id))
            throw new HubException("Invalid book id.");

        var book = await repo.GetByIdAsync(id);
        var userIdClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdClaim, out var uid) ? uid : null;
        // Same rule as the REST ownership check: books without an owner are shared.
        if (book is null || (book.UserId is not null && book.UserId != userId))
            throw new HubException("Book not found.");

        await Groups.AddToGroupAsync(Context.ConnectionId, bookId);
    }

    public async Task LeaveBook(string bookId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, bookId);
}
