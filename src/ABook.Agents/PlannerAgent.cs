#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class PlannerAgent : AgentBase
{
    public PlannerAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Generates chapter outlines for the book.
    /// Returns the list of chapters created.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> PlanAsync(int bookId, CancellationToken ct = default)
    {
        var book = await Repo.GetByIdWithDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Running", ct);

        // Clear existing chapters before re-planning so there are no duplicates
        await Repo.DeleteChaptersAsync(bookId);

        var kernel = await GetKernelAsync(bookId);

        var history = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.PlannerSystemPrompt)
            ? InterpolateSystemPrompt(book.PlannerSystemPrompt, book)
            : $"""
            You are a creative writing Planner. Your task is to outline a book in detail.
            For each chapter, output a JSON array of objects with fields:
              "number" (int), "title" (string), "outline" (string — a thorough description of the chapter: key scenes, character actions, emotional beats, foreshadowing, and how the chapter advances the plot. Write as much detail as you need to fully convey your vision for the chapter).
            Output ONLY the JSON array, no additional text.
            Write all content in {book.Language}.
            """;
        history.AddSystemMessage(systemPrompt);

        history.AddUserMessage($"""
            Book title: {book.Title}
            Genre: {book.Genre}
            Premise: {book.Premise}
            Target chapter count: {book.TargetChapterCount}

            Create {book.TargetChapterCount} chapter outlines for this book.
            """);

        var raw = await StreamResponseAsync(kernel, history, bookId, null, AgentRole.Planner, ct);

        // Parse the JSON response — log the raw output if it cannot be parsed
        List<Chapter> chapters;
        try
        {
            chapters = ParseChapterOutlines(bookId, raw);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex,
                "[Book {BookId}] Planner produced a response that could not be parsed as chapter outlines.\nRaw LLM response ({Chars} chars):\n{Raw}",
                bookId, raw.Length, raw);
            await Notifier.NotifyAgentErrorAsync(bookId, AgentRole.Planner.ToString(),
                "Planner error: the LLM response was not valid JSON chapter outlines. " +
                "Try again — if the problem persists, simplify your premise or check the LLM configuration.", ct);
            throw;
        }

        if (chapters.Count == 0)
        {
            Logger.LogWarning(
                "[Book {BookId}] Planner parsed zero chapters from LLM response ({Chars} chars):\n{Raw}",
                bookId, raw.Length, raw);
        }

        foreach (var chapter in chapters)
            await Repo.AddChapterAsync(chapter);

        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, "Done", ct);

        return chapters;
    }

    private static List<Chapter> ParseChapterOutlines(int bookId, string json)
    {
        // Strip markdown code fences if present
        json = json.Trim();
        if (json.StartsWith("```")) json = string.Join('\n', json.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var chapters = new List<Chapter>();

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            chapters.Add(new Chapter
            {
                BookId = bookId,
                Number = element.GetProperty("number").GetInt32(),
                Title = element.GetProperty("title").GetString() ?? "",
                Outline = element.GetProperty("outline").GetString() ?? "",
                Status = ChapterStatus.Outlined
            });
        }

        return chapters;
    }
}
