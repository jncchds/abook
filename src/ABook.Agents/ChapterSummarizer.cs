using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>A named character the summarizer found in a chapter but not among the book's characters.</summary>
public sealed record SummaryCharacter(string Name, CharacterRole Role, string Description);

/// <summary>A known plot thread whose state a chapter changed.</summary>
public sealed record SummaryThreadChange(string Name, PlotThreadStatus Status, string Note);

/// <summary>What the summarizer recorded about one chapter.</summary>
public sealed record ChapterSummaryResult(
    string Summary, IReadOnlyList<SummaryCharacter> NewCharacters, IReadOnlyList<SummaryThreadChange> ThreadChanges);

/// <summary>
/// Records what a written chapter actually tells — a short account plus the facts it establishes — on
/// <see cref="Chapter.Summary"/>, so prompts describe the book as written rather than as planned. It also
/// reports characters and plot-thread changes the chapter introduced, for the book chat to offer as updates.
/// </summary>
public class ChapterSummarizer : AgentBase
{
    public ChapterSummarizer(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>Summarizes the chapter's active version and saves it. Null when the chapter has no prose.</summary>
    public async Task<ChapterSummaryResult?> SummarizeAsync(int bookId, int chapterId, CancellationToken ct)
    {
        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = EnsureNotArchived(await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));
        if (string.IsNullOrWhiteSpace(chapter.Content)) return null;

        var versionId = (await Repo.GetActiveChapterVersionIdsAsync(bookId)).GetValueOrDefault(chapterId);
        var characters = (await Repo.GetCharacterCardsAsync(bookId)).Select(c => c.Name).ToList();
        var threads = (await Repo.GetPlotThreadsAsync(bookId)).Select(t => t.Name).ToList();

        var (client, config) = await GetChatClientAsync(bookId);
        var messages = new List<LlmChatMessage>
        {
            new(LlmChatRole.System, PromptTemplates.ChapterSummarySystem(book)),
            new(LlmChatRole.User, PromptTemplates.ChapterSummaryUser(chapter, characters, threads)),
        };
        var raw = await StreamResponseAsync(client, config, messages, bookId, chapterId, AgentRole.ChapterSummarizer, ct,
            suspiciousThreshold: 0, jsonSchema: JsonSchemas.ChapterSummary, forwardToUi: false);

        var result = Parse(raw, characters, threads);
        // Re-read: the chapter row may have been saved by someone else while the model was writing.
        var fresh = await Repo.GetChapterAsync(bookId, chapterId) ?? chapter;
        fresh.Summary = result.Summary;
        fresh.SummaryVersionId = versionId == 0 ? null : versionId;
        await Repo.UpdateChapterAsync(fresh);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        return result;
    }

    /// <summary>
    /// Summarizes up to <paramref name="max"/> written chapters whose summary is missing or was written from
    /// another version (the prose changed since). A failure is logged, never thrown: a stale summary is only
    /// less precise. Returns the number refreshed.
    /// </summary>
    public async Task<int> RefreshStaleAsync(int bookId, int max, CancellationToken ct)
    {
        var active = await Repo.GetActiveChapterVersionIdsAsync(bookId);
        var stale = (await Repo.GetChaptersAsync(bookId))
            .Where(c => !string.IsNullOrWhiteSpace(c.Content)
                        && (string.IsNullOrWhiteSpace(c.Summary) || c.SummaryVersionId != active.GetValueOrDefault(c.Id)))
            .OrderBy(c => c.Number)
            .Take(max)
            .ToList();
        var done = 0;
        foreach (var chapter in stale)
        {
            try { if (await SummarizeAsync(bookId, chapter.Id, ct) is not null) done++; }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Summarizing chapter {Number} failed.", bookId, chapter.Number); }
        }
        return done;
    }

    /// <summary>
    /// Reads the summarizer's JSON. Characters already known and threads that are not are dropped — the model
    /// is only asked about the others, but small models do not always listen.
    /// </summary>
    internal static ChapterSummaryResult Parse(string raw, IReadOnlyCollection<string> knownCharacters, IReadOnlyCollection<string> knownThreads)
    {
        using var doc = PlanningParse.OpenObject(raw, "Chapter summary");
        var root = doc.RootElement;
        var summary = LenientJson.Text(root, "summary").Trim();
        if (summary.Length == 0)
            throw new FormatException($"Chapter summary response had no summary. {PlanningParse.Describe(raw)}");

        var facts = Items(root, "facts").Select(LenientJson.Text).Where(f => f.Trim().Length > 0).ToList();

        var cast = knownCharacters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var newCharacters = Items(root, "newCharacters")
            .Select(e => new SummaryCharacter(
                LenientJson.Text(e, "name").Trim(),
                Enum.TryParse<CharacterRole>(LenientJson.Text(e, "role"), true, out var r) ? r : CharacterRole.Minor,
                LenientJson.Text(e, "description").Trim()))
            .Where(c => c.Name.Length > 0 && !cast.Contains(c.Name))
            .DistinctBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var threads = knownThreads.ToDictionary(t => t, t => t, StringComparer.OrdinalIgnoreCase);
        var threadChanges = Items(root, "threadChanges")
            .Select(e => (Name: LenientJson.Text(e, "name").Trim(), Status: LenientJson.Text(e, "status"), Note: LenientJson.Text(e, "note").Trim()))
            .Where(t => threads.ContainsKey(t.Name) && Enum.TryParse<PlotThreadStatus>(t.Status, true, out _))
            .Select(t => new SummaryThreadChange(threads[t.Name], Enum.Parse<PlotThreadStatus>(t.Status, true), t.Note))
            .DistinctBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new ChapterSummaryResult(PromptTemplates.ChapterSummaryText(summary, facts), newCharacters, threadChanges);
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().ToList()
            : [];
}
