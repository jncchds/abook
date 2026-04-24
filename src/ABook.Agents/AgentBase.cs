#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
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
    protected readonly ILogger Logger;

    // Set LLM_DEBUG_LOGGING=true to print full prompts and responses to the console.
    private static readonly bool DebugLoggingEnabled =
        string.Equals(Environment.GetEnvironmentVariable("LLM_DEBUG_LOGGING"), "true", StringComparison.OrdinalIgnoreCase);

    protected AgentBase(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
    {
        Repo = repo;
        LlmFactory = llmFactory;
        VectorStore = vectorStore;
        Notifier = notifier;
        StateService = stateService;
        Logger = loggerFactory.CreateLogger(GetType().FullName ?? GetType().Name);
    }

    protected async Task<Kernel> GetKernelAsync(int bookId)
    {
        var book = await Repo.GetByIdAsync(bookId);
        var config = await Repo.GetLlmConfigAsync(bookId, book?.UserId)
            ?? throw new InvalidOperationException("No LLM configuration found.");
        return LlmFactory.CreateKernel(config);
    }

    /// <summary>Streams LLM tokens to the book's SignalR group and accumulates the full response.</summary>
    /// <param name="suspiciousThreshold">Minimum response length before a 'suspiciously short' warning is emitted. Pass 0 to suppress.</param>
    protected async Task<string> StreamResponseAsync(
        Kernel kernel, ChatHistory history, int bookId, int? chapterId, AgentRole role, CancellationToken ct, int suspiciousThreshold = 50)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = new OllamaPromptExecutionSettings { Temperature = (float?)0.8f };
        var sb = new System.Text.StringBuilder();

        if (DebugLoggingEnabled)
        {
            var req = new System.Text.StringBuilder();
            req.AppendLine($"=== LLM Request [{role}] Book={bookId} Chapter={chapterId} ===");
            foreach (var msg in history)
                req.AppendLine($"[{msg.Role}] {msg.Content}");
            req.AppendLine("=== End Request ===");
            Logger.LogInformation("{LlmRequest}", req.ToString());
        }

        try
        {
            await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct))
            {
                if (chunk.Content is { Length: > 0 } token)
                {
                    sb.Append(token);
                    await Notifier.StreamTokenAsync(bookId, chapterId, token, ct);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] [{Role}] LLM streaming call failed after receiving {Chars} chars. Partial response:\n{Partial}",
                bookId, role, sb.Length, sb.Length > 0 ? sb.ToString() : "(empty)");
            throw;
        }

        var result = sb.ToString();
        if (DebugLoggingEnabled)
            Logger.LogInformation("=== LLM Response [{Role}] Book={BookId} Chapter={ChapterId} ===\n{Response}\n=== End Response ===",
                role, bookId, chapterId, result);

        if (result.Trim().Length == 0)
        {
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM returned an empty response.", bookId, role);
        }
        else if (suspiciousThreshold > 0 && result.Length < suspiciousThreshold)
        {
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM returned a suspiciously short response ({Chars} chars): {Response}",
                bookId, role, result.Length, result.Trim());
        }

        int promptTokens = history.Sum(m => (m.Content?.Length ?? 0)) / 4;
        int completionTokens = result.Length / 4;
        try { await Notifier.NotifyTokenStatsAsync(bookId, chapterId, role.ToString(), promptTokens, completionTokens, ct); }
        catch { /* non-fatal */ }
        try
        {
            await Repo.AddTokenUsageAsync(new TokenUsageRecord
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            });
        }
        catch { /* non-fatal — do not interrupt agent on DB write failure */ }

        return result;
    }

    // Pattern that matches a complete [ASK: question text] marker anywhere in the buffer.
    private static readonly System.Text.RegularExpressions.Regex AskMarkerRegex =
        new(@"\[ASK:\s*([^\]]+)\]", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Streams an LLM response like <see cref="StreamResponseAsync"/> but watches for
    /// <c>[ASK: question]</c> markers mid-stream. When one is detected the current stream is
    /// cancelled, the marker is stripped from the output, the user is asked the question, and
    /// the LLM call is restarted with the Q&amp;A pair appended to the chat history — so the
    /// answer is in-context before the LLM produces the actual JSON/text artifact.
    /// Q&amp;A pairs are also appended to <paramref name="qaContext"/> so subsequent calls can
    /// forward them to later phases.
    /// </summary>
    protected async Task<string> StreamWithQuestionsAsync(
        Kernel kernel, ChatHistory history, int bookId, int? chapterId,
        AgentRole role, System.Text.StringBuilder qaContext, CancellationToken ct,
        int maxQuestions = 3)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = new OllamaPromptExecutionSettings { Temperature = (float?)0.8f };
        int questionsAsked = 0;
        var fullAccumulated = new System.Text.StringBuilder(); // clean text, no markers

        while (true)
        {
            if (DebugLoggingEnabled)
            {
                var req = new System.Text.StringBuilder();
                req.AppendLine($"=== LLM Request [{role}] Book={bookId} Chapter={chapterId} (Q round {questionsAsked}) ===");
                foreach (var msg in history)
                    req.AppendLine($"[{msg.Role}] {msg.Content}");
                req.AppendLine("=== End Request ===");
                Logger.LogInformation("{LlmRequest}", req.ToString());
            }

            // Local CTS to let us abort the stream when we find an [ASK:] marker.
            using var localCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var buffer = new System.Text.StringBuilder();
            bool markerFound = false;
            string? extractedQuestion = null;

            try
            {
                await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(
                    history, settings, kernel, localCts.Token))
                {
                    if (chunk.Content is not { Length: > 0 } token) continue;

                    buffer.Append(token);

                    // Check whether a complete [ASK: ...] marker is now present in the buffer.
                    var bufStr = buffer.ToString();
                    var match = AskMarkerRegex.Match(bufStr);
                    if (match.Success)
                    {
                        extractedQuestion = match.Groups[1].Value.Trim();
                        markerFound = true;

                        // Stream only the portion before the marker to the UI.
                        var beforeMarker = bufStr[..match.Index];
                        if (beforeMarker.Length > 0)
                        {
                            // We already streamed tokens up to here — we can't "unstream" them,
                            // but we've been suppressing streaming until now (below).
                            // Actually, we stream the clean pre-marker text in one shot here.
                        }

                        localCts.Cancel(); // stops the foreach loop
                        break;
                    }

                    // Hold back the last 8 chars (length of "[ASK: " + 2) to avoid streaming
                    // a partial marker prefix. Flush everything before that window.
                    const int HoldBack = 8;
                    if (buffer.Length > HoldBack)
                    {
                        var safeToStream = bufStr[..(buffer.Length - HoldBack)];
                        if (safeToStream.Length > 0)
                            await Notifier.StreamTokenAsync(bookId, chapterId, safeToStream, ct);
                        // Replace buffer with only the held-back tail
                        buffer.Clear();
                        buffer.Append(bufStr[(bufStr.Length - HoldBack)..]);
                    }
                }
            }
            catch (OperationCanceledException) when (markerFound) { /* expected — we cancelled */ }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Logger.LogError(ex, "[Book {BookId}] [{Role}] LLM streaming call failed.", bookId, role);
                throw;
            }

            if (markerFound && extractedQuestion is not null)
            {
                // Flush the text before the marker (clean, no marker) to SignalR and accumulate it.
                var bufStr = buffer.ToString();
                var match = AskMarkerRegex.Match(bufStr);
                var beforeMarker = match.Success ? bufStr[..match.Index] : bufStr;
                // Also handle text that was already streamed in the hold-back loop — it's already
                // gone to SignalR; we just need to accumulate it.  Here 'beforeMarker' is only the
                // tail portion in the buffer at cancellation time.
                if (beforeMarker.Length > 0)
                    await Notifier.StreamTokenAsync(bookId, chapterId, beforeMarker, ct);
                fullAccumulated.Append(beforeMarker);

                // Add partial assistant response to history so the next turn has context.
                history.AddAssistantMessage(fullAccumulated.ToString());

                // Pause and ask the user.
                questionsAsked++;
                Logger.LogInformation("[Book {BookId}] [{Role}] Detected [ASK:] marker (question {N}): {Q}", bookId, role, questionsAsked, extractedQuestion);
                var answer = await AskUserAndWaitAsync(bookId, chapterId, role, extractedQuestion, ct);
                qaContext.AppendLine($"Q: {extractedQuestion}\nA: {answer}\n");

                // Feed answer back and continue.
                history.AddUserMessage($"Answer to your question: {answer}. Please continue from where you left off.");

                if (questionsAsked >= maxQuestions)
                {
                    // Safety valve: tell LLM to stop asking and just produce the output.
                    history.AddUserMessage("Please now generate the output directly without asking further questions.");
                }

                // Reset partial buffer; loop back to start another streaming pass.
                continue;
            }

            // No marker — flush whatever remains in the buffer.
            var remaining = buffer.ToString();
            if (remaining.Length > 0)
                await Notifier.StreamTokenAsync(bookId, chapterId, remaining, ct);
            fullAccumulated.Append(remaining);
            break; // Done streaming this phase.
        }

        var result = fullAccumulated.ToString();

        if (DebugLoggingEnabled)
            Logger.LogInformation("=== LLM Response [{Role}] Book={BookId} Chapter={ChapterId} ===\n{Response}\n=== End Response ===",
                role, bookId, chapterId, result);

        if (result.Trim().Length == 0)
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM returned an empty response.", bookId, role);
        else if (result.Length < 50)
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM returned a suspiciously short response ({Chars} chars).", bookId, role, result.Length);

        int promptTokens = history.Sum(m => (m.Content?.Length ?? 0)) / 4;
        int completionTokens = result.Length / 4;
        try { await Notifier.NotifyTokenStatsAsync(bookId, chapterId, role.ToString(), promptTokens, completionTokens, ct); }
        catch { /* non-fatal */ }
        try
        {
            await Repo.AddTokenUsageAsync(new TokenUsageRecord
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens
            });
        }
        catch { /* non-fatal */ }

        return result;
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

    /// <summary>
    /// Persists an error as a SystemNote agent message so it appears in the chat panel,
    /// then fires the AgentError SignalR event. Never throws.
    /// </summary>
    protected async Task ReportErrorAsync(int bookId, int? chapterId, AgentRole role, string message, CancellationToken ct = default)
    {
        try
        {
            await Repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                MessageType = MessageType.SystemNote,
                Content = $"❌ {message}",
                IsResolved = true
            });
        }
        catch { /* non-fatal */ }
        try { await Notifier.NotifyAgentErrorAsync(bookId, role.ToString(), message, ct); }
        catch { /* non-fatal */ }
    }

    /// <summary>Retrieve relevant context chunks from Qdrant for RAG. Returns empty on failure or when no embedding model is configured.</summary>
    protected async Task<string> GetRagContextAsync(
        int bookId, string query, int topK, ILlmProviderFactory factory, LlmConfiguration config)
    {
        if (string.IsNullOrWhiteSpace(config.EmbeddingModelName))
        {
            Logger.LogDebug("[Book {BookId}] No EmbeddingModelName configured — skipping RAG context retrieval.", bookId);
            return string.Empty;
        }

        try
        {
            var embedder = factory.CreateEmbeddingGeneration(config);
            var embeddings = await embedder.GenerateEmbeddingsAsync([query]);
            var embedding = embeddings[0];
            var chunks = await VectorStore.SearchAsync(bookId, embedding, topK);
            return string.Join("\n\n---\n\n", chunks.Select(c => $"[Chapter {c.ChapterNumber}]\n{c.Text}"));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] RAG context retrieval failed — continuing without context.", bookId);
            return string.Empty;
        }
    }

    /// <summary>Remove any leading markdown heading(s) the LLM added for the chapter title.</summary>
    protected static string StripLeadingChapterHeading(string content, int number, string title)
    {
        var lines = content.TrimStart().Split('\n').ToList();
        // Strip consecutive heading lines that look like a chapter title (# / ## / bold / plain)
        while (lines.Count > 0)
        {
            var raw = lines[0];
            // Normalise: remove markdown heading markers, asterisks (bold), whitespace
            var stripped = raw.TrimStart('#', '*', ' ').TrimEnd('#', '*', ' ').Trim();
            if (string.IsNullOrWhiteSpace(stripped)) { lines.RemoveAt(0); continue; }

            var chapterPrefix = $"chapter {number}";
            bool looksLikeHeading =
                stripped.StartsWith(chapterPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stripped, title, StringComparison.OrdinalIgnoreCase) ||
                // "Chapter One", "Chapter Two" etc.
                System.Text.RegularExpressions.Regex.IsMatch(stripped,
                    @"^chapter\s+(one|two|three|four|five|six|seven|eight|nine|ten|\d+)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (looksLikeHeading) { lines.RemoveAt(0); }
            else break;
        }

        return string.Join('\n', lines).TrimStart();
    }

    /// <summary>
    /// Interpolate well-known placeholders in a user-supplied system prompt with book metadata.
    /// Supported tokens: {TITLE}, {GENRE}, {PREMISE}, {LANGUAGE}, {CHAPTER_COUNT}, {AUTHOR_NOTES}.
    /// </summary>
    protected static string InterpolateSystemPrompt(string prompt, Book book) =>
        prompt
            .Replace("{TITLE}", book.Title ?? "")
            .Replace("{GENRE}", book.Genre ?? "")
            .Replace("{PREMISE}", book.Premise ?? "")
            .Replace("{LANGUAGE}", book.Language ?? "English")
            .Replace("{CHAPTER_COUNT}", book.TargetChapterCount.ToString());

    /// <summary>
    /// Returns the last few paragraphs of the previous chapter to give the Writer
    /// continuity context without relying solely on RAG.
    /// Returns empty string for chapter 1 or when no content is available.
    /// </summary>
    protected async Task<string> GetPreviousChapterEndingAsync(int bookId, int currentChapterNumber, int paragraphCount = 3)
    {
        if (currentChapterNumber <= 1) return string.Empty;
        try
        {
            var chapters = await Repo.GetChaptersAsync(bookId);
            var prev = chapters
                .Where(c => c.Number == currentChapterNumber - 1 && !string.IsNullOrWhiteSpace(c.Content))
                .FirstOrDefault();
            if (prev is null) return string.Empty;

            // Split into paragraphs (blank-line-separated) and take the last N
            var paras = prev.Content!
                .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            var tail = paras.TakeLast(paragraphCount);
            return $"[End of Chapter {prev.Number}: {prev.Title}]\n\n" + string.Join("\n\n", tail);
        }
        catch { return string.Empty; }
    }
}
