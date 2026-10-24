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
    /// <summary>
    /// Rewinds the whole book and its chat to <paramref name="at"/> as a new state (<see cref="IBookRewinder"/>).
    /// <paramref name="fingerprint"/> (from the preview) refuses the rewind if the book changed since.
    /// </summary>
    Task RewindAsync(int bookId, DateTime at, string? fingerprint, string? origin, CancellationToken ct = default);
    /// <param name="origin">Where the answer came from when not the web UI (e.g. "Telegram · @anna · Writers' room").</param>
    Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default, string? origin = null);
    /// <summary>
    /// Interactive books: answers the author's chat message <paramref name="authorMessageId"/>, or writes the
    /// opening reply of a new book when null. The reply's changes are only proposed.
    /// </summary>
    Task RunBookChatTurnAsync(int bookId, int? authorMessageId, CancellationToken ct = default);
    /// <summary>Interactive books: applies the proposals the author approved (writing chapters when asked).</summary>
    Task ApplyProposalsAsync(int bookId, IReadOnlyList<int> proposalIds, CancellationToken ct = default);
    /// <summary>Interactive books: undoes an applied proposal.</summary>
    Task UndoProposalAsync(int bookId, int proposalId, CancellationToken ct = default);
    /// <summary>LLM-only: writes illustration prompts and positions. Nothing is rendered.</summary>
    Task StartIllustrationPlanningAsync(int bookId, IllustrationPlanRequest request, CancellationToken ct = default);
    /// <summary>Image-model only: renders planned illustrations. Makes no LLM calls.</summary>
    Task StartRenderAsync(int bookId, RenderRequest request, CancellationToken ct = default);
}

public enum IllustrationPlanScope
{
    /// <summary>Finished chapters without illustrations, then the cover once every chapter is finished.</summary>
    Missing,
    /// <summary>Re-plan one chapter (TargetId = chapter id).</summary>
    Chapter,
    Cover,
    /// <summary>Rewrite every character's visual description and portrait prompt.</summary>
    Characters,
    /// <summary>Rewrite the book's art direction (style, portrait composition, book-wide negative prompt).</summary>
    Style,
}

public record IllustrationPlanRequest(IllustrationPlanScope Scope, int? TargetId = null);

public enum RenderScope
{
    /// <summary>Everything not yet rendered: portraits, cover, then scenes in reading order.</summary>
    All,
    Portraits,
    /// <summary>Unrendered scenes of one chapter (TargetId = chapter id).</summary>
    Chapter,
    /// <summary>Re-renders the portrait and every scene centred on one character (TargetId = character id).</summary>
    Character,
    /// <summary>Re-renders one illustration (TargetId = illustration id).</summary>
    Single,
}

/// <param name="Reroll">Single scope only: render with a new random seed. For a portrait the new seed becomes the character's.</param>
public record RenderRequest(RenderScope Scope, int? TargetId = null, bool Reroll = false);

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
