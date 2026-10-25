using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;
using Microsoft.Extensions.Logging;

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

    public async Task WriteAsync(int bookId, int chapterId, CancellationToken ct = default, string? authorInstructions = null)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = EnsureNotArchived(await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));

        chapter.Status = ChapterStatus.Writing;
        await Repo.UpdateChapterAsync(chapter);
        await ReportRunningAsync(bookId, AgentRole.Writer, chapterId, ct);

        var (client, config) = await GetChatClientAsync(bookId);

        var bible = await Repo.GetStoryBibleAsync(bookId);
        var contextBlock = await BuildWriterContextAsync(bookId, chapter, bible, config!, ct);
        var synopsesBlock = await BuildChapterSynopsesAsync(bookId, chapter.Number, ct);
        var prevEnding = await GetPreviousChapterEndingAsync(bookId, chapter.Number, paragraphCount: 3);
        var sectionContext = await BuildSectionContextAsync(bookId, chapter);

        var basePrompt = !string.IsNullOrWhiteSpace(book.WriterSystemPrompt)
            ? InterpolateSystemPrompt(book.WriterSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Writer, book, bible);
        var messages = new List<LlmChatMessage>
        {
            new(LlmChatRole.System, PromptTemplates.WriterSystem(basePrompt, book, chapter, contextBlock, prevEnding)),
            new(LlmChatRole.User, PromptTemplates.WriterUser(chapter, synopsesBlock, authorInstructions, sectionContext)),
        };

        var content = await StreamResponseAsync(client, config, messages, bookId, chapterId, AgentRole.Writer, ct);
        content = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);

        var version = CreateChapterVersion(chapter, content, ChapterStatus.Review, AgentCreatedBy.Writer);
        var savedVersion = await Repo.AddChapterVersionAsync(version);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, RunStates.Done, chapterId, ct);

        try { await IndexChapterAsync(bookId, chapterId, savedVersion.Id, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Failed to index chapter version for embeddings.", bookId);
        }
    }

    /// <summary>The section's plan and its other chapters, for a chapter of a section with several; empty otherwise.</summary>
    private async Task<string> BuildSectionContextAsync(int bookId, Chapter chapter)
    {
        if (chapter.SectionId is not int sid || await Repo.GetSectionAsync(bookId, sid) is not { } section) return "";
        var sectionChapters = (await Repo.GetChaptersAsync(bookId)).Where(c => c.SectionId == sid).ToList();
        return PromptTemplates.SectionContext(section, sectionChapters, chapter);
    }

    /// <summary>Loads the cards, threads and prior-chapter passages relevant to this chapter and formats them.</summary>
    private async Task<string> BuildWriterContextAsync(
        int bookId, Chapter chapter, StoryBible? bible, LlmConfiguration config, CancellationToken ct)
    {
        var allCards = await Repo.GetCharacterCardsAsync(bookId);
        var involvedNames = ParseNameList(chapter.CharactersInvolvedJson);
        var cards = involvedNames.Count > 0
            ? allCards.Where(c => involvedNames.Contains(c.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            : allCards.Where(c => c.Role == CharacterRole.Protagonist || c.Role == CharacterRole.Antagonist).ToList();

        var allThreads = await Repo.GetPlotThreadsAsync(bookId);
        var activeThreadNames = ParseNameList(chapter.PlotThreadsJson);
        // Threads' "introduced in" counts sections.
        var sectionNumber = chapter.SectionId is int sid && await Repo.GetSectionAsync(bookId, sid) is { } section
            ? section.Number : chapter.Number;
        var threads = activeThreadNames.Count > 0
            ? allThreads.Where(t => activeThreadNames.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList()
            : allThreads.Where(t => t.Status == PlotThreadStatus.Active
                && (t.IntroducedChapterNumber == null || t.IntroducedChapterNumber <= sectionNumber)).ToList();

        var rag = await GetChapterRagAsync(bookId, chapter, config, includeRepeatedPhrases: false, ct);

        return PromptTemplates.WriterContext(bible, cards, threads, rag);
    }
}
