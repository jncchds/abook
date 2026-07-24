using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Infrastructure.Llm;

public interface ILlmProviderStrategy
{
    LlmProvider Provider { get; }
    ILlmChatClient CreateChatClient(LlmConfiguration config);
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);
}
