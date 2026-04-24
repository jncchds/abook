#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text;
using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class WriterAgent : AgentBase
{
    public WriterAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    public async Task WriteAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        chapter.Status = ChapterStatus.Writing;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Running", ct);

        var kernel = await GetKernelAsync(bookId);
        var config = await Repo.GetLlmConfigAsync(bookId, book.UserId)!;

        var contextBlock = await BuildWriterContextAsync(bookId, chapter, config!, ct);
        var prevEnding = await GetPreviousChapterEndingAsync(bookId, chapter.Number, paragraphCount: 3);

        var history = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.WriterSystemPrompt)
            ? InterpolateSystemPrompt(book.WriterSystemPrompt, book)
            : $"""
            You are a creative fiction Writer. Write compelling, immersive prose in markdown.
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Total chapters: {book.TargetChapterCount}
            Current chapter: {chapter.Number} of {book.TargetChapterCount} � "{chapter.Title}"
            Write all content in {book.Language}.
            IMPORTANT: Do NOT begin your response with any chapter heading, title, or label.
            Start immediately with narrative prose (a scene, action, dialogue, or description).
            The character profiles and plot thread notes below are canonical � do not contradict them.
            Honour all foreshadowing and payoff directives exactly as specified.
            {contextBlock}
            {(prevEnding.Length > 0 ? $"\nThe previous chapter ended with:\n{prevEnding}\nContinue the story naturally from this point." : "")}
            """;
        history.AddSystemMessage(systemPrompt);

        history.AddUserMessage($"""
            Write the full content for:
            Chapter {chapter.Number}: {chapter.Title}
            Outline: {chapter.Outline}
            {(chapter.PovCharacter.Length > 0 ? $"Point of view: {chapter.PovCharacter}" : "")}
            {(chapter.ForeshadowingNotes.Length > 0 ? $"Plant in this chapter (foreshadowing): {chapter.ForeshadowingNotes}" : "")}
            {(chapter.PayoffNotes.Length > 0 ? $"Pay off in this chapter: {chapter.PayoffNotes}" : "")}

            Write at least 1000 words of narrative prose. Output markdown only. Do NOT include a chapter heading.
            """);

        var content = await StreamResponseAsync(kernel, history, bookId, chapterId, AgentRole.Writer, ct);
        content = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);

        chapter.Content = content;
        chapter.Status = ChapterStatus.Review;
        await Repo.UpdateChapterAsync(chapter);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Done", ct);

        // Index chapter in Qdrant for RAG (non-fatal)
        try { await IndexChapterAsync(bookId, chapter, kernel, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* Qdrant unavailable */ }
    }

    /// <summary>
    /// Assembles the rich context block injected into the Writer system prompt:
    /// Story Bible (tone + world rules), relevant character profiles, active plot threads,
    /// and RAG passages from prior chapters.
    /// </summary>
    private async Task<string> BuildWriterContextAsync(
        int bookId, Chapter chapter, LlmConfiguration config, CancellationToken ct)
    {
        var sb = new StringBuilder();

        // Story Bible
        var bible = await Repo.GetStoryBibleAsync(bookId);
        if (bible is not null)
        {
            sb.AppendLine("\n## Story Bible");
            if (!string.IsNullOrWhiteSpace(bible.ToneAndStyle))
                sb.AppendLine($"Tone & style: {bible.ToneAndStyle}");
            if (!string.IsNullOrWhiteSpace(bible.WorldRules))
                sb.AppendLine($"World rules: {bible.WorldRules}");
            if (!string.IsNullOrWhiteSpace(bible.SettingDescription))
                sb.AppendLine($"Setting: {bible.SettingDescription}");
        }

        // Relevant character profiles
        var allCards = await Repo.GetCharacterCardsAsync(bookId);
        if (allCards.Any())
        {
            List<string> involvedNames;
            try { involvedNames = JsonSerializer.Deserialize<List<string>>(chapter.CharactersInvolvedJson) ?? []; }
            catch { involvedNames = []; }

            var relevantCards = involvedNames.Count > 0
                ? allCards.Where(c => involvedNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allCards.Where(c => c.Role == CharacterRole.Protagonist || c.Role == CharacterRole.Antagonist).ToList();

            if (relevantCards.Count > 0)
            {
                sb.AppendLine("\n## Character Profiles (canonical � do not contradict)");
                foreach (var card in relevantCards)
                {
                    sb.AppendLine($"\n**{card.Name}** ({card.Role})");
                    if (!string.IsNullOrWhiteSpace(card.PhysicalDescription))
                        sb.AppendLine($"  Appearance: {card.PhysicalDescription}");
                    if (!string.IsNullOrWhiteSpace(card.Personality))
                        sb.AppendLine($"  Personality: {card.Personality}");
                    if (!string.IsNullOrWhiteSpace(card.GoalMotivation))
                        sb.AppendLine($"  Goal/motivation: {card.GoalMotivation}");
                    if (!string.IsNullOrWhiteSpace(card.Arc))
                        sb.AppendLine($"  Arc: {card.Arc}");
                }
            }
        }

        // Active plot threads for this chapter
        var allThreads = await Repo.GetPlotThreadsAsync(bookId);
        if (allThreads.Any())
        {
            List<string> activeThreadNames;
            try { activeThreadNames = JsonSerializer.Deserialize<List<string>>(chapter.PlotThreadsJson) ?? []; }
            catch { activeThreadNames = []; }

            var relevantThreads = activeThreadNames.Count > 0
                ? allThreads.Where(t => activeThreadNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
                : allThreads.Where(t => t.Status == PlotThreadStatus.Active
                    && (t.IntroducedChapterNumber == null || t.IntroducedChapterNumber <= chapter.Number)).ToList();

            if (relevantThreads.Count > 0)
            {
                sb.AppendLine("\n## Active Plot Threads");
                foreach (var t in relevantThreads)
                    sb.AppendLine($"- **{t.Name}** ({t.Type}): {t.Description}");
            }
        }

        // RAG: semantically relevant passages from prior chapters
        if (chapter.Number > 1)
        {
            var ragContext = await GetRagContextAsync(bookId, chapter.Outline, 5, LlmFactory, config, chapter.Id, ct);
            if (!string.IsNullOrWhiteSpace(ragContext))
            {
                sb.AppendLine("\n## Relevant Passages from Prior Chapters (for consistency)");
                sb.AppendLine(ragContext);
            }
        }

        return sb.ToString();
    }

    private async Task IndexChapterAsync(int bookId, Chapter chapter, Kernel kernel, LlmConfiguration config, CancellationToken ct)
    {
        await VectorStore.EnsureCollectionAsync(bookId, ct);
        await VectorStore.DeleteChapterChunksAsync(bookId, chapter.Id, ct);

        var chunks = TextChunker.Chunk(chapter.Content);
        var embedder = LlmFactory.CreateEmbeddingGeneration(config);

        for (int i = 0; i < chunks.Count; i++)
        {
            var embeddings = await embedder.GenerateEmbeddingsAsync([chunks[i]], cancellationToken: ct);
            var embedding = embeddings[0];
            await VectorStore.UpsertChunkAsync(bookId, chapter.Id, chapter.Number, i, chunks[i], embedding, ct);
        }

        int indexPromptTokens = chunks.Sum(c => c.Length) / 4;
        try { await Notifier.NotifyTokenStatsAsync(bookId, chapter.Id, AgentRole.Embedder.ToString(), indexPromptTokens, 0, ct); }
        catch { /* non-fatal */ }
        try
        {
            await Repo.AddTokenUsageAsync(new TokenUsageRecord
            {
                BookId = bookId,
                ChapterId = chapter.Id,
                AgentRole = AgentRole.Embedder,
                PromptTokens = indexPromptTokens,
                CompletionTokens = 0
            });
        }
        catch { /* non-fatal */ }
    }
}
