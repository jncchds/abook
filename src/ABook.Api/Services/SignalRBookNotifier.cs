using ABook.Api.Hubs;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace ABook.Api.Services;

public class SignalRBookNotifier : IBookNotifier
{
    private readonly IHubContext<BookHub> _hub;
    private readonly IServiceScopeFactory _scopeFactory;

    public SignalRBookNotifier(IHubContext<BookHub> hub, IServiceScopeFactory scopeFactory)
    {
        _hub = hub;
        _scopeFactory = scopeFactory;
    }

    public Task StreamTokenAsync(int bookId, int? chapterId, AgentRole agentRole, string token, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentStreaming", bookId, chapterId, agentRole.ToString(), token, ct);

    public Task NotifyQuestionAsync(int bookId, AgentMessage message, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentQuestion", bookId, message, ct);

    public Task NotifyStatusChangedAsync(int bookId, AgentRole role, string state, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentStatusChanged", bookId, role.ToString(), state, ct);

    public Task NotifyChapterUpdatedAsync(int bookId, int chapterId, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("ChapterUpdated", bookId, chapterId, ct);

    public async Task NotifyWorkflowProgressAsync(int bookId, string step, bool isComplete, CancellationToken ct = default)
    {
        await _hub.Clients.Group(bookId.ToString()).SendAsync("WorkflowProgress", bookId, step, isComplete, ct);
        // Persist step to DB for history (best-effort, non-fatal)
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var repo = scope.ServiceProvider.GetRequiredService<IBookRepository>();
            string? endpoint = null, modelName = null;
            try
            {
                var cfg = await repo.GetLlmConfigAsync(bookId);
                endpoint = cfg?.Endpoint;
                modelName = cfg?.ModelName;
            }
            catch { /* non-fatal */ }
            await repo.AddWorkflowStepAsync(bookId, step, endpoint, modelName, CancellationToken.None);
        }
        catch { /* non-fatal */ }
    }

    public Task NotifyTokenStatsAsync(int bookId, int? chapterId, string agentRole, int promptTokens, int completionTokens, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("TokenStats", bookId, chapterId, agentRole, promptTokens, completionTokens, ct);

    public Task NotifyAgentErrorAsync(int bookId, string agentRole, string errorMessage, CancellationToken ct = default) =>
        _hub.Clients.Group(bookId.ToString()).SendAsync("AgentError", bookId, agentRole, errorMessage, ct);
}
