namespace ABook.Core.Models;

public enum BookStatus { Draft, InProgress, Complete }

public enum PlanningPhaseStatus { NotStarted, Complete }

public enum ChapterStatus { Outlined, Writing, Review, Editing, Done }

public enum AgentRole { Planner, Writer, Editor, ContinuityChecker, Embedder }

public enum MessageType { Content, Question, Answer, SystemNote, Feedback }

public enum LlmProvider { Ollama, OpenAI, AzureOpenAI, Anthropic, LMStudio }

public enum CharacterRole { Protagonist, Antagonist, Supporting, Minor }

public enum PlotThreadType { MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, ThematicThread }

public enum PlotThreadStatus { Active, Resolved, Dormant }
