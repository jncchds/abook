namespace ABook.Core.Models;

public enum LlmChatRole { System, User, Assistant }

public record LlmChatMessage(LlmChatRole Role, string Content);

public record LlmStreamChunk(string? Content, string? Reasoning);

public class LlmChatOptions
{
    public float? Temperature { get; init; }
    public int? MaxTokens { get; init; }
    public string? JsonSchema { get; init; }
    public string? ReasoningEffort { get; init; }
    public int? TimeoutMs { get; init; }
}
