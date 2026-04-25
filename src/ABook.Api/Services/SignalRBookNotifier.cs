using ABook.Api.Hubs;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.SignalR;

namespace ABook.Api.Services;

public class SignalRBookNotifier : IBookNotifier
{
    private readonly IHubContext<BookHub> _hub;

    public SignalRBookNotifier(IHubContext<BookHub> hub) => _hub = hub;

    public Task StreamTokenAsync(int bookId, int? chapterId, AgentRole agentRole, string token, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentStreaming", bookId, chapterId, agentRole.ToString(), token, ct);

    public Task NotifyQuestionAsync(int bookId, AgentMessage message, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentQuestion", bookId, message, ct);

    public Task NotifyStatusChangedAsync(int bookId, AgentRole role, string state, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentStatusChanged", bookId, role.ToString(), state, ct);

    public Task NotifyChapterUpdatedAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("ChapterUpdated", bookId, chapterId, ct);

    public Task NotifyWorkflowProgressAsync(int bookId, string step, bool isComplete, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("WorkflowProgress", bookId, step, isComplete, ct);

    public Task NotifyTokenStatsAsync(int bookId, int? chapterId, string agentRole, int promptTokens, int completionTokens, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("TokenStats", bookId, chapterId, agentRole, promptTokens, completionTokens, ct);

    public Task NotifyAgentErrorAsync(int bookId, string agentRole, string errorMessage, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentError", bookId, agentRole, errorMessage, ct);
}
