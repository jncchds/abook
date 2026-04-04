#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class EditorAgent : AgentBase
{
    public EditorAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService)
        : base(repo, llmFactory, vectorStore, notifier, stateService) { }

    public async Task EditAsync(int bookId, int chapterId, CancellationToken ct = default, string? continuityNotes = null)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        chapter.Status = ChapterStatus.Editing;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Running", ct);

        var kernel = await GetKernelAsync(bookId);

        var history = new ChatHistory();
        var systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? book.EditorSystemPrompt
            : $"""
            You are a professional fiction Editor. Your job is to improve prose quality, fix grammar,
            enhance pacing, and strengthen character voice. Preserve the author's style.
            Output the complete improved chapter in markdown, followed by a brief
            "## Editorial Notes" section listing key changes made.
            Book: {book.Title} | Genre: {book.Genre} | Language: {book.Language}
            """;
        history.AddSystemMessage(systemPrompt);

        var editRequest = string.IsNullOrWhiteSpace(continuityNotes)
            ? $"""
            Please edit Chapter {chapter.Number}: {chapter.Title}

            Original content:
            {chapter.Content}
            """
            : $"""
            Please edit Chapter {chapter.Number}: {chapter.Title}

            The continuity checker identified the following issues to fix in this chapter:
            {continuityNotes}

            Original content:
            {chapter.Content}
            """;
        history.AddUserMessage(editRequest);

        var edited = await StreamResponseAsync(kernel, history, bookId, chapterId, ct);

        // Split off the editorial notes (everything after "## Editorial Notes")
        int notesIdx = edited.IndexOf("## Editorial Notes", StringComparison.OrdinalIgnoreCase);
        string prose;
        if (notesIdx > 0)
        {
            var notes = edited[notesIdx..];
            prose = edited[..notesIdx].Trim();
            await Repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = AgentRole.Editor,
                MessageType = MessageType.Feedback,
                Content = notes,
                IsResolved = true
            });
        }
        else
        {
            prose = edited;
        }

        // Strip any leading chapter heading the LLM may have added (e.g. "# Chapter 1: Title")
        chapter.Content = StripLeadingChapterHeading(prose, chapter.Number, chapter.Title);

        chapter.Status = ChapterStatus.Done;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", ct);
    }
}
