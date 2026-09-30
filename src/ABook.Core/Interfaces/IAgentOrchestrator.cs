using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface IAgentOrchestrator
{
    Task StartPlanningAsync(int bookId, CancellationToken ct = default);
    Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default);
    Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default);
    /// <summary>Checks one chapter when <paramref name="chapterId"/> is given, otherwise the whole manuscript.</summary>
    Task StartContinuityCheckAsync(int bookId, int? chapterId = null, CancellationToken ct = default);
    Task StartWorkflowAsync(int bookId, CancellationToken ct = default);
    Task ContinueWorkflowAsync(int bookId, CancellationToken ct = default);
    Task ContinuePlanningAsync(int bookId, CancellationToken ct = default);
    Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default);
}

public enum RunStartResult
{
    Success,
    BookBusy,
    AtCapacity
}

public record AgentRunStatus(AgentRole Role, string State, int? ChapterId)
{
    /// <summary>True while the run occupies the book (and a concurrency slot).</summary>
    public bool IsActive => State is RunStates.Running or RunStates.WaitingForInput;
}

/// <summary>
/// Values of <see cref="AgentRunStatus.State"/> and of the state argument of the AgentStatusChanged
/// SignalR event. Sent to the UI as plain strings.
/// </summary>
public static class RunStates
{
    public const string Running = "Running";
    public const string WaitingForInput = "WaitingForInput";
    public const string Done = "Done";
    public const string Failed = "Failed";
    public const string Cancelled = "Cancelled";
}
