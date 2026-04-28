#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

/// <summary>Planning Phase 1: generates and persists the Story Bible.</summary>
public class StoryBibleAgent : AgentBase
{
    public StoryBibleAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Generates the Story Bible for the book, persists it, marks the phase Complete,
    /// and returns the saved <see cref="StoryBible"/>.
    /// </summary>
    public async Task<StoryBible> RunAsync(Book book, string qaContext, CancellationToken ct = default)
    {
        var bookId = book.Id;
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Phase 1/4 - Story Bible...", false, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.StoryBibleAgent, "Running", ct);

        var (kernel, config) = await GetKernelAsync(bookId);
        var history = new ChatHistory();

        var systemPrompt = !string.IsNullOrWhiteSpace(book.StoryBibleSystemPrompt)
            ? InterpolateSystemPrompt(book.StoryBibleSystemPrompt, book)
            : InterpolateSystemPrompt(DefaultPrompts.StoryBible, book);

        history.AddSystemMessage(systemPrompt);
        history.AddUserMessage($"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}
            {(qaContext.Length > 0 ? $"\nAuthor guidance:\n{qaContext}" : "")}

            Create the Story Bible for this book.
            """);

        var raw = await StreamResponseAsync(kernel, config, history, bookId, null, AgentRole.StoryBibleAgent, ct, jsonMode: true);

        StoryBible bible;
        try { bible = Parse(bookId, raw); }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] StoryBibleAgent: could not parse Story Bible JSON.", bookId);
            await ReportErrorAsync(bookId, null, AgentRole.StoryBibleAgent,
                "Story Bible JSON could not be parsed. Try again or simplify the premise.", ct);
            throw;
        }

        await Repo.UpsertStoryBibleAsync(bible);
        book.StoryBibleStatus = PlanningPhaseStatus.Complete;
        await Repo.UpdateAsync(book);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.StoryBibleAgent, "Done", ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Story Bible saved. (complete)", false, ct);
        return bible;
    }

    private static StoryBible Parse(int bookId, string raw)
    {
        var json = ExtractJson(raw, '{', '}');
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var r = doc.RootElement;
        string Get(string key) => r.TryGetProperty(key, out var v) ? v.GetString() ?? "" : "";
        return new StoryBible
        {
            BookId = bookId,
            SettingDescription = Get("settingDescription"),
            TimePeriod = Get("timePeriod"),
            Themes = Get("themes"),
            ToneAndStyle = Get("toneAndStyle"),
            WorldRules = Get("worldRules"),
            Notes = Get("notes")
        };
    }
}
