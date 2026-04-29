#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

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

    protected async Task<(Kernel kernel, LlmConfiguration config)> GetKernelAsync(int bookId)
    {
        var book = await Repo.GetByIdAsync(bookId);
        var config = await Repo.GetLlmConfigAsync(bookId, book?.UserId)
            ?? throw new InvalidOperationException("No LLM configuration found.");
        return (LlmFactory.CreateKernel(config), config);
    }

    /// <summary>
    /// Non-streaming LLM call. Used for short question-gathering queries that don't need token-by-token
    /// streaming to the UI. When <paramref name="bookId"/> and <paramref name="role"/> are provided,
    /// token usage is persisted to the DB and emitted via SignalR.
    /// </summary>
    protected async Task<string> GetCompletionAsync(
        Kernel kernel, LlmConfiguration config, ChatHistory history, CancellationToken ct,
        int? bookId = null, int? chapterId = null, AgentRole? role = null, bool jsonMode = false)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = LlmFactory.CreateExecutionSettings(config, 0.7f, jsonMode);

        if (DebugLoggingEnabled)
        {
            var req = new System.Text.StringBuilder();
            req.AppendLine($"=== LLM Completion Request ===");
            foreach (var msg in history)
                req.AppendLine($"[{msg.Role}] {msg.Content}");
            req.AppendLine("=== End Request ===");
            Logger.LogInformation("{LlmRequest}", req.ToString());
        }

        var result = await chat.GetChatMessageContentAsync(history, settings, kernel, ct);
        var text = result.Content ?? string.Empty;

        if (DebugLoggingEnabled)
            Logger.LogInformation("=== LLM Completion Response ===\n{Response}\n=== End Response ===", text);

        if (bookId.HasValue && role.HasValue)
        {
            int promptTokens = history.Sum(m => (m.Content?.Length ?? 0)) / 4;
            int completionTokens = text.Length / 4;
            try { await Notifier.NotifyTokenStatsAsync(bookId.Value, chapterId, role.Value.ToString(), promptTokens, completionTokens, ct); }
            catch { /* non-fatal */ }
            try
            {
                await Repo.AddTokenUsageAsync(new TokenUsageRecord
                {
                    BookId = bookId.Value,
                    ChapterId = chapterId,
                    AgentRole = role.Value,
                    PromptTokens = promptTokens,
                    CompletionTokens = completionTokens,
                    Endpoint = config.Endpoint,
                    ModelName = config.ModelName,
                });
            }
            catch { /* non-fatal */ }
        }

        return text;
    }

    /// <summary>Streams LLM tokens to the book's SignalR group and accumulates the full response.</summary>
    /// <param name="suspiciousThreshold">Minimum response length before a 'suspiciously short' warning is emitted. Pass 0 to suppress.</param>
    protected async Task<string> StreamResponseAsync(
        Kernel kernel, LlmConfiguration config, ChatHistory history, int bookId, int? chapterId, AgentRole role, CancellationToken ct, bool jsonMode = false, int suspiciousThreshold = 50)
    {
        var chat = kernel.GetRequiredService<IChatCompletionService>();
        var settings = LlmFactory.CreateExecutionSettings(config, 0.8f, jsonMode);
        // Reuse the singleton buffer so the HTTP endpoint can serve accumulated content on hard-refresh.
        // Clear any stale content from a previous run with the same key before starting.
        var sb = StateService.GetOrCreateStreamBuffer(bookId, chapterId, role.ToString());
        sb.Clear();
        // Prompt tokens are stable before the call; compute here so they are available in the catch block.
        int promptTokens = history.Sum(m => (m.Content?.Length ?? 0)) / 4;

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
                    await Notifier.StreamTokenAsync(bookId, chapterId, role, token, ct);
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogError(ex, "[Book {BookId}] [{Role}] LLM streaming call failed after receiving {Chars} chars. Partial response:\n{Partial}",
                bookId, role, sb.Length, sb.Length > 0 ? sb.ToString() : "(empty)");
            // Persist a failed record so Token Stats shows partial usage even on error.
            try
            {
                await Repo.AddTokenUsageAsync(new TokenUsageRecord
                {
                    BookId = bookId,
                    ChapterId = chapterId,
                    AgentRole = role,
                    PromptTokens = promptTokens,
                    CompletionTokens = sb.Length / 4,
                    Endpoint = config.Endpoint,
                    ModelName = config.ModelName,
                    Failed = true,
                });
            }
            catch { /* non-fatal */ }
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
                CompletionTokens = completionTokens,
                Endpoint = config.Endpoint,
                ModelName = config.ModelName,
            });
        }
        catch { /* non-fatal — do not interrupt agent on DB write failure */ }

        return result;
    }

    /// <summary>Persist a question message and notify the UI to pause.</summary>
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

    /// <summary>
    /// Ask the user a question, pause execution, and return their answer.
    /// Sets run state to WaitingForInput until the answer is submitted via the API.
    /// The pause is persisted to the DB so the run survives an app restart.
    /// When <paramref name="isOptional"/> is true the user may submit an empty answer to skip.
    /// </summary>
    protected async Task<string> AskUserAndWaitAsync(
        int bookId, int? chapterId, AgentRole role, string question, CancellationToken ct, bool isOptional = false)
    {
        var msg = await AskUserAsync(bookId, chapterId, role, question, ct, isOptional);

        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        ct.Register(() => tcs.TrySetCanceled());

        StateService.SetPending(bookId, msg.Id, tcs);
        StateService.SetStatus(bookId, new AgentRunStatus(role, "WaitingForInput", chapterId));
        await Notifier.NotifyStatusChangedAsync(bookId, role, "WaitingForInput", ct);

        // Persist pause so the question survives a process restart
        await StateService.PersistRunPausedAsync(bookId, msg.Id);

        var answer = await tcs.Task; // throws OperationCanceledException if stopped

        StateService.SetStatus(bookId, new AgentRunStatus(role, "Running", chapterId));
        await StateService.PersistRunResumedAsync(bookId);
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

    /// <summary>Retrieve relevant context chunks from pgvector for RAG. Returns empty on failure or when no embedding model is configured.
    /// Token usage for the embedding call is persisted/notified when <paramref name="ct"/> and <paramref name="chapterId"/> are supplied.</summary>
    protected async Task<string> GetRagContextAsync(
        int bookId, string query, int topK, ILlmProviderFactory factory, LlmConfiguration config,
        int? chapterId = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(config.EmbeddingModelName))
        {
            Logger.LogDebug("[Book {BookId}] No EmbeddingModelName configured — skipping RAG context retrieval.", bookId);
            return string.Empty;
        }

        try
        {
            var embedder = factory.CreateEmbeddingGeneration(config);
            var embeddings = await embedder.GenerateAsync([query], cancellationToken: ct);
            var embedding = embeddings[0].Vector;

            int promptTokens = query.Length / 4;
            try { await Notifier.NotifyTokenStatsAsync(bookId, chapterId, AgentRole.Embedder.ToString(), promptTokens, 0, ct); }
            catch { /* non-fatal */ }
            try
            {
                await Repo.AddTokenUsageAsync(new TokenUsageRecord
                {
                    BookId = bookId,
                    ChapterId = chapterId,
                    AgentRole = AgentRole.Embedder,
                    PromptTokens = promptTokens,
                    CompletionTokens = 0
                });
            }
            catch { /* non-fatal */ }

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
    /// Substitutes <see cref="PromptPlaceholders"/> tokens in a user-supplied system prompt
    /// with live data from <paramref name="book"/> and, when provided, <paramref name="bible"/>.
    /// <para>
    /// Book-level tokens: {TITLE}, {GENRE}, {PREMISE}, {CHAPTER_COUNT}, {LANGUAGE}.
    /// Story-Bible tokens (require <paramref name="bible"/>): {SETTING}, {THEMES}, {TONE}, {WORLD_RULES}.
    /// </para>
    /// </summary>
    protected static string InterpolateSystemPrompt(string prompt, Book book, ABook.Core.Models.StoryBible? bible = null)
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

        return result;
    }

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

    /// <summary>
    /// Extract the outermost JSON object or array from a raw LLM response,
    /// stripping any markdown code fences and surrounding prose.
    /// </summary>
    protected static string ExtractJson(string raw, char open, char close)
    {
        raw = raw.Trim();
        if (raw.StartsWith("```"))
            raw = string.Join('\n', raw.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));
        var start = raw.IndexOf(open);
        var end = raw.LastIndexOf(close);
        if (start >= 0 && end > start) return raw[start..(end + 1)];
        return raw;
    }
}
