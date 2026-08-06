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
    protected async Task<string> StreamResponseAsync(
        ILlmChatClient client, LlmConfiguration config, IReadOnlyList<LlmChatMessage> messages,
        int bookId, int? chapterId, AgentRole role, CancellationToken ct,
        int suspiciousThreshold = 50,
        System.Text.RegularExpressions.Regex? stopStreamingAt = null, string? jsonSchema = null)
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
        sb.Clear();
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

        var thinkingSb = new System.Text.StringBuilder();

        try
        {
            bool stopStreaming = false;
            await foreach (var chunk in client.StreamAsync(messages, options, ct))
            {
                if (chunk.Reasoning?.Length > 0)
                    thinkingSb.Append(chunk.Reasoning);

                if (chunk.Content?.Length > 0)
                {
                    sb.Append(chunk.Content);
                    if (!stopStreaming)
                    {
                        if (stopStreamingAt is not null && stopStreamingAt.IsMatch(sb.ToString()))
                            stopStreaming = true;
                        else
                            await Notifier.StreamTokenAsync(bookId, chapterId, role, chunk.Content, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            var reason = DescribeFailure(ex, ct.IsCancellationRequested, config.TimeoutMs);
            Logger.LogWarning("[Book {BookId}] [{Role}] LLM streaming call ended after receiving {Chars} chars: {Reason}",
                bookId, role, sb.Length, reason);
            await RecordUsageAsync(bookId, chapterId, role, promptTokens, sb.Length / 4,
                config.Endpoint, config.ModelName, reason, ct);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] [{Role}] LLM streaming call failed after receiving {Chars} chars. Partial response:\n{Partial}",
                bookId, role, sb.Length, sb.Length > 0 ? sb.ToString() : "(empty)");
            await RecordUsageAsync(bookId, chapterId, role, promptTokens, sb.Length / 4,
                config.Endpoint, config.ModelName, DescribeFailure(ex, false, config.TimeoutMs), ct);
            throw;
        }

        var result = sb.ToString();

        var (inlineThinking, cleanedResult) = ExtractThinkingTags(result);
        var fullThinking = MergeThinking(thinkingSb.ToString(), inlineThinking);
        if (!string.IsNullOrWhiteSpace(fullThinking))
        {
            result = cleanedResult;
            sb.Clear();
            sb.Append(result);
            try { await SaveThinkingAsync(bookId, chapterId, role, fullThinking, ct); }
            catch { /* non-fatal */ }
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
        StateService.SetStatus(bookId, new AgentRunStatus(role, "WaitingForInput", chapterId));
        await Notifier.NotifyStatusChangedAsync(bookId, role, "WaitingForInput", chapterId, ct);

        await StateService.PersistRunPausedAsync(bookId, msg.Id);

        var answer = await tcs.Task;

        StateService.SetStatus(bookId, new AgentRunStatus(role, "Running", chapterId));
        await StateService.PersistRunResumedAsync(bookId);
        await Notifier.NotifyStatusChangedAsync(bookId, role, "Running", chapterId, ct);

        return answer;
    }

    protected Task ReportErrorAsync(int bookId, int? chapterId, AgentRole role, string message, CancellationToken ct = default) =>
        ReportAgentErrorCoreAsync(Repo, Notifier, Logger, bookId, role, chapterId, message);

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
            return string.Join("\n\n---\n\n", chunks.Select(c => $"[Chapter {c.ChapterNumber}]\n{c.Text}"));
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

    internal static string InterpolateSystemPrompt(string prompt, Book book, ABook.Core.Models.StoryBible? bible = null, string? chapterSynopses = null)
    {
        var result = prompt
            .Replace(PromptPlaceholders.Title, book.Title ?? "")
            .Replace(PromptPlaceholders.Genre, book.Genre ?? "")
            .Replace(PromptPlaceholders.Premise, book.Premise ?? "")
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

        return result;
    }

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

            var tail = paras.TakeLast(paragraphCount);
            return $"[End of Chapter {prev.Number}: {prev.Title}]\n\n" + string.Join("\n\n", tail);
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

            return string.Join("\n", relevant.Select(c =>
                $"{c.Number}. **{c.Title}** — {c.Outline}"));
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

    protected async Task IndexChapterAsync(int bookId, int chapterId, int chapterVersionId, LlmConfiguration config, CancellationToken ct)
    {
        await VectorStore.EnsureCollectionAsync(bookId, ct);

        var version = await Repo.GetChapterVersionAsync(chapterId, chapterVersionId);
        if (version is null || string.IsNullOrEmpty(version.Content)) return;

        var chapter = await Repo.GetChapterAsync(bookId, chapterId);
        if (chapter is null) return;

        await VectorStore.DeleteVersionChunksAsync(bookId, chapterVersionId, ct);

        var chunks = TextChunker.Chunk(version.Content);
        var embedder = LlmFactory.CreateEmbeddingGeneration(config);

        int embeddedChars = 0;
        try
        {
            for (int i = 0; i < chunks.Count; i++)
            {
                var embeddings = await embedder.GenerateAsync([chunks[i]], cancellationToken: ct);
                var embedding = embeddings[0].Vector;
                embeddedChars += chunks[i].Length;
                await VectorStore.UpsertChunkAsync(bookId, chapterId, chapter.Number, i, chunks[i], embedding, ct, chapterVersionId);
            }
        }
        catch (Exception ex)
        {
            // Still bill the chunks that were embedded before the failure.
            await RecordUsageAsync(bookId, chapterId, AgentRole.Embedder, embeddedChars / 4, 0,
                config.Endpoint, config.EmbeddingModelName,
                DescribeFailure(ex, ex is OperationCanceledException && ct.IsCancellationRequested, config.TimeoutMs), ct);
            throw;
        }

        await RecordUsageAsync(bookId, chapterId, AgentRole.Embedder, chunks.Sum(c => c.Length) / 4, 0,
            config.Endpoint, config.EmbeddingModelName, failureReason: null, ct);
    }

    // ── Thinking / Reasoning helpers ────────────────────────────────────────────

    private static readonly System.Text.RegularExpressions.Regex ThinkTagRegex =
        new(@"<think(?:ing)?>(.*?)</think(?:ing)?>",
            System.Text.RegularExpressions.RegexOptions.Singleline |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static (string thinking, string cleaned) ExtractThinkingTags(string text)
    {
        var matches = ThinkTagRegex.Matches(text);
        if (matches.Count == 0) return (string.Empty, text);

        var thinking = string.Join("\n\n", matches.Select(m => m.Groups[1].Value.Trim()));
        var cleaned = ThinkTagRegex.Replace(text, string.Empty).Trim();
        return (thinking, cleaned);
    }

    private static string MergeThinking(string metaThinking, string inlineThinking)
    {
        if (string.IsNullOrWhiteSpace(metaThinking)) return inlineThinking;
        if (string.IsNullOrWhiteSpace(inlineThinking)) return metaThinking;
        return metaThinking + "\n\n" + inlineThinking;
    }

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

    private async Task SaveThinkingAsync(int bookId, int? chapterId, AgentRole role, string thinking, CancellationToken ct)
    {
        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = role,
            MessageType = MessageType.SystemNote,
            Content = $"💭 Thinking\n\n{thinking}",
            IsResolved = true
        });
        try { await Notifier.NotifyMessagesUpdatedAsync(bookId, ct); }
        catch { /* non-fatal */ }
    }
}
