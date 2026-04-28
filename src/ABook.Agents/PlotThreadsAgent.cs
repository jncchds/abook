#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

/// <summary>Planning Phase 3: generates and persists Plot Threads.</summary>
public class PlotThreadsAgent : AgentBase
{
    public PlotThreadsAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Generates the plot thread map for the book, persists it, marks the phase Complete,
    /// and returns the saved plot threads.
    /// </summary>
    public async Task<IReadOnlyList<PlotThread>> RunAsync(
        Book book,
        StoryBible bible,
        IReadOnlyList<CharacterCard> characters,
        string qaContext,
        CancellationToken ct = default)
    {
        var bookId = book.Id;
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 3/4 - Plot Threads...", false, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.PlotThreadsAgent, "Running", ct);

        var (kernel, config) = await GetKernelAsync(bookId);
        var history = new ChatHistory();

        var characterSummary = string.Join("\n", characters.Select(c =>
            $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));

        var systemPrompt = !string.IsNullOrWhiteSpace(book.PlotThreadsSystemPrompt)
            ? InterpolateSystemPrompt(book.PlotThreadsSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.PlotThreads, book, bible);

        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage($"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}

            Story Bible:
            Setting: {bible.SettingDescription}
            Time period: {bible.TimePeriod}
            Themes: {bible.Themes}
            World rules: {bible.WorldRules}

            Characters:
            {characterSummary}
            {(qaContext.Length > 0 ? $"\nAuthor guidance so far:\n{qaContext}" : "")}

            Create the plot thread map for this book.
            """);

        var raw = await StreamResponseAsync(kernel, config, history, bookId, null, AgentRole.PlotThreadsAgent, ct, jsonMode: true);

        List<PlotThread> threads;
        try { threads = Parse(bookId, raw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] PlotThreadsAgent: could not parse Plot Threads JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.PlotThreadsAgent,
                "Plot Threads JSON could not be parsed. Try again.", ct);
            throw;
        }

        await Repo.DeletePlotThreadsAsync(bookId);
        foreach (var thread in threads)
            await Repo.AddPlotThreadAsync(thread);
        book.PlotThreadsStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.PlotThreadsAgent, "Done", ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {threads.Count} plot thread{(threads.Count == 1 ? "" : "s")} saved. (complete)", false, ct);
        return threads;
    }

    private static List<PlotThread> Parse(int bookId, string raw)
    {
        var json = ExtractJson(raw, '[', ']');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var threads = new List<PlotThread>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            string Get(string k) => el.TryGetProperty(k, out var v) ? v.GetString() ?? "" : "";
            var type = Enum.TryParse<PlotThreadType>(Get("type"), true, out var t) ? t : PlotThreadType.Subplot;
            var status = Enum.TryParse<PlotThreadStatus>(Get("status"), true, out var s) ? s : PlotThreadStatus.Active;
            int? introduced = el.TryGetProperty("introducedChapterNumber", out var iv)
                && iv.ValueKind == System.Text.Json.JsonValueKind.Number ? iv.GetInt32() : null;
            int? resolved = el.TryGetProperty("resolvedChapterNumber", out var rv)
                && rv.ValueKind == System.Text.Json.JsonValueKind.Number ? rv.GetInt32() : null;
            threads.Add(new PlotThread
            {
                BookId = bookId,
                Name = Get("name"),
                Description = Get("description"),
                Type = type,
                IntroducedChapterNumber = introduced,
                ResolvedChapterNumber = resolved,
                Status = status
            });
        }
        return threads;
    }
}
