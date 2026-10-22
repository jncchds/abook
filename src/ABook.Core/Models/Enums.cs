namespace ABook.Core.Models;

public enum BookStatus { Draft, InProgress, Complete }

public enum PlanningPhaseStatus { NotStarted, Complete }

public enum ChapterStatus { Outlined, Writing, Review, Editing, Done }

public enum AgentRole { Planner, Writer, Editor, ContinuityChecker, Embedder, StoryBibleAgent, CharactersAgent, PlotThreadsAgent, ChaptersAgent, IllustrationPlanner, Illustrator,
    /// <summary>Legacy (up to v0.3.1): the LLM the author chatted with about a section during a human-assisted pause.</summary>
    SectionChat,
    /// <summary>The LLM the author writes an Interactive book with: replies and proposes changes.</summary>
    BookChat,
    /// <summary>Summarizes what a written chapter tells and the facts it establishes.</summary>
    ChapterSummarizer,
    WorkflowStep = 99 }

/// <summary>
/// How a book is written. Autonomous: the planning pipeline, then each chapter written, checked and edited
/// in turn. Interactive: the author and the LLM build the book in a chat, one approved change at a time.
/// </summary>
public enum BookMode { Autonomous, Interactive }

public enum MessageType
{
    Content, Question, Answer, SystemNote, Feedback,
    /// <summary>Section chat: the section as it stood when the chat began (sent to the LLM, hidden in the UI).</summary>
    ChatContext,
    /// <summary>Section chat: a message the author typed.</summary>
    ChatUser,
    /// <summary>Section chat: the LLM's reply.</summary>
    ChatReply,
    /// <summary>
    /// Section chat: one change made to the section — JSON <c>{ replyId, before, after }</c> holding the
    /// section rendered before and after it. Shown as a diff on its reply and used by Undo; hidden in the UI.
    /// </summary>
    ChatChange,
}

public enum LlmProvider { Ollama, OpenAI, GoogleAIStudio, OpenAICompatible }

public enum ImageProvider { ZImage, UnslothStudio }

public enum IllustrationKind { Scene, Cover, Portrait }

public enum IllustrationOrientation { Portrait, Landscape, Square }

public enum IllustrationStatus { Planned, Rendered, Failed }

public enum VisualAssetKind { Object, Outfit, Location, Creature, Vehicle, Other }

public enum CharacterRole { Protagonist, Antagonist, Supporting, Minor }

public enum PlotThreadType { MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, ThematicThread }

public enum PlotThreadStatus { Active, Resolved, Dormant }

/// <summary>
/// Where a clarification came from: a planner question answered on the Clarifications page, an author
/// note after a planning phase (up to v0.3.0), one the author added, one imported from the chat-based
/// Q&amp;A of older books, the decisions of a section chat the author saved when completing a review pause
/// (up to v0.3.1), or a lasting decision noted from the book chat of an Interactive book.
/// </summary>
public enum ClarificationSource { Planner, PhaseNote, Author, Imported, ChatSummary, ChatNote }

public enum AgentRunPersistStatus
{
    Running,
    WaitingForInput,
    Completed,
    Failed,
    Cancelled,
    Orphaned
}
