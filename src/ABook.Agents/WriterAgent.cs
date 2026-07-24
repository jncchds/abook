using System.Text;
using System.Text.Json;
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

    public async Task WriteAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        chapter.Status = ChapterStatus.Writing;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Running", chapterId, ct);

        var (client, config) = await GetChatClientAsync(bookId);

        var contextBlock = await BuildWriterContextAsync(bookId, chapter, config!, ct);
        var synopsesBlock = await BuildChapterSynopsesAsync(bookId, chapter.Number, ct);
        var prevEnding = await GetPreviousChapterEndingAsync(bookId, chapter.Number, paragraphCount: 3);

        var messages = new List<LlmChatMessage>();
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var basePrompt = !string.IsNullOrWhiteSpace(book.WriterSystemPrompt)
            ? InterpolateSystemPrompt(book.WriterSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Writer, book, bible);
        var systemPrompt = $"""
            {basePrompt}
            Current chapter: {chapter.Number} of {book.TargetChapterCount} — "{chapter.Title}"
            {contextBlock}
            {(prevEnding.Length > 0 ? $"\nThe previous chapter ended with:\n{prevEnding}\nContinue the story naturally from this point." : "")}
            """;
        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));

        messages.Add(new LlmChatMessage(LlmChatRole.User, $"""
            {(synopsesBlock.Length > 0 ? $"## Story So Far — Chapter Synopses\nRefer to this to avoid re-introducing or restating anything already established in prior chapters.\n{synopsesBlock}\n\n" : "")}Write the full content for:
            Chapter {chapter.Number}: {chapter.Title}
            Outline: {chapter.Outline}
            {(chapter.PovCharacter.Length > 0 ? $"Point of view: {chapter.PovCharacter}" : "")}
            {(chapter.ForeshadowingNotes.Length > 0 ? $"Plant in this chapter (foreshadowing): {chapter.ForeshadowingNotes}" : "")}
            {(chapter.PayoffNotes.Length > 0 ? $"Pay off in this chapter: {chapter.PayoffNotes}" : "")}

            Write at least 1000 words of narrative prose. Output markdown only. Do NOT include a chapter heading.
            """));

        var content = await StreamResponseAsync(client, config, messages, bookId, chapterId, AgentRole.Writer, ct);
        content = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);

        var version = new ChapterVersion
        {
            ChapterId = chapterId,
            BookId = bookId,
            Title = chapter.Title,
            Outline = chapter.Outline,
            Content = content,
            Status = ChapterStatus.Review,
            PovCharacter = chapter.PovCharacter,
            CharactersInvolvedJson = chapter.CharactersInvolvedJson,
            PlotThreadsJson = chapter.PlotThreadsJson,
            ForeshadowingNotes = chapter.ForeshadowingNotes,
            PayoffNotes = chapter.PayoffNotes,
            CreatedBy = "agent:Writer",
            HasEmbeddings = false,
        };
        var savedVersion = await Repo.AddChapterVersionAsync(version);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Done", chapterId, ct);

        try { await IndexChapterAsync(bookId, chapterId, savedVersion.Id, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Failed to index chapter version for embeddings.", bookId);
        }
    }

    private async Task<string> BuildWriterContextAsync(
        int bookId, Chapter chapter, LlmConfiguration config, CancellationToken ct)
    {
        var sb = new StringBuilder();

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
                sb.AppendLine("\n## Character Profiles (canonical — do not contradict)");
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

        if (chapter.Number > 1)
        {
            List<string> charNames;
            try { charNames = JsonSerializer.Deserialize<List<string>>(chapter.CharactersInvolvedJson) ?? []; }
            catch { charNames = []; }
            var charQuery = charNames.Count > 0
                ? $"character appearance description personality {string.Join(' ', charNames)}"
                : "character appearance description personality backstory";

            var locationQuery = $"location place setting description {chapter.Outline}";

            List<string> threadNames;
            try { threadNames = JsonSerializer.Deserialize<List<string>>(chapter.PlotThreadsJson) ?? []; }
            catch { threadNames = []; }
            var threadQuery = threadNames.Count > 0
                ? $"plot thread events foreshadowing {string.Join(' ', threadNames)}"
                : $"plot events sequence foreshadowing {chapter.Outline}";

            var charRag     = await GetRagContextAsync(bookId, charQuery,     4, LlmFactory, config, chapter.Id, ct);
            var locationRag = await GetRagContextAsync(bookId, locationQuery, 3, LlmFactory, config, chapter.Id, ct);
            var threadRag   = await GetRagContextAsync(bookId, threadQuery,   3, LlmFactory, config, chapter.Id, ct);

            if (!string.IsNullOrWhiteSpace(charRag) || !string.IsNullOrWhiteSpace(locationRag) || !string.IsNullOrWhiteSpace(threadRag))
            {
                sb.AppendLine("\n## Prior Chapter Passages (for consistency — do not re-introduce or restate)");
                if (!string.IsNullOrWhiteSpace(charRag))
                {
                    sb.AppendLine("\n### Character Details from Prior Chapters");
                    sb.AppendLine(charRag);
                }
                if (!string.IsNullOrWhiteSpace(locationRag))
                {
                    sb.AppendLine("\n### Location & Setting Details from Prior Chapters");
                    sb.AppendLine(locationRag);
                }
                if (!string.IsNullOrWhiteSpace(threadRag))
                {
                    sb.AppendLine("\n### Plot Thread Context from Prior Chapters");
                    sb.AppendLine(threadRag);
                }
            }
        }

        return sb.ToString();
    }
}
