namespace ABook.Core.Models;

public enum BookStatus { Draft, InProgress, Complete }

public enum ChapterStatus { Outlined, Writing, Review, Editing, Done }

public enum AgentRole { Planner, Writer, Editor, ContinuityChecker }

public enum MessageType { Content, Question, Answer, SystemNote, Feedback }

public enum LlmProvider { Ollama, OpenAI, AzureOpenAI, Anthropic }
