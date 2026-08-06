using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;

namespace ABook.Api.Services;

/// <summary>
/// Embeds chapter versions created outside an agent run — i.e. the author's own edits and any
/// older version they re-activate. Agents index their output inline; edits arriving over REST are
/// indexed here on a detached scope so saving a chapter stays fast while RAG still sees the
/// author's text on the next agent step.
/// </summary>
public class ChapterIndexingService(IServiceScopeFactory scopeFactory, ILogger<ChapterIndexingService> logger)
{
    /// <summary>Fire-and-forget: indexes the version in its own DI scope. Never throws.</summary>
    public void QueueIndex(int bookId, int chapterId, int chapterVersionId) =>
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var sp = scope.ServiceProvider;
                var repo = sp.GetRequiredService<IBookRepository>();
                var book = await repo.GetByIdAsync(bookId);
                var config = await repo.GetLlmConfigAsync(bookId, book?.UserId);
                if (config is null)
                {
                    logger.LogWarning("[Book {BookId}] No LLM configuration — author edit to chapter {ChapterId} left unembedded.",
                        bookId, chapterId);
                    return;
                }

                var notifier = sp.GetRequiredService<IBookNotifier>();
                var indexed = await ChapterIndexer.IndexVersionAsync(
                    repo,
                    sp.GetRequiredService<ILlmProviderFactory>(),
                    sp.GetRequiredService<IVectorStoreService>(),
                    config, bookId, chapterId, chapterVersionId,
                    (embeddedChars, ex) => RecordUsageAsync(repo, notifier, bookId, chapterId, embeddedChars, config, ex),
                    CancellationToken.None);

                if (indexed)
                    logger.LogInformation("[Book {BookId}] Embedded author edit of chapter {ChapterId} (version {VersionId}).",
                        bookId, chapterId, chapterVersionId);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "[Book {BookId}] Failed to embed chapter {ChapterId} version {VersionId}.",
                    bookId, chapterId, chapterVersionId);
            }
        });

    private static async Task RecordUsageAsync(
        IBookRepository repo, IBookNotifier notifier, int bookId, int chapterId, int embeddedChars,
        LlmConfiguration config, Exception? failure)
    {
        var promptTokens = embeddedChars / 4;
        var failureReason = failure is null ? null : $"{failure.GetType().Name}: {failure.Message}";
        try { await notifier.NotifyTokenStatsAsync(bookId, chapterId, AgentRole.Embedder.ToString(), promptTokens, 0); }
        catch { /* non-fatal */ }
        await repo.AddTokenUsageAsync(new TokenUsageRecord
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = AgentRole.Embedder,
            PromptTokens = promptTokens,
            CompletionTokens = 0,
            Endpoint = config.Endpoint,
            ModelName = config.EmbeddingModelName,
            Failed = failureReason is not null,
            FailureReason = failureReason,
        });
    }
}
