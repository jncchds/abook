using Microsoft.AspNetCore.SignalR;

namespace ABook.Api.Hubs;

public class BookHub : Hub
{
    public async Task JoinBook(string bookId) =>
        await Groups.AddToGroupAsync(Context.ConnectionId, bookId);

    public async Task LeaveBook(string bookId) =>
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, bookId);
}
