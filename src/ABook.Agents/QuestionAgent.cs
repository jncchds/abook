using System.Text;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Handles the upfront clarifying Q&amp;A round that precedes the planning pipeline.
/// Gathers questions from the LLM about the book premise, presents them to the author
/// one at a time, and can rebuild prior Q&amp;A context for continuation runs.
/// </summary>
public class QuestionAgent : AgentBase
{
    public QuestionAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Makes a single non-streaming LLM call to gather clarifying questions about the book premise.
    /// Returns an empty list if the LLM determines nothing is unclear.
    /// </summary>
    public async Task<List<string>> GatherQuestionsAsync(int bookId, string bookContext, string language, CancellationToken ct)
    {
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Checking if clarification is needed…", false, ct);

        var (client, config) = await GetChatClientAsync(bookId);
        var messages = new List<LlmChatMessage>();
        messages.Add(new LlmChatMessage(LlmChatRole.System, PromptTemplates.QuestionGatheringSystem(language)));
        messages.Add(new LlmChatMessage(LlmChatRole.User, bookContext));

        string response;
        try
        {
            // Not book content — keep it out of the UI stream (it would land in the chapter-outline preview).
            response = await StreamResponseAsync(client, config, messages, bookId, (int?)null, AgentRole.Planner, ct,
                forwardToUi: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Initial question gathering failed — continuing without questions.", bookId);
            return [];
        }

        if (response.Trim().StartsWith("None", StringComparison.OrdinalIgnoreCase))
            return [];

        var questions = new List<string>();
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            var match = System.Text.RegularExpressions.Regex.Match(trimmed, @"^\d+[.)]\s+(.+)$");
            if (match.Success)
                questions.Add(match.Groups[1].Value.Trim());
        }
        return questions;
    }

    /// <summary>
    /// Posts a single preview message listing all upcoming questions, then presents each
    /// one to the author individually via <see cref="AskUserAndWaitAsync"/> and appends Q&amp;A pairs
    /// to the shared context string builder.
    /// </summary>
    public async Task AskQuestionsAsync(
        int bookId, List<string> questions, StringBuilder qaContext, CancellationToken ct)
    {
        // An informational note, not a Question: nothing answers it, so as a Question it would stay
        // "open" in the chat forever and could be offered as the pending question.
        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = null,
            AgentRole = AgentRole.Planner,
            MessageType = MessageType.SystemNote,
            Content = $"The planner has {questions.Count} question(s) to clarify the plan:\n\n{string.Join("\n", questions.Select((q, i) => ($"{i + 1}. {q}")))}",
            IsResolved = true
        });
        await Notifier.NotifyMessagesUpdatedAsync(bookId, ct);

        foreach (var question in questions)
        {
            var answer = await AskUserAndWaitAsync(bookId, null, AgentRole.Planner, question, ct);
            if (!string.IsNullOrWhiteSpace(answer))
                qaContext.AppendLine(PromptTemplates.QuestionAndAnswer(question, answer));
        }
    }

    /// <summary>
    /// Asks a single optional question and returns the user's response (may be empty if skipped).
    /// Used for human-assisted pauses after planning phases and chapter checks.
    /// </summary>
    public Task<string> AskSingleOptionalAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct) =>
        AskUserAndWaitAsync(bookId, chapterId, role, question, ct, isOptional: true);

    /// <summary>
    /// Reconstructs the Q&amp;A context from persisted Planner messages.
    /// Used on continuation runs so prior author answers flow into subsequent phases.
    /// </summary>
    public async Task<StringBuilder> LoadExistingContextAsync(int bookId)
    {
        var messages = (await Repo.GetMessagesAsync(bookId))
            .Where(m => m.AgentRole == AgentRole.Planner
                     && (m.MessageType == MessageType.Question || m.MessageType == MessageType.Answer))
            .OrderBy(m => m.CreatedAt)
            .ToList();

        var sb = new StringBuilder();
        for (int i = 0; i < messages.Count - 1; i++)
        {
            var q = messages[i];
            var a = messages[i + 1];
            if (q.MessageType == MessageType.Question && a.MessageType == MessageType.Answer)
            {
                sb.AppendLine(PromptTemplates.QuestionAndAnswer(q.Content, a.Content));
                i++;
            }
        }
        return sb;
    }
}
