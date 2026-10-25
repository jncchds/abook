using System.Text.Json.Serialization;

namespace ABook.Core.Models;

public class Book
{
    public int Id { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Premise { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int TargetChapterCount { get; set; }
    public BookStatus Status { get; set; } = BookStatus.Draft;
    public string Language { get; set; } = "English";
    public string? StoryBibleSystemPrompt { get; set; }
    public string? CharactersSystemPrompt { get; set; }
    public string? PlotThreadsSystemPrompt { get; set; }
    public string? ChapterOutlinesSystemPrompt { get; set; }
    public string? WriterSystemPrompt { get; set; }
    public string? EditorSystemPrompt { get; set; }
    public string? ContinuityCheckerSystemPrompt { get; set; }
    public string? IllustratorSystemPrompt { get; set; }
    /// <summary>When true, the workflow plans illustrations (prompts and positions) for each finished chapter and the cover.</summary>
    public bool IllustrationsEnabled { get; set; } = false;
    /// <summary>
    /// When true, exports (HTML, EPUB, FB2) and the reader show each section as a part heading ("I. Title") before
    /// its first chapter. Off by default: sections are a planning tool.
    /// </summary>
    public bool ShowSectionsInOutput { get; set; }

    // ── Art direction: written once per book by the illustration planner, editable, and added word for
    //    word to every image at render time so all portraits and scenes share one style. ─────────────
    /// <summary>Style description (medium, technique, palette, lighting, mood) prepended to every image prompt.</summary>
    public string? IllustrationStyle { get; set; }
    /// <summary>Composition shared by every character's reference portrait (framing, pose, backdrop, light).</summary>
    public string? IllustrationPortraitFraming { get; set; }
    /// <summary>Negative prompt added to every image's own negative prompt.</summary>
    public string? IllustrationNegativePrompt { get; set; }
    /// <summary>The configured visual style the art direction was written from; a change rewrites it.</summary>
    public string? IllustrationStyleSource { get; set; }
    /// <summary>Autonomous (the planning pipeline) or Interactive (the book chat). Replaces the old human-assisted flag.</summary>
    public BookMode Mode { get; set; } = BookMode.Autonomous;
    /// <summary>
    /// Interactive books only: when the premise and Story Bible were locked and writing began. Null while the
    /// book is in its foundation stage, where the chat may still change them; set, they are fixed canon.
    /// Only the author can unlock them again (Book Settings).
    /// </summary>
    public DateTime? StoryBibleLockedAt { get; set; }
    /// <summary>Rolling summary of the book chat older than the turns sent verbatim with each request.</summary>
    [JsonIgnore] public string ChatMemory { get; set; } = string.Empty;
    /// <summary>The last chat message <see cref="ChatMemory"/> covers.</summary>
    [JsonIgnore] public int? ChatMemoryThroughMessageId { get; set; }
    /// <summary>Complete once the planner's clarifying questions have been asked; later planning runs skip them.</summary>
    public PlanningPhaseStatus ClarificationsStatus { get; set; } = PlanningPhaseStatus.NotStarted;
    public PlanningPhaseStatus StoryBibleStatus { get; set; } = PlanningPhaseStatus.NotStarted;
    public PlanningPhaseStatus CharactersStatus { get; set; } = PlanningPhaseStatus.NotStarted;
    public PlanningPhaseStatus PlotThreadsStatus { get; set; } = PlanningPhaseStatus.NotStarted;
    public PlanningPhaseStatus ChaptersStatus { get; set; } = PlanningPhaseStatus.NotStarted;
    public int? BaseBookId { get; set; }
    public DateTime? SettingsCopiedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public int? UserId { get; set; }
    [JsonIgnore] public AppUser? User { get; set; }
    [JsonIgnore] public Book? BaseBook { get; set; }
    [JsonIgnore] public ICollection<Book> DerivedBooks { get; set; } = new List<Book>();

    public ICollection<Chapter> Chapters { get; set; } = new List<Chapter>();
    /// <summary>The plan's sections (loaded with the chapters by the detail queries; empty otherwise).</summary>
    public ICollection<Section> Sections { get; set; } = new List<Section>();
    [JsonIgnore] public ICollection<AgentMessage> AgentMessages { get; set; } = new List<AgentMessage>();
    [JsonIgnore] public ICollection<LlmConfiguration> LlmConfigurations { get; set; } = new List<LlmConfiguration>();
    [JsonIgnore] public StoryBible? StoryBible { get; set; }
    [JsonIgnore] public ICollection<CharacterCard> CharacterCards { get; set; } = new List<CharacterCard>();
    [JsonIgnore] public ICollection<PlotThread> PlotThreads { get; set; } = new List<PlotThread>();
    /// <summary>
    /// The active clarifications, filled (untracked) by the repository's single-book loads so prompts can
    /// append them to the premise. Not an EF navigation: saving the book must never write them back.
    /// </summary>
    [JsonIgnore, System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public IReadOnlyList<Clarification> Clarifications { get; set; } = [];
}
