#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text;
using System.Text.RegularExpressions;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;
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
        AgentRunStateService stateService)
        : base(repo, llmFactory, vectorStore, notifier, stateService) { }

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

        // Retrieve RAG context: semantically relevant passages from prior chapters
        var ragContext = chapter.Number > 1
            ? await GetRagContextAsync(bookId, chapter.Outline, 5, LlmFactory, config!)
            : string.Empty;

        // Retrieve the direct ending of the previous chapter for narrative continuity
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
            Current chapter: {chapter.Number} of {book.TargetChapterCount} — "{chapter.Title}"
            Chapter outline: {chapter.Outline}
            Write all content in {book.Language}.
            IMPORTANT: Do NOT begin your response with any chapter heading, title, or label.
            Start immediately with the narrative prose (a scene, action, dialogue, or description).
            If you reach a point where a significant plot decision, character choice, or narrative
            direction is unclear and the author's input would meaningfully change the outcome,
            output EXACTLY: [ASK: your question here]
            Then stop writing. The author will answer and you will continue from that exact point.
            Use this sparingly — only for genuinely pivotal decisions, not minor details.
            {(ragContext.Length > 0 ? $"\nRelevant context from previous chapters (for consistency):\n{ragContext}" : "")}
            {(prevEnding.Length > 0 ? $"\nThe previous chapter ended with:\n{prevEnding}\nContinue the story naturally from this point." : "")}
            """;
        history.AddSystemMessage(systemPrompt);

        history.AddUserMessage($"""
            Write the full content for:
            Chapter {chapter.Number}: {chapter.Title}
            Outline: {chapter.Outline}

            Write at least 1000 words of narrative prose. Output markdown only. Do NOT include a chapter heading.
            """);

        var content = await WriteWithQuestionsAsync(kernel, history, bookId, chapterId, chapter, ct);

        // Strip any leading chapter heading the LLM may have added (e.g. "# Chapter 1: Title")
        // since the UI displays title separately
        content = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);

        chapter.Content = content;
        chapter.Status = ChapterStatus.Review;
        await Repo.UpdateChapterAsync(chapter);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Writer, "Done", ct);

        // Index chapter in Qdrant for RAG (non-fatal — chapter is already saved)
        try { await IndexChapterAsync(bookId, chapter, kernel, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* Qdrant unavailable — RAG context will be skipped for future chapters */ }
    }

    // Detects [ASK: ...] markers the LLM emits mid-generation to request author input.
    private static readonly Regex AskMarkerRegex = new(
        @"\[ASK:\s*(.*?)\]",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Streams the chapter in a loop, pausing whenever the LLM emits [ASK: question].
    /// The user's answer is fed back as a new turn and generation continues.
    /// </summary>
    private async Task<string> WriteWithQuestionsAsync(
        Kernel kernel, ChatHistory history, int bookId, int chapterId, Chapter chapter, CancellationToken ct)
    {
        var accumulated = new StringBuilder();
        const int maxRounds = 6; // guard against runaway loops

        for (int round = 0; round < maxRounds; round++)
        {
            var raw = await StreamResponseAsync(kernel, history, bookId, chapterId, ct);

            var match = AskMarkerRegex.Match(raw);
            if (!match.Success)
            {
                accumulated.Append(raw);
                break;
            }

            // Content written before the [ASK: ...] marker
            var beforeQuestion = raw[..match.Index].TrimEnd();
            var question = match.Groups[1].Value.Trim();
            accumulated.Append(beforeQuestion);
            if (beforeQuestion.Length > 0) accumulated.Append("\n\n");

            // Pause and collect the author's answer
            var answer = await AskUserAndWaitAsync(bookId, chapterId, AgentRole.Writer, question, ct);

            // Feed partial prose + answer back into the conversation so the LLM can continue
            history.AddAssistantMessage(beforeQuestion.Length > 0
                ? beforeQuestion
                : "[Writer paused to ask a question before writing this section]");
            history.AddUserMessage(string.IsNullOrWhiteSpace(answer)
                ? "Please continue writing the chapter."
                : $"Author's answer: {answer}\n\nPlease continue writing the chapter from where you left off, incorporating this answer.");
        }

        return accumulated.ToString();
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
    }
}
