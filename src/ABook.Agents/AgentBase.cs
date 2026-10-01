using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.VectorStore;
using Microsoft.Extensions.Logging;

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

    /// <summary>Longest silence tolerated between streamed chunks when the config sets no TimeoutMs.</summary>
    private const int DefaultStreamIdleTimeoutMs = 300_000;

    /// <summary>Characters at the end of the buffer searched for a <c>stopStreamingAt</c> heading.</summary>
    private const int StopPatternWindow = 512;

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

    protected async Task<(ILlmChatClient client, LlmConfiguration config)> GetChatClientAsync(int bookId)
    {
        var book = await Repo.GetByIdAsync(bookId);
        var config = await Repo.GetLlmConfigAsync(bookId, book?.UserId)
            ?? throw new InvalidOperationException("No LLM configuration found.");
        return (LlmFactory.CreateChatClient(config), config);
    }

    /// <summary>Streams LLM tokens to the book's SignalR group and accumulates the full response.</summary>
    /// <param name="suspiciousThreshold">Minimum response length before a 'suspiciously short' warning is emitted. Pass 0 to suppress.</param>
    /// <param name="stopStreamingAt">Optional regex. Once matched against the accumulated buffer, further tokens are still accumulated but no longer forwarded to the SignalR stream.</param>
    /// <param name="forwardToUi">False for calls whose output is not book content (questions, checker reports): the response is accumulated but never streamed.</param>
    /// <param name="onElementClosed">
    /// Called with the content received so far whenever a chunk closes a JSON object or array, so a caller
    /// can persist each element the moment it finishes — work that survives an interrupted call.
    /// </param>
    /// <remarks>
    /// Reasoning — whether delivered on the provider's reasoning channel or inline as &lt;think&gt; tags —
    /// is never streamed; it is saved as a single "Thinking" chat message when the call ends, successfully or not.
    /// </remarks>
    protected async Task<string> StreamResponseAsync(
        ILlmChatClient client, LlmConfiguration config, IReadOnlyList<LlmChatMessage> messages,
        int bookId, int? chapterId, AgentRole role, CancellationToken ct,
        int suspiciousThreshold = 50,
        System.Text.RegularExpressions.Regex? stopStreamingAt = null, string? jsonSchema = null,
        bool forwardToUi = true, Func<string, Task>? onElementClosed = null)
    {
        var options = new LlmChatOptions
        {
            Temperature = config.Temperature > 0 ? config.Temperature : null,
            MaxTokens = config.MaxTokens is > 0 ? config.MaxTokens : null,
            ReasoningEffort = config.ReasoningEffort,
            TimeoutMs = config.TimeoutMs,
            JsonSchema = jsonSchema,
        };

        var sb = StateService.GetOrCreateStreamBuffer(bookId, chapterId, role.ToString());
        sb.Begin(live: forwardToUi);
        int promptTokens = messages.Sum(m => m.Content.Length) / 4;

        if (DebugLoggingEnabled)
        {
            var req = new System.Text.StringBuilder();
            req.AppendLine($"=== LLM Request [{role}] Book={bookId} Chapter={chapterId} ===");
            foreach (var msg in messages)
                req.AppendLine($"[{msg.Role}] {msg.Content}");
            req.AppendLine("=== End Request ===");
            Logger.LogInformation("{LlmRequest}", req.ToString());
        }

        // sb only ever holds content: inline <think> sections are split off as they arrive, so the
        // UI stream, the refresh-restore buffer and partial-JSON salvage never see reasoning text.
        var thinkingSb = new System.Text.StringBuilder();
        var splitter = new InlineThinkSplitter();
        bool stopStreaming = !forwardToUi;

        async Task AppendAsync((string Content, string Thinking) part, bool forward)
        {
            thinkingSb.Append(part.Thinking);
            if (part.Content.Length == 0) return;
            sb.Append(part.Content);
            if (forward && onElementClosed is not null && part.Content.AsSpan().IndexOfAny('}', ']') >= 0)
                await onElementClosed(sb.ToString());
            if (!forward) sb.HideFrom(sb.Length - part.Content.Length);
            if (!forward || stopStreaming) return;
            // The stop patterns are line-anchored headings, so the last few lines are enough —
            // matching the whole buffer on every chunk would be quadratic in the chapter length.
            if (stopStreamingAt is not null && stopStreamingAt.IsMatch(sb.TailFromLineStart(StopPatternWindow)))
            {
                stopStreaming = true;
                sb.HideFrom(sb.Length - part.Content.Length);
            }
            else
                await Notifier.StreamTokenAsync(bookId, chapterId, role, part.Content, ct);
        }

        // Fail-safe for a stream that stalls: HttpClient timeouts only cover the wait for the response
        // headers (and some providers ignore TimeoutMs), so a connection that goes silent mid-response
        // would otherwise hang the run — and its concurrency slot — forever.
        var idleTimeoutMs = config.TimeoutMs is > 0 ? config.TimeoutMs.Value : DefaultStreamIdleTimeoutMs;
        using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        idleCts.CancelAfter(idleTimeoutMs);

        try
        {
            await foreach (var chunk in client.StreamAsync(messages, options, idleCts.Token))
            {
                idleCts.CancelAfter(idleTimeoutMs);

                if (chunk.Reasoning?.Length > 0)
                    thinkingSb.Append(chunk.Reasoning);

                if (chunk.Content?.Length > 0)
                    await AppendAsync(splitter.Feed(chunk.Content), forward: true);
            }
            await AppendAsync(splitter.Flush(), forward: true);
        }
        catch (OperationCanceledException ex)
        {
            await AppendAsync(splitter.Flush(), forward: false);
            var reason = DescribeFailure(ex, ct.IsCancellationRequested, idleTimeoutMs);
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM streaming call ended after receiving {Chars} chars: {Reason}",
                bookId, role, sb.Length, reason);
            await RecordUsageAsync(bookId, chapterId, role, promptTokens, sb.Length / 4,
                config.Endpoint, config.ModelName, reason, ct);
            await SaveThinkingAsync(bookId, chapterId, role, thinkingSb.ToString());
            throw;
        }
        catch (Exception ex)
        {
            await AppendAsync(splitter.Flush(), forward: false);
            Logger.LogError(ex, "[Book {BookId}] [{Role}] LLM streaming call failed after receiving {Chars} chars. Partial response:\n{Partial}",
                bookId, role, sb.Length, sb.Length > 0 ? sb.ToString() : "(empty)");
            await RecordUsageAsync(bookId, chapterId, role, promptTokens, sb.Length / 4,
                config.Endpoint, config.ModelName, DescribeFailure(ex, false, config.TimeoutMs), ct);
            await SaveThinkingAsync(bookId, chapterId, role, thinkingSb.ToString());
            throw;
        }
        finally
        {
            sb.End();
        }

        var result = sb.ToString();

        var fullThinking = thinkingSb.ToString();
        if (splitter.SawThinking || !string.IsNullOrWhiteSpace(fullThinking))
        {
            // Content after a </think> tag usually starts with blank lines.
            result = result.Trim();
            sb.Set(result);
            await SaveThinkingAsync(bookId, chapterId, role, fullThinking);
        }

        if (DebugLoggingEnabled)
            Logger.LogInformation("=== LLM Response [{Role}] Book={BookId} Chapter={ChapterId} ===\n{Response}\n=== End Response ===",
                role, bookId, chapterId, result);

        if (result.Trim().Length == 0)
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM returned an empty response.", bookId, role);
        else if (suspiciousThreshold > 0 && result.Length < suspiciousThreshold)
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM returned a suspiciously short response ({Chars} chars): {Response}",
                bookId, role, result.Length, result.Trim());

        await RecordUsageAsync(bookId, chapterId, role, promptTokens, result.Length / 4,
            config.Endpoint, config.ModelName, failureReason: null, ct);

        return result;
    }

    /// <summary>
    /// Streams a JSON response without letting a failure discard what the author has already seen.
    /// On success returns the full response and a null failure. On any error — provider timeout,
    /// dropped connection, or user cancellation — returns the tokens accumulated so far together
    /// with the exception, so the caller can salvage complete elements before rethrowing.
    /// </summary>
    protected async Task<(string Raw, Exception? Failure)> StreamJsonResponseAsync(
        ILlmChatClient client, LlmConfiguration config, IReadOnlyList<LlmChatMessage> messages,
        int bookId, int? chapterId, AgentRole role, string jsonSchema, CancellationToken ct, bool forwardToUi = true,
        Func<string, Task>? onElementClosed = null)
    {
        try
        {
            var raw = await StreamResponseAsync(
                client, config, messages, bookId, chapterId, role, ct, jsonSchema: jsonSchema, forwardToUi: forwardToUi,
                onElementClosed: onElementClosed);
            return (raw, null);
        }
        catch (Exception ex)
        {
            var partial = StateService.GetStreamBufferContent(bookId, chapterId, role.ToString()) ?? string.Empty;
            Logger.LogWarning(
                "[Book {BookId}] [{Role}] streaming failed after {Chars} chars — attempting to salvage the partial response.",
                bookId, role, partial.Length);
            return (partial, ex);
        }
    }

    /// <summary>Rethrows <paramref name="ex"/> preserving its original stack trace.</summary>
    [System.Diagnostics.CodeAnalysis.DoesNotReturn]
    protected static void Rethrow(Exception ex) =>
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex).Throw();

    private static readonly System.Text.Json.JsonSerializerOptions PromptJsonOptions = new()
    {
        PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase,
        // Non-Latin scripts must stay readable in the prompt rather than being escaped to \uXXXX.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false,
    };

    /// <summary>Serializes already-saved planning data for inclusion in a prompt, in the agent's own schema shape.</summary>
    protected static string SerializeForPrompt<T>(IEnumerable<T> items) =>
        System.Text.Json.JsonSerializer.Serialize(items, PromptJsonOptions);

    /// <summary>
    /// Persists a token usage row (and pushes it over SignalR) for one LLM call. Never throws — bookkeeping must
    /// not break the run. Pass <paramref name="failureReason"/> to mark the call as failed.
    /// </summary>
    private async Task RecordUsageAsync(
        int bookId, int? chapterId, AgentRole role, int promptTokens, int completionTokens,
        string? endpoint, string? modelName, string? failureReason, CancellationToken ct)
    {
        // On the failure path ct is often already cancelled; don't let that suppress the notification.
        var notifyCt = ct.IsCancellationRequested ? CancellationToken.None : ct;
        try { await Notifier.NotifyTokenStatsAsync(bookId, chapterId, role.ToString(), promptTokens, completionTokens, notifyCt); }
        catch { /* non-fatal */ }
        try
        {
            await Repo.AddTokenUsageAsync(new TokenUsageRecord
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                Endpoint = endpoint,
                ModelName = modelName,
                Failed = failureReason is not null,
                FailureReason = failureReason,
            });
        }
        catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Failed to persist token usage for {Role}.", bookId, role); }
    }

    private const int MaxFailureReasonLength = 1000;

    /// <summary>Builds the human-readable failure reason stored on <see cref="TokenUsageRecord.FailureReason"/>.</summary>
    /// <param name="cancelledByUser">True when the agent's own cancellation token was signalled (Stop pressed), as opposed to a provider timeout.</param>
    internal static string DescribeFailure(Exception ex, bool cancelledByUser, int? timeoutMs = null)
    {
        if (cancelledByUser) return "Cancelled by user";

        var inner = Unwrap(ex);

        if (inner is OperationCanceledException)
            return timeoutMs is > 0 ? $"Timed out after {timeoutMs}ms" : "Timed out";

        string text = inner is HttpRequestException { StatusCode: { } status }
            ? $"HttpRequestException (HTTP {(int)status} {status}): {inner.Message}"
            : $"{inner.GetType().Name}: {inner.Message}";

        return text.Length > MaxFailureReasonLength ? text[..(MaxFailureReasonLength - 1)] + "…" : text;
    }

    private static Exception Unwrap(Exception ex) => ex switch
    {
        AggregateException agg when agg.InnerExceptions.Count == 1 => Unwrap(agg.InnerExceptions[0]),
        { InnerException: OperationCanceledException inner } => inner,
        _ => ex,
    };

    protected async Task<AgentMessage> AskUserAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct, bool isOptional = false)
    {
        var msg = await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = role,
            MessageType = MessageType.Question,
            Content = question,
            IsResolved = false,
            IsOptional = isOptional
        });

        await Notifier.NotifyQuestionAsync(bookId, msg, ct);
        return msg;
    }

    protected async Task<string> AskUserAndWaitAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct, bool isOptional = false)
    {
        var msg = await AskUserAsync(bookId, chapterId, role, question, ct, isOptional);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled());

        StateService.SetPending(bookId, msg.Id, tcs);
        StateService.SetStatus(bookId, new AgentRunStatus(role, RunStates.WaitingForInput, chapterId));
        await Notifier.NotifyStatusChangedAsync(bookId, role, RunStates.WaitingForInput, chapterId, ct);

        await StateService.PersistRunPausedAsync(bookId, msg.Id);

        var answer = await tcs.Task;

        StateService.SetStatus(bookId, new AgentRunStatus(role, RunStates.Running, chapterId));
        await StateService.PersistRunResumedAsync(bookId);
        await Notifier.NotifyStatusChangedAsync(bookId, role, RunStates.Running, chapterId, ct);

        return answer;
    }

    /// <summary>
    /// Announces the step the run is now on. The server-side status is updated too, so a page loaded
    /// mid-run shows the current step (and finds its stream) rather than the role the run started with.
    /// </summary>
    protected async Task ReportRunningAsync(int bookId, AgentRole role, int? chapterId, CancellationToken ct)
    {
        StateService.UpdateRunRole(bookId, role, chapterId);
        await Notifier.NotifyStatusChangedAsync(bookId, role, RunStates.Running, chapterId, ct);
    }

    protected Task ReportErrorAsync(int bookId, int? chapterId, AgentRole role, string message, CancellationToken ct = default) =>
        ReportAgentErrorCoreAsync(Repo, Notifier, Logger, bookId, role, chapterId, message);

    /// <summary>
    /// Records a non-fatal note in the chat — the run produced usable output but something is worth
    /// the author's attention. Unlike <see cref="ReportErrorAsync"/> this raises no error signal.
    /// </summary>
    protected async Task ReportNoteAsync(int bookId, int? chapterId, AgentRole role, string message, CancellationToken ct = default)
    {
        try
        {
            await Repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                MessageType = MessageType.SystemNote,
                Content = $"⚠️ {message}",
                IsResolved = true
            });
            await Notifier.NotifyMessagesUpdatedAsync(bookId, ct);
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] Failed to persist warning SystemNote for {Role}.", bookId, role);
        }
    }

    internal static async Task ReportAgentErrorCoreAsync(
        IBookRepository repo, IBookNotifier notifier, ILogger logger,
        int bookId, AgentRole role, int? chapterId, string message)
    {
        try
        {
            await repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                MessageType = MessageType.SystemNote,
                Content = $"❌ {message}",
                IsResolved = true
            });
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Book {BookId}] Failed to persist error SystemNote for {Role}.", bookId, role);
        }
        try
        {
            await notifier.NotifyAgentErrorAsync(bookId, role.ToString(), message, CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "[Book {BookId}] Failed to notify AgentError via SignalR for {Role}.", bookId, role);
        }
    }

    protected async Task<string> GetRagContextAsync(
        int bookId, string query, int topK, ILlmProviderFactory factory, LlmConfiguration config,
        int? chapterId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.EmbeddingModelName))
        {
            Logger.LogDebug("[Book {BookId}] No EmbeddingModelName configured — skipping RAG context retrieval.", bookId);
            return string.Empty;
        }

        int promptTokens = query.Length / 4;
        bool usageRecorded = false;

        try
        {
            var embedder = factory.CreateEmbeddingGeneration(config);
            var embeddings = await embedder.GenerateAsync([query], cancellationToken: ct);
            var embedding = embeddings[0].Vector;

            await RecordUsageAsync(bookId, chapterId, AgentRole.Embedder, promptTokens, 0,
                config.Endpoint, config.EmbeddingModelName, failureReason: null, ct);
            usageRecorded = true;

            var ancestryIds = await Repo.GetAncestryBookIdsAsync(bookId, ct);
            var chunks = await VectorStore.SearchAsync(bookId, embedding, topK, ancestryIds, ct);
            return PromptTemplates.RagPassages(chunks.Select(c => (c.ChapterNumber, c.Text)));
        }
        catch (OperationCanceledException ex)
        {
            if (!usageRecorded)
                await RecordUsageAsync(bookId, chapterId, AgentRole.Embedder, promptTokens, 0,
                    config.Endpoint, config.EmbeddingModelName, DescribeFailure(ex, ct.IsCancellationRequested, config.TimeoutMs), ct);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] RAG context retrieval failed — continuing without context.", bookId);
            if (!usageRecorded)
                await RecordUsageAsync(bookId, chapterId, AgentRole.Embedder, promptTokens, 0,
                    config.Endpoint, config.EmbeddingModelName, DescribeFailure(ex, false, config.TimeoutMs), ct);
            return string.Empty;
        }
    }

    /// <summary>
    /// Retrieves prior-chapter passages relevant to <paramref name="chapter"/>: characters involved,
    /// locations, plot threads and — when <paramref name="includeRepeatedPhrases"/> — likely repetitions.
    /// Chapter 1 has no prior chapters, so it gets <see cref="ChapterRag.Empty"/>.
    /// </summary>
    protected async Task<ChapterRag> GetChapterRagAsync(
        int bookId, Chapter chapter, LlmConfiguration config, bool includeRepeatedPhrases, CancellationToken ct)
    {
        if (chapter.Number <= 1) return ChapterRag.Empty;

        var charNames = ParseNameList(chapter.CharactersInvolvedJson);
        var threadNames = ParseNameList(chapter.PlotThreadsJson);

        var characters = await GetRagContextAsync(bookId, RagQueries.Characters(charNames), 4, LlmFactory, config, chapter.Id, ct);
        var locations  = await GetRagContextAsync(bookId, RagQueries.Locations(chapter.Outline), 3, LlmFactory, config, chapter.Id, ct);
        var threads    = await GetRagContextAsync(bookId, RagQueries.Threads(threadNames, chapter.Outline), 3, LlmFactory, config, chapter.Id, ct);
        var phrases    = includeRepeatedPhrases
            ? await GetRagContextAsync(bookId, RagQueries.RepeatedPhrases, 4, LlmFactory, config, chapter.Id, ct)
            : string.Empty;

        return new ChapterRag(characters, locations, threads, phrases);
    }

    /// <summary>Established facts of the earlier books in the series; empty for a book with no base book.</summary>
    protected async Task<string> BuildAncestorReferenceAsync(int bookId, CancellationToken ct) =>
        PromptTemplates.AncestorReference(await Repo.GetAncestorPlanningContextAsync(bookId, ct));

    /// <summary>Parses a chapter's JSON list of names (characters involved / plot threads); malformed JSON yields an empty list.</summary>
    protected internal static List<string> ParseNameList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (System.Text.Json.JsonException) { return []; }
    }

    internal static string StripLeadingChapterHeading(string content, int number, string title)
    {
        var lines = content.TrimStart().Split('\n').ToList();
        while (lines.Count > 0)
        {
            var raw = lines[0];
            var stripped = raw.TrimStart('#', '*', ' ').TrimEnd('#', '*', ' ').Trim();
            if (string.IsNullOrWhiteSpace(stripped)) { lines.RemoveAt(0); continue; }

            var chapterPrefix = $"chapter {number}";
            bool looksLikeHeading =
                stripped.StartsWith(chapterPrefix, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(stripped, title, StringComparison.OrdinalIgnoreCase) ||
                System.Text.RegularExpressions.Regex.IsMatch(stripped,
                    @"^chapter\s+(one|two|three|four|five|six|seven|eight|nine|ten|\d+)\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);

            if (looksLikeHeading) { lines.RemoveAt(0); }
            else break;
        }

        return string.Join('\n', lines).TrimStart();
    }

    internal static string InterpolateSystemPrompt(
        string prompt, Book book, ABook.Core.Models.StoryBible? bible = null, string? chapterSynopses = null,
        string? visualStyle = null, string? negativePrompts = null)
    {
        var result = prompt
            .Replace(PromptPlaceholders.Title, book.Title ?? "")
            .Replace(PromptPlaceholders.Genre, book.Genre ?? "")
            .Replace(PromptPlaceholders.Premise, PromptTemplates.Premise(book))
            .Replace(PromptPlaceholders.Language, book.Language ?? "English")
            .Replace(PromptPlaceholders.ChapterCount, book.TargetChapterCount.ToString());

        if (bible is not null)
        {
            result = result
                .Replace(PromptPlaceholders.Setting, bible.SettingDescription ?? "")
                .Replace(PromptPlaceholders.Themes, bible.Themes ?? "")
                .Replace(PromptPlaceholders.Tone, bible.ToneAndStyle ?? "")
                .Replace(PromptPlaceholders.WorldRules, bible.WorldRules ?? "");
        }

        if (chapterSynopses is not null)
            result = result.Replace(PromptPlaceholders.ChapterSynopses, chapterSynopses);

        if (visualStyle is not null)
            result = result.Replace(PromptPlaceholders.VisualStyle, visualStyle);

        if (negativePrompts is not null)
            result = result.Replace(PromptPlaceholders.NegativePrompts, negativePrompts);

        return result;
    }

    /// <summary>
    /// Guards an agent entry point against operating on an archived chapter. Archived chapters are
    /// display-only history: they must never be read into a prompt, nor written to by an agent.
    /// <see cref="Repo"/>'s <c>GetChapterAsync</c> deliberately serves archived rows so the UI can
    /// render and restore them, so every agent that resolves a chapter by id must call this.
    /// </summary>
    protected internal static Chapter EnsureNotArchived(Chapter chapter) =>
        chapter.IsArchived
            ? throw new InvalidOperationException(
                $"Chapter {chapter.Id} is archived and cannot take part in generation.")
            : chapter;

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

            var paras = prev.Content!
                .Split(["\n\n", "\r\n\r\n"], StringSplitOptions.RemoveEmptyEntries)
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToArray();

            return PromptTemplates.PreviousChapterEnding(prev, paras.TakeLast(paragraphCount));
        }
        catch { return string.Empty; }
    }

    protected async Task<string> BuildChapterSynopsesAsync(
        int bookId, int? currentChapterNumber = null, CancellationToken ct = default)
    {
        try
        {
            var chapters = await Repo.GetChaptersAsync(bookId);
            var relevant = chapters
                .Where(c => (currentChapterNumber == null || c.Number < currentChapterNumber)
                    && (!string.IsNullOrWhiteSpace(c.Title) || !string.IsNullOrWhiteSpace(c.Outline)))
                .OrderBy(c => c.Number)
                .ToList();

            if (relevant.Count == 0) return string.Empty;

            return PromptTemplates.ChapterSynopses(relevant);
        }
        catch { return string.Empty; }
    }

    internal static string ExtractJson(string raw, char open, char close)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
            raw = string.Join('\n', raw.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var start = raw.IndexOf(open);
        var end = raw.LastIndexOf(close);
        if (start >= 0 && end > start) return raw[start..(end + 1)];
        return raw;
    }

    protected Task IndexChapterAsync(int bookId, int chapterId, int chapterVersionId, LlmConfiguration config, CancellationToken ct) =>
        ChapterIndexer.IndexVersionAsync(
            Repo, LlmFactory, VectorStore, config, bookId, chapterId, chapterVersionId,
            (embeddedChars, ex) => RecordUsageAsync(
                bookId, chapterId, AgentRole.Embedder, embeddedChars / 4, 0,
                config.Endpoint, config.EmbeddingModelName,
                ex is null
                    ? null
                    : DescribeFailure(ex, ex is OperationCanceledException && ct.IsCancellationRequested, config.TimeoutMs),
                ct),
            ct);

    // ── Thinking / Reasoning helpers ────────────────────────────────────────────

    protected static string Capitalize(string s) => char.ToUpper(s[0]) + s[1..];
    protected static string EscapeMarkdown(string text) => text.Replace("`", "\\`");

    protected static ChapterVersion CreateChapterVersion(
        Chapter chapter, string content, ChapterStatus status, string createdBy) =>
        new()
        {
            ChapterId = chapter.Id,
            BookId = chapter.BookId,
            Title = chapter.Title,
            Outline = chapter.Outline,
            Content = content,
            Status = status,
            PovCharacter = chapter.PovCharacter,
            CharactersInvolvedJson = chapter.CharactersInvolvedJson,
            PlotThreadsJson = chapter.PlotThreadsJson,
            ForeshadowingNotes = chapter.ForeshadowingNotes,
            PayoffNotes = chapter.PayoffNotes,
            CreatedBy = createdBy,
            HasEmbeddings = false,
        };

    /// <summary>
    /// Saves the model's reasoning as a chat message and tells the UI to refresh its messages. Never
    /// throws and ignores cancellation — it also runs on the failure path, where the reasoning is
    /// often the only clue to what went wrong.
    /// </summary>
    private async Task SaveThinkingAsync(int bookId, int? chapterId, AgentRole role, string thinking)
    {
        if (string.IsNullOrWhiteSpace(thinking)) return;
        try
        {
            await Repo.AddMessageAsync(new AgentMessage
            {
                BookId = bookId,
                ChapterId = chapterId,
                AgentRole = role,
                MessageType = MessageType.SystemNote,
                Content = $"💭 Thinking\n\n{thinking.Trim()}",
                IsResolved = true
            });
            await Notifier.NotifyMessagesUpdatedAsync(bookId, CancellationToken.None);
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Failed to save thinking output for {Role}.", bookId, role);
        }
    }
}
