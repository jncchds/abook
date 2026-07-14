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
    /// Creates provider-specific execution settings with the appropriate JSON output format parameter
    /// when a schema is provided. When <paramref name="jsonSchema"/> is null, no response format constraint is applied.
    /// </summary>
    PromptExecutionSettings CreateExecutionSettings(float temperature, string? jsonSchema = null);
}
