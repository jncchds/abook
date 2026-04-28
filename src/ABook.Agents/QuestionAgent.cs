#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel.ChatCompletion;

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
    public async Task<List<string>> GatherQuestionsAsync(int bookId, string bookContext, CancellationToken ct)
    {
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning: Checking if clarification is needed…", false, ct);

        var (kernel, config) = await GetKernelAsync(bookId);
        var history = new ChatHistory();
        history.AddSystemMessage("""
            You are helping plan a book. Given the book premise below, determine if there is anything
            genuinely unclear that an author decision would meaningfully change the story plan.
            If so, list those questions as a numbered list (e.g. "1. Question here?").
            Keep the number of questions to an absolute minimum — only ask when truly necessary.
            If everything is clear from the premise, respond with exactly: None.
            """);
        history.AddUserMessage(bookContext);

        string response;
        try { response = await GetCompletionAsync(kernel, config, history, ct, bookId, null, AgentRole.Planner); }
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
    /// Presents each question to the author one at a time via <see cref="AskUserAndWaitAsync"/>
    /// and appends Q&amp;A pairs to the shared context string builder.
    /// </summary>
    public async Task AskQuestionsAsync(
        int bookId, List<string> questions, StringBuilder qaContext, CancellationToken ct)
    {
        foreach (var question in questions)
        {
            var answer = await AskUserAndWaitAsync(bookId, null, AgentRole.Planner, question, ct);
            if (!string.IsNullOrWhiteSpace(answer))
                qaContext.AppendLine($"Q: {question}\nA: {answer}\n");
        }
    }

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
                sb.AppendLine($"Q: {q.Content}\nA: {a.Content}\n");
                i++;
            }
        }
        return sb;
    }
}
