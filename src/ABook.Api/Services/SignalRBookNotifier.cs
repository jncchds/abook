using ABook.Api.Hubs;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace ABook.Api.Services;

public class SignalRBookNotifier : IBookNotifier
{
    private readonly IHubContext<BookHub> _hub;

    public SignalRBookNotifier(IHubContext<BookHub> hub) => _hub = hub;

    public Task StreamTokenAsync(int bookId, int? chapterId, string token, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentStreaming", bookId, chapterId, token, ct);

    public Task NotifyQuestionAsync(int bookId, AgentMessage message, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentQuestion", bookId, message, ct);

    public Task NotifyStatusChangedAsync(int bookId, AgentRole role, string state, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentStatusChanged", bookId, role.ToString(), state, ct);

    public Task NotifyChapterUpdatedAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("ChapterUpdated", bookId, chapterId, ct);

    public Task NotifyWorkflowProgressAsync(int bookId, string step, bool isComplete, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("WorkflowProgress", bookId, step, isComplete, ct);
}
