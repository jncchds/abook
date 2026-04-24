using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IAgentOrchestrator
{
    Task StartPlanningAsync(int bookId, CancellationToken ct = default);
    Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default);
    Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default);
    Task StartContinuityCheckAsync(int bookId, CancellationToken ct = default);
    Task StartWorkflowAsync(int bookId, CancellationToken ct = default);
    Task ContinueWorkflowAsync(int bookId, CancellationToken ct = default);
    Task ContinuePlanningAsync(int bookId, CancellationToken ct = default);
    Task StopWorkflowAsync(int bookId);
    Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default);
    Task<AgentRunStatus?> GetRunStatusAsync(int bookId);
}

public record AgentRunStatus(AgentRole Role, string State, int? ChapterId);
