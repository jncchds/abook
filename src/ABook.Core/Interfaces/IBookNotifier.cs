using ABook.Core.Models;

namespace ABook.Core.Interfaces;

/// <summary>Abstraction for pushing real-time notifications to connected UI clients.</summary>
public interface IBookNotifier
{
    Task StreamTokenAsync(int bookId, int? chapterId, string token, CancellationToken ct = default);
    Task NotifyQuestionAsync(int bookId, AgentMessage message, CancellationToken ct = default);
    Task NotifyStatusChangedAsync(int bookId, AgentRole role, string state, CancellationToken ct = default);
    Task NotifyChapterUpdatedAsync(int bookId, int chapterId, CancellationToken ct = default);
    Task NotifyWorkflowProgressAsync(int bookId, string step, bool isComplete, CancellationToken ct = default);
    Task NotifyTokenStatsAsync(int bookId, int? chapterId, string agentRole, int promptTokens, int completionTokens, CancellationToken ct = default);
}
