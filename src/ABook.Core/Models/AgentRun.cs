namespace ABook.Core.Models;

/// <summary>
/// Persisted record of an agent run, enabling automatic resume after app restarts.
/// A run is created when a workflow/plan starts and updated as it progresses.
/// When the run pauses to ask a question (WaitingForInput), the PendingMessageId
/// and WorkflowContext are persisted so the run can be resumed from scratch on a
/// fresh process without losing progress.
/// </summary>
public class AgentRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public int BookId { get; set; }

    /// <summary>
    /// Human-readable identifier for the workflow type (e.g. "workflow", "plan", "write", "edit", "continuity").
    /// Used by the recovery service to know which orchestrator method to call on resume.
    /// </summary>
    public string RunType { get; set; } = string.Empty;

    public AgentRunPersistStatus Status { get; set; } = AgentRunPersistStatus.Running;

    /// <summary>The agent role currently executing or most recently executing.</summary>
    public AgentRole CurrentRole { get; set; }

    /// <summary>The chapter being processed, if applicable.</summary>
    public int? ChapterId { get; set; }

    /// <summary>
    /// FK to the AgentMessage that the run is waiting for a user answer on.
    /// Null when the run is not waiting for input.
    /// </summary>
    public int? PendingMessageId { get; set; }

    /// <summary>
    /// Serialised JSON checkpoint capturing enough state to resume the workflow
    /// after a process restart. Contents depend on RunType; at minimum stores the
    /// last completed step name and any partial output produced before the pause.
    /// </summary>
    public string? WorkflowContext { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
    public AgentMessage? PendingMessage { get; set; }
}
