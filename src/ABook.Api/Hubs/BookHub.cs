using ABook.Api.Services;
using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace ABook.Api.Hubs;

/// <summary>
/// Real-time channel for a book's agent activity (streamed content, questions, status). A connection
/// only joins the group of a book its user may access — the stream carries the book's full text.
/// Every event arrives as <c>BookEvent</c> with a per-book sequence number (see <see cref="BookEventLog"/>),
/// so a client knows exactly what it missed and gets it replayed when it rejoins.
/// </summary>
[Authorize]
public class BookHub(IBookRepository repo, BookEventLog events) : Hub
{
    private const string JoinedKey = "joinedBooks";

    /// <summary>
    /// Joins the book's group. A client that was joined before passes the session id and the last
    /// sequence number it handled, and gets the events it missed since then (or null: re-read everything).
    /// </summary>
    public async Task<BookSyncState> JoinBook(string bookId, string? epoch, long? afterSeq)
    {
        if (!int.TryParse(bookId, out var id))
            throw new HubException("Invalid book id.");

        var book = await repo.GetByIdAsync(id);
        var userIdClaim = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        int? userId = int.TryParse(userIdClaim, out var uid) ? uid : null;
        // Same rule as the REST ownership check: books without an owner are shared.
        if (book is null || (book.UserId is not null && book.UserId != userId))
            throw new HubException("Book not found.");

        var state = await events.JoinAsync(id, () => Groups.AddToGroupAsync(Context.ConnectionId, bookId), epoch, afterSeq);
        lock (Context.Items) JoinedBooks().Add(id);
        return state;
    }

    public async Task LeaveBook(string bookId)
    {
        if (int.TryParse(bookId, out var id))
            lock (Context.Items) JoinedBooks().Remove(id);
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, bookId);
    }

    /// <summary>
    /// Lets the client check that the connection still works and that it has every event sent so far.
    /// Fails when this connection is not in the book's group, so the client rejoins.
    /// </summary>
    public BookSyncState Heartbeat(string bookId)
    {
        if (!int.TryParse(bookId, out var id))
            throw new HubException("Invalid book id.");
        bool joined;
        lock (Context.Items) joined = JoinedBooks().Contains(id);
        if (!joined)
            throw new HubException("Not joined.");
        return new BookSyncState(events.Epoch, events.LastSent(id), null);
    }

    private HashSet<int> JoinedBooks()
    {
        if (Context.Items.TryGetValue(JoinedKey, out var v) && v is HashSet<int> set) return set;
        set = [];
        Context.Items[JoinedKey] = set;
        return set;
    }
}
