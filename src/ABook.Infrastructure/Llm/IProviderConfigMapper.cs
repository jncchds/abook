using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm;

/// <summary>
/// Maps an LLM configuration to provider-specific ChatOptions. Each provider maps Temperature,
/// MaxOutputTokens, ReasoningEffort (OpenAI), TimeoutMs, and JSON schema response format differently.
/// Provider-specific extras are set via ChatOptions.Metadata.
/// </summary>
public interface IProviderConfigMapper
{
    LlmProvider Provider { get; }
    ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null);

    /// <summary>Creates an embedding generator for the configured provider.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);
}
