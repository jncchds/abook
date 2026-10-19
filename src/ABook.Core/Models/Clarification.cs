using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// An author decision that refines the premise: a clarifying question from the planner with the author's
/// answer, or a note the author added after a planning phase. The planner's questions are saved open
/// (unanswered) and answered on the Clarifications page, so nothing is lost if the run is interrupted.
/// Answered ones are appended to the premise wherever the premise reaches a prompt; archived ones are
/// kept for reference and never sent.
/// </summary>
public class Clarification
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public ClarificationSource Source { get; set; } = ClarificationSource.Author;
    public string Question { get; set; } = string.Empty;
    /// <summary>Empty while the planner's question awaits the author's answer.</summary>
    public string Answer { get; set; } = string.Empty;
    /// <summary>When the answer was first given; set by the repository. Null while the question is open.</summary>
    public DateTime? AnsweredAt { get; set; }
    /// <summary>
    /// For a PhaseNote: the chat question it answers (its answer message points back at that question).
    /// For a ChatSummary: the review pause whose section chat it summarises.
    /// Null for other sources and for phase notes saved before v0.3.0.
    /// </summary>
    public int? QuestionMessageId { get; set; }
    public bool IsArchived { get; set; }

    /// <summary>Awaiting an answer: active and not yet answered. Archiving an open one skips it.</summary>
    public bool IsOpen => !IsArchived && string.IsNullOrWhiteSpace(Answer);
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
}
