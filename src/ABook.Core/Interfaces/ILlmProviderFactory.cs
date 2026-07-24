using ABook.Core.Models;
using Microsoft.Extensions.AI;

namespace ABook.Core.Interfaces;

public interface ILlmProviderFactory
{
    ILlmChatClient CreateChatClient(LlmConfiguration config);
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);
}
