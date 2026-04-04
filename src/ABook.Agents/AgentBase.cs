#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.Ollama;

namespace ABook.Agents;

/// <summary>Base helpers shared across all agents.</summary>
public abstract class AgentBase
{
    protected readonly IBookRepository Repo;
    protected readonly ILlmProviderFactory LlmFactory;
    protected readonly IVectorStoreService VectorStore;
    protected readonly IBookNotifier Notifier;
    protected readonly AgentRunStateService StateService;

    protected AgentBase(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService)
    {
        Repo = repo;
        LlmFactory = llmFactory;
        VectorStore = vectorStore;
        Notifier = notifier;
        StateService = stateService;
    }

    protected async Task<Kernel> GetKernelAsync(int bookId)
    {
        var book = await Repo.GetByIdAsync(bookId);
        var config = await Repo.GetLlmConfigAsync(bookId, book?.UserId)
            ?? throw new InvalidOperationException("No LLM configuration found.");
        return LlmFactory.CreateKernel(config);
    }

    /// <summary>Streams LLM tokens to the book's SignalR group and accumulates the full response.</summary>
    protected async Task<string> StreamResponseAsync(
        Kernel kernel, ChatHistory history, int bookId, int? chapterId, CancellationToken ct)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = new OllamaPromptExecutionSettings { Temperature = (float?)0.8f };
        var sb = new System.Text.StringBuilder();

        await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct))
        {
            if (chunk.Content is { Length: > 0 } token)
            {
                sb.Append(token);
                await Notifier.StreamTokenAsync(bookId, chapterId, token, ct);
            }
        }

        return sb.ToString();
    }

    /// <summary>Persist a question message and notify the UI to pause.</summary>
    protected async Task<AgentMessage> AskUserAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct)
    {
        var msg = await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = role,
            MessageType = MessageType.Question,
            Content = question,
            IsResolved = false
        });

        await Notifier.NotifyQuestionAsync(bookId, msg, ct);

        return msg;
    }

    /// <summary>
    /// Ask the user a question, pause execution, and return their answer.
    /// Sets run state to WaitingForInput until the answer is submitted via the API.
    /// </summary>
    protected async Task<string> AskUserAndWaitAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct)
    {
        var msg = await AskUserAsync(bookId, chapterId, role, question, ct);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled());

        StateService.SetPending(bookId, msg.Id, tcs);
        StateService.SetStatus(bookId, new AgentRunStatus(role, "WaitingForInput", chapterId));
        await Notifier.NotifyStatusChangedAsync(bookId, role, "WaitingForInput", ct);

        var answer = await tcs.Task; // throws OperationCanceledException if stopped

        StateService.SetStatus(bookId, new AgentRunStatus(role, "Running", chapterId));
        await Notifier.NotifyStatusChangedAsync(bookId, role, "Running", ct);

        return answer;
    }

    /// <summary>Retrieve relevant context chunks from Qdrant for RAG. Returns empty on failure.</summary>
    protected async Task<string> GetRagContextAsync(
        int bookId, string query, int topK, ILlmProviderFactory factory, LlmConfiguration config)
    {
        try
        {
            var embedder = factory.CreateEmbeddingGeneration(config);
            var embeddings = await embedder.GenerateEmbeddingsAsync([query]);
            var embedding = embeddings[0];
            var chunks = await VectorStore.SearchAsync(bookId, embedding, topK);
            return string.Join("\n\n---\n\n", chunks.Select(c => $"[Chapter {c.ChapterNumber}]\n{c.Text}"));
        }
        catch (OperationCanceledException) { throw; }
        catch { return string.Empty; }
    }
}
