using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

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
    /// Any threads already saved are fed back to the model so a re-run refines and extends them
    /// instead of starting over. If the call fails part-way through streaming, the threads that
    /// finished streaming are merged into the existing set before the failure is rethrown.
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
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.PlotThreadsAgent, "Running", ct: ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var messages = new List<LlmChatMessage>();
        var ancestorReference = await Repo.BuildAncestorPlanningReferenceAsync(bookId, ct);

        // Archived threads are excluded — they must never reach the model or be overwritten.
        var existing = (await Repo.GetPlotThreadsAsync(bookId)).ToList();

        var characterSummary = string.Join("\n", characters.Select(c =>
            $"- {c.Name} ({c.Role}): {c.GoalMotivation}. Arc: {c.Arc}"));

        var systemPrompt = !string.IsNullOrWhiteSpace(book.PlotThreadsSystemPrompt)
            ? InterpolateSystemPrompt(book.PlotThreadsSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.PlotThreads, book, bible);

        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));
        messages.Add(new LlmChatMessage(LlmChatRole.User, $"""
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
            {(ancestorReference.Length > 0 ? $"\n{ancestorReference}" : "")}
            {BuildExistingBlock(existing)}
            Create the plot thread map for this book.
            """));

        var (raw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.PlotThreadsAgent, JsonSchemas.PlotThreads, ct);

        List<PlotThread> threads = [];
        List<string> skipped = [];
        Exception? parseError = null;
        try { threads = Parse(bookId, raw, skipped); }
        catch (Exception ex)
        {
            parseError = ex;
            Logger.LogError(ex, "[Book {BookId}] PlotThreadsAgent: could not parse Plot Threads JSON.", bookId);
        }

        // A stream can also end cleanly on a truncated array — typically the model hitting MaxTokens.
        if (failure is null && parseError is null && !PartialJson.IsComplete(raw))
        {
            parseError = new FormatException(
                "The response was cut off before the thread list was closed (the model likely hit its token limit).");
            Logger.LogWarning("[Book {BookId}] PlotThreadsAgent: response truncated — salvaged {Count} thread(s).",
                bookId, threads.Count);
        }

        if (failure is not null || parseError is not null)
        {
            await KeepPartialAsync(bookId, threads, parseError);
            Rethrow(failure ?? parseError!);
        }

        if (skipped.Count > 0)
        {
            Logger.LogWarning("[Book {BookId}] PlotThreadsAgent: dropped {Count} unusable element(s): {Detail}",
                bookId, skipped.Count, string.Join(" | ", skipped));
            await ReportNoteAsync(bookId, null, AgentRole.PlotThreadsAgent,
                PlanningParse.KeptSummary(skipped, threads.Count, "plot thread(s)"), ct);
        }

        // Snapshot existing plot threads before deleting so they are preserved in history
        if (existing.Count > 0)
        {
            await Repo.AddPlotThreadsSnapshotAsync(new ABook.Core.Models.PlotThreadsSnapshot
            {
                BookId = bookId,
                DataJson = System.Text.Json.JsonSerializer.Serialize(existing),
                Reason = "agent-overwrite",
                Source = "agent-overwrite"
            });
        }

        await Repo.DeletePlotThreadsAsync(bookId);
        var savedThreads = await Repo.AddPlotThreadsBatchAsync(threads);
        await SaveVersionsAsync(bookId, savedThreads);

        // Save the newly generated plot threads as a snapshot so they appear in history immediately
        await Repo.AddPlotThreadsSnapshotAsync(new ABook.Core.Models.PlotThreadsSnapshot
        {
            BookId = bookId,
            DataJson = System.Text.Json.JsonSerializer.Serialize(threads),
            Reason = "agent-generated",
            Source = "agent-generated"
        });

        book.PlotThreadsStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.PlotThreadsAgent, "Done", ct: ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: {threads.Count} plot thread{(threads.Count == 1 ? "" : "s")} saved. (complete)", false, ct);
        return threads;
    }

    /// <summary>
    /// Renders the threads already saved for this book so a re-run builds on them.
    /// Empty when the book has none.
    /// </summary>
    private static string BuildExistingBlock(IReadOnlyList<PlotThread> existing)
    {
        if (existing.Count == 0) return string.Empty;

        var json = SerializeForPrompt(existing.Select(t => new
        {
            name = t.Name,
            description = t.Description,
            type = t.Type.ToString(),
            introducedChapterNumber = t.IntroducedChapterNumber,
            resolvedChapterNumber = t.ResolvedChapterNumber,
            status = t.Status.ToString(),
        }));

        return $"""

            These plot threads are already saved for this book:
            {json}

            Return the complete, updated map: repeat every thread you are keeping (unchanged or revised,
            keeping its exact "name" so it can be matched), and add any thread the story still needs.
            Threads you leave out will be removed.

            """;
    }

    /// <summary>
    /// Keeps the threads that finished streaming before the run failed. They are merged into the
    /// existing map — never replacing it — so nothing the author has already seen is lost, and the
    /// phase is deliberately left incomplete so the next run continues from here.
    /// </summary>
    private async Task KeepPartialAsync(int bookId, IReadOnlyList<PlotThread> salvaged, Exception? parseError)
    {
        if (salvaged.Count == 0)
        {
            await ReportErrorAsync(bookId, null, AgentRole.PlotThreadsAgent,
                parseError is not null
                    ? $"No plot threads could be saved: {parseError.Message} Try again."
                    : "The Plot Threads run failed before any complete thread arrived. Nothing was saved — try again.");
            return;
        }

        var saved = await Repo.MergePlotThreadsAsync(bookId, salvaged);
        await SaveVersionsAsync(bookId, saved);
        await Repo.AddPlotThreadsSnapshotAsync(new ABook.Core.Models.PlotThreadsSnapshot
        {
            BookId = bookId,
            DataJson = System.Text.Json.JsonSerializer.Serialize(saved),
            Reason = "agent-partial",
            Source = "agent-partial"
        });

        Logger.LogWarning("[Book {BookId}] PlotThreadsAgent: kept {Count} thread(s) salvaged from the interrupted run.",
            bookId, saved.Count);
        await ReportErrorAsync(bookId, null, AgentRole.PlotThreadsAgent,
            $"The Plot Threads run was interrupted. Kept the {saved.Count} thread{(saved.Count == 1 ? "" : "s")} " +
            "received before the failure — run it again to complete the map." +
            (parseError is not null ? $" Reason: {parseError.Message}" : ""));
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Planning: kept {saved.Count} plot thread{(saved.Count == 1 ? "" : "s")} from the interrupted run.",
            false, CancellationToken.None);
    }

    private Task SaveVersionsAsync(int bookId, IReadOnlyList<PlotThread> threads) =>
        Repo.AddPlotThreadVersionsBatchAsync(threads.Select(thread => new ABook.Core.Models.PlotThreadVersion
        {
            PlotThreadId = thread.Id,
            BookId = bookId,
            Name = thread.Name,
            Description = thread.Description,
            Type = thread.Type,
            IntroducedChapterNumber = thread.IntroducedChapterNumber,
            ResolvedChapterNumber = thread.ResolvedChapterNumber,
            Status = thread.Status,
            CreatedBy = AgentCreatedBy.PlotThreads,
        }));

    /// <param name="skipped">Receives one line per rejected element, naming the reason and the offending JSON.</param>
    internal static List<PlotThread> Parse(int bookId, string raw, List<string>? skipped = null)
    {
        // Salvaging keeps every element that finished streaming and drops only a truncated tail,
        // so a response cut short still yields the threads the author already saw.
        using var doc = PlanningParse.OpenArray(raw, "Plot threads");
        var threads = new List<PlotThread>();
        var skips = skipped ?? [];
        var index = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            index++;
            // One malformed entry must not cost the author the whole map — drop it and say why.
            try
            {
                string Get(string k) => LenientJson.Text(el, k);
                var name = Get("name");
                if (string.IsNullOrWhiteSpace(name))   // unusable without a merge key
                {
                    skips.Add(PlanningParse.SkipNote("Plot thread", index, el, "no \"name\" field"));
                    continue;
                }
                var type = Enum.TryParse<PlotThreadType>(Get("type"), true, out var t) ? t : PlotThreadType.Subplot;
                var status = Enum.TryParse<PlotThreadStatus>(Get("status"), true, out var s) ? s : PlotThreadStatus.Active;
                threads.Add(new PlotThread
                {
                    BookId = bookId,
                    Name = name,
                    Description = Get("description"),
                    Type = type,
                    IntroducedChapterNumber = LenientJson.Int(el, "introducedChapterNumber"),
                    ResolvedChapterNumber = LenientJson.Int(el, "resolvedChapterNumber"),
                    Status = status
                });
            }
            catch (Exception ex)
            {
                skips.Add(PlanningParse.SkipNote("Plot thread", index, el, ex));
            }
        }
        // An empty result must not be allowed to wipe the book's existing threads.
        if (threads.Count == 0)
            throw new FormatException(
                $"Plot threads response contained no usable thread. {PlanningParse.SkipSummary(skips, index, raw)}");
        return threads;
    }
}
