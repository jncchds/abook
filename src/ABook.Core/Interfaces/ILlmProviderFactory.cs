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
    /// Creates provider-specific <see cref="PromptExecutionSettings"/> with the correct JSON output
    /// format parameter for the given provider when <paramref name="jsonMode"/> is <c>true</c>.
    /// </summary>
    PromptExecutionSettings CreateExecutionSettings(LlmConfiguration config, float temperature, bool jsonMode = false);
}
