using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Handles the upfront clarifying Q&amp;A round that precedes the planning pipeline.
/// Gathers questions from the LLM about the book premise and presents them to the author one at a
/// time; the answers become the book's clarifications, which prompts append to the premise.
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
    /// Saves the questions as open (unanswered) clarifications — the author answers them all on the
    /// Clarifications page — and waits until every one is answered or archived. Returns false when the
    /// author reset the round instead (Archive all).
    /// </summary>
    public async Task<bool> AskQuestionsAsync(int bookId, List<string> questions, CancellationToken ct)
    {
        foreach (var question in questions)
            await Repo.AddClarificationAsync(new Clarification
            {
                BookId = bookId, Source = ClarificationSource.Planner, Question = question.Trim(),
            });
        return await WaitForAnswersAsync(bookId, ct);
    }

    /// <summary>
    /// Pauses the run until no open clarification is left. The answer that closes the last one releases
    /// it (<see cref="AgentRunStateService.TryResolveClarifications"/>); Stop cancels it. Returns false
    /// when the author reset the round instead.
    /// </summary>
    public async Task<bool> WaitForAnswersAsync(int bookId, CancellationToken ct)
    {
        var open = (await Repo.GetClarificationsAsync(bookId)).Where(c => c.IsOpen).ToList();
        if (open.Count == 0) return true;

        // An informational note, not a Question: the answers go to the Clarifications page, not the chat.
        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = null,
            AgentRole = AgentRole.Planner,
            MessageType = MessageType.SystemNote,
            Content = $"The planner has {open.Count} question(s) to clarify the plan — answer them on the Clarifications page. " +
                      $"Planning continues once the last one is answered.\n\n{string.Join("\n", open.Select((c, i) => $"{i + 1}. {c.Question}"))}",
            IsResolved = true
        });
        await Notifier.NotifyMessagesUpdatedAsync(bookId, ct);

        var answered = StateService.WaitForClarificationsAsync(bookId);
        // The last answer may have landed before the waiter existed.
        if (!(await Repo.GetClarificationsAsync(bookId)).Any(c => c.IsOpen))
            StateService.TryResolveClarifications(bookId);

        StateService.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, RunStates.WaitingForInput, null));
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, RunStates.WaitingForInput, null, ct);
        await StateService.PersistRunPausedAsync(bookId, messageId: null);

        var roundComplete = await answered.WaitAsync(ct);

        StateService.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, RunStates.Running, null));
        await StateService.PersistRunResumedAsync(bookId);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Planner, RunStates.Running, null, ct);
        return roundComplete;
    }

    /// <summary>Saves a non-empty phase note as a clarification; blank (skipped) notes are dropped.</summary>
    public async Task SaveClarificationAsync(int bookId, string question, string answer)
    {
        if (string.IsNullOrWhiteSpace(answer)) return;
        await Repo.AddClarificationAsync(new Clarification
        {
            BookId = bookId,
            Source = ClarificationSource.PhaseNote,
            Question = question.Trim(),
            Answer = answer.Trim(),
        });
    }

    /// <summary>
    /// Asks a single optional question and returns the user's response (may be empty if skipped).
    /// Used for human-assisted pauses after planning phases and chapter checks.
    /// </summary>
    public Task<string> AskSingleOptionalAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct) =>
        AskUserAndWaitAsync(bookId, chapterId, role, question, ct, isOptional: true);
}
