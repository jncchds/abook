using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Infrastructure.Llm;

public interface ILlmProviderStrategy
{
    LlmProvider Provider { get; }
    IChatCompletionService CreateChatCompletion(LlmConfiguration config);
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);
    void ConfigureKernelBuilder(IKernelBuilder builder, LlmConfiguration config);
    /// <summary>
    /// Creates provider-specific execution settings from the given LLM configuration. Reads
    /// Temperature, TimeoutMs, ReasoningEffort, and MaxTokens from config and maps them to
    /// the provider's native parameters (or ExtensionData for Ollama). When <paramref name="jsonSchema"/>
    /// is non-null, the settings constrain JSON output format.
    /// </summary>
    PromptExecutionSettings CreateExecutionSettings(LlmConfiguration config, string? jsonSchema = null);
}
