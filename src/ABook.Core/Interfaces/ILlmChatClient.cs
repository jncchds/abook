using ABook.Core.Models;

namespace ABook.Core.Interfaces;

public interface ILlmChatClient
{
    IAsyncEnumerable<LlmStreamChunk> StreamAsync(
        IReadOnlyList<LlmChatMessage> messages,
        LlmChatOptions options,
        CancellationToken ct = default);
}
