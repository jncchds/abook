using ABook.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Embeddings;

namespace ABook.Core.Interfaces;

public interface ILlmProviderFactory
{
    IChatCompletionService CreateChatCompletion(LlmConfiguration config);
    ITextEmbeddingGenerationService CreateEmbeddingGeneration(LlmConfiguration config);
    Kernel CreateKernel(LlmConfiguration config);
}
