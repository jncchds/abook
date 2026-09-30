namespace ABook.Core.Models;

public enum BookStatus { Draft, InProgress, Complete }

public enum PlanningPhaseStatus { NotStarted, Complete }

public enum ChapterStatus { Outlined, Writing, Review, Editing, Done }

public enum AgentRole { Planner, Writer, Editor, ContinuityChecker, Embedder, StoryBibleAgent, CharactersAgent, PlotThreadsAgent, ChaptersAgent, IllustrationPlanner, Illustrator, WorkflowStep = 99 }

public enum MessageType { Content, Question, Answer, SystemNote, Feedback }

public enum LlmProvider { Ollama, OpenAI, GoogleAIStudio, OpenAICompatible }

public enum ImageProvider { ZImage }

public enum IllustrationKind { Scene, Cover, Portrait }

public enum IllustrationOrientation { Portrait, Landscape, Square }

public enum IllustrationStatus { Planned, Rendered, Failed }

public enum CharacterRole { Protagonist, Antagonist, Supporting, Minor }

public enum PlotThreadType { MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, ThematicThread }

public enum PlotThreadStatus { Active, Resolved, Dormant }

public enum AgentRunPersistStatus
{
    Running,
    WaitingForInput,
    Completed,
    Failed,
    Cancelled,
    Orphaned
}
