using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Core.Interfaces;

public interface ILlmProviderFactory
{
    /// <summary>Creates a chat client for streaming completions.</summary>
    IChatClient CreateChatClient(LlmConfiguration config);

    /// <summary>Creates an embedding generator for RAG context retrieval and chapter indexing.</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);

    /// <summary>Builds ChatOptions from the given LLM configuration, applying Temperature, MaxOutputTokens,
    /// ReasoningEffort (OpenAI), TimeoutMs mapping, and JSON schema response format constraints.
    /// Provider-specific extras are set via ChatOptions.Metadata.</summary>
    ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null);
}
