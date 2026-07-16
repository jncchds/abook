using ABook.Core.Models;
using Microsoft.Extensions.AI;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Core.Interfaces;

public interface ILlmProviderFactory
{
    IChatCompletionService CreateChatCompletion(LlmConfiguration config);
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);
    Kernel CreateKernel(LlmConfiguration config);
    /// <summary>
    /// Creates provider-specific <see cref="PromptExecutionSettings"/> from the given LLM configuration,
    /// applying Temperature, TimeoutMs, ReasoningEffort, and MaxTokens mapped to each provider's native
    /// parameters. When a JSON schema is provided, constrains output format accordingly.
    /// </summary>
    PromptExecutionSettings CreateExecutionSettings(LlmConfiguration config, string? jsonSchema = null);
}
