using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public enum BookChatRole
{
    /// <summary>A message the author typed.</summary>
    Author,
    /// <summary>The LLM's reply, possibly with conflicts and proposed changes.</summary>
    Assistant,
    /// <summary>Something that happened to the book outside the conversation (bible locked, chapter written…).</summary>
    Event,
}

public enum BookChatMessageStatus
{
    /// <summary>An author message whose reply is still being written.</summary>
    Pending,
    Done,
    /// <summary>An author message whose reply failed or was stopped; it can be retried.</summary>
    Failed,
}

/// <summary>One message of an Interactive book's chat.</summary>
public class BookChatMessage
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public BookChatRole Role { get; set; }
    public string Content { get; set; } = string.Empty;
    /// <summary>Assistant replies: JSON array of the conflicts with the book's canon the LLM flagged.</summary>
    public string ConflictsJson { get; set; } = "[]";
    public BookChatMessageStatus Status { get; set; } = BookChatMessageStatus.Done;
    /// <summary>Author messages not typed in the web UI: where they came from, e.g. "Telegram · @anna · Writers' room".</summary>
    public string? Origin { get; set; }
    /// <summary>
    /// Set when the book was rewound to a moment before this message: it stays in the record (collapsed in the UI) but is
    /// never sent to the LLM again and takes no part in the conversation.
    /// </summary>
    public DateTime? HiddenAt { get; set; }
    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
    public ICollection<ChatProposal> Proposals { get; set; } = new List<ChatProposal>();
}

public enum ChatProposalStatus
{
    /// <summary>Waiting for the author to apply or reject it.</summary>
    Pending,
    /// <summary>Being applied (a long action such as writing a chapter is running).</summary>
    Applying,
    Applied,
    Rejected,
    /// <summary>A later proposal on the same target, or the bible lock, made it obsolete.</summary>
    Superseded,
    Failed,
    /// <summary>Applied, then undone by the author.</summary>
    Undone,
    /// <summary>
    /// A chapter write or revision of a batch, waiting its turn: it starts once the one before it
    /// (<see cref="ChatProposal.QueuedAfterProposalId"/>) is written, checked and its follow-ups are applied or rejected.
    /// </summary>
    Queued,
}

/// <summary>
/// One change the LLM proposed in a chat reply. Nothing happens to the book until the author applies it;
/// applied changes keep what they replaced, so they can be undone.
/// </summary>
public class ChatProposal
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int MessageId { get; set; }
    /// <summary>Position in the reply; applying several runs them in this order.</summary>
    public int Ordinal { get; set; }
    /// <summary>The action type (<c>upsert_character</c>, <c>write_chapter</c> …).</summary>
    public string Type { get; set; } = string.Empty;
    /// <summary>Short description shown on the card.</summary>
    public string Label { get; set; } = string.Empty;
    /// <summary>The normalized action, as JSON.</summary>
    public string PayloadJson { get; set; } = "{}";
    /// <summary>
    /// What it changes: <c>premise</c>, <c>bible</c>, <c>character:Name</c>, <c>thread:Name</c>,
    /// <c>chapter:Id</c> (outline), <c>prose:Id</c>, <c>note</c>. A pending proposal is superseded by a newer one
    /// on the same target. Null for an added chapter until it is applied.
    /// </summary>
    public string? Target { get; set; }
    /// <summary>A proposal this one needs applied first (writing a chapter that the same reply adds).</summary>
    public int? DependsOnProposalId { get; set; }
    /// <summary>A queued write/revision: the one whose full cycle (write, check, the author's review) it waits for.</summary>
    public int? QueuedAfterProposalId { get; set; }
    /// <summary>Alternatives offered for a conflict share a group ("A", "B"); applying one rejects the others.</summary>
    public string? OptionGroup { get; set; }
    /// <summary>A conflict with the book's canon that this change would cause or resolve.</summary>
    public string? ConflictNote { get; set; }
    public ChatProposalStatus Status { get; set; } = ChatProposalStatus.Pending;
    /// <summary>The target as it read when the change was proposed, and as the change would leave it.</summary>
    public string PreviewBefore { get; set; } = string.Empty;
    public string PreviewAfter { get; set; } = string.Empty;
    /// <summary>The target before and after the change was applied — what Undo restores and checks against.</summary>
    public string? BeforeJson { get; set; }
    public string? AfterJson { get; set; }
    /// <summary>Prose changes: the chapter version active before and the one the change created.</summary>
    public int? BeforeVersionId { get; set; }
    public int? AfterVersionId { get; set; }
    /// <summary>What applying (or undoing) did, or why it failed.</summary>
    public string? ResultNote { get; set; }
    public DateTime? AppliedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public BookChatMessage Message { get; set; } = null!;
}
