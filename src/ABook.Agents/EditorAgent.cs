#pragma warning disable SKEXP0001, SKEXP0010, SKEXP0070

using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;

namespace ABook.Agents;

public class EditorAgent : AgentBase
{
    // Matches the editorial-notes heading the LLM appends after chapter prose.
    // Used to stop streaming those notes into the chapter view.
    private static readonly System.Text.RegularExpressions.Regex _notesHeadingRegex =
        new(@"^##\s+(editorial notes?|editor'?s? notes?|feedback|changes made|notes?|revisions?)\s*$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.Multiline);

    public EditorAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    public async Task EditAsync(int bookId, int chapterId, CancellationToken ct = default,
        CheckerResult? checkerResult = null, string? humanAttentionPoints = null, bool finalizeStatus = true)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        chapter.Status = ChapterStatus.Editing;
        await Repo.UpdateChapterAsync(chapter);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Running", chapterId, ct);

        var (kernel, config) = await GetKernelAsync(bookId);

        // Cross-chapter context: full synopsis spine + 4 targeted RAG queries
        var synopsesBlock = await BuildChapterSynopsesAsync(bookId, chapter.Number, ct);

        string charRag = string.Empty, locationRag = string.Empty, threadRag = string.Empty, phrasesRag = string.Empty;
        if (chapter.Number > 1)
        {
            List<string> charNames;
            try { charNames = JsonSerializer.Deserialize<List<string>>(chapter.CharactersInvolvedJson) ?? []; }
            catch { charNames = []; }
            var charQuery = charNames.Count > 0
                ? $"character appearance description personality {string.Join(' ', charNames)}"
                : "character appearance description personality backstory";

            var locationQuery = $"location place setting description {chapter.Outline}";

            List<string> threadNames;
            try { threadNames = JsonSerializer.Deserialize<List<string>>(chapter.PlotThreadsJson) ?? []; }
            catch { threadNames = []; }
            var threadQuery = threadNames.Count > 0
                ? $"plot thread events foreshadowing {string.Join(' ', threadNames)}"
                : $"plot events sequence foreshadowing {chapter.Outline}";

            var phrasesQuery = "repeated descriptions recurring phrases re-introduction established facts";

            // Sequential — GetRagContextAsync touches the shared scoped BookRepository
            // (AddTokenUsageAsync → SaveChangesAsync), so concurrent calls corrupt the EF
            // Core identity map. VectorStore.SearchAsync is safe (owns its own DbContext).
            charRag     = await GetRagContextAsync(bookId, charQuery,     4, LlmFactory, config, chapter.Id, ct);
            locationRag = await GetRagContextAsync(bookId, locationQuery, 3, LlmFactory, config, chapter.Id, ct);
            threadRag   = await GetRagContextAsync(bookId, threadQuery,   3, LlmFactory, config, chapter.Id, ct);
            phrasesRag  = await GetRagContextAsync(bookId, phrasesQuery,  4, LlmFactory, config, chapter.Id, ct);
        }

        var history = new ChatHistory();
        var bible = await Repo.GetStoryBibleAsync(bookId);

        if (checkerResult is { HasIssues: true, Issues.Length: > 0 })
        {
            await ApplyPatchesAsync(bookId, chapterId, checkerResult!, finalizeStatus, ct);
            return;
        }

        // ── CREATIVE LLM PATH (manual edits / no checker patches) ─────────────────
        await EditWithLlmAsync(bookId, chapterId, humanAttentionPoints, finalizeStatus, ct);
    }

    /// <summary>
    /// Mechanically apply verbatim patches from the continuity checker. No LLM call.
    /// Patches are located via position-based matching and applied end-first to preserve offsets.
    /// Unlocated patches are skipped silently but reported in the feedback message.
    /// A new ChapterVersion is created so history is preserved.
    /// </summary>
    private async Task ApplyPatchesAsync(int bookId, int chapterId, CheckerResult result,
        bool finalizeStatus, CancellationToken ct)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        var content = chapter.Content ?? string.Empty;

        // Locate each patch (end-first to avoid offset corruption on application)
        var appliedPatches = new List<(int Offset, CheckerIssue Issue)>();
        var skippedIssues = new List<(CheckerIssue Issue, string Reason)>();

        foreach (var issue in result.Issues)
        {
            if (string.IsNullOrEmpty(issue.OriginalText))
            {
                skippedIssues.Add((issue, "no verbatim text provided"));
                continue;
            }

            var match = FindPatchLocation(content, issue);
            if (match.Offset >= 0)
                appliedPatches.Add((match.Offset, issue));
            else
                skippedIssues.Add((issue, match.Reason ?? "text not found in chapter"));
        }

        // Sort descending — apply from end of chapter toward the start so offsets stay valid
        appliedPatches.Sort((a, b) => b.Offset.CompareTo(a.Offset));

        foreach (var (offset, issue) in appliedPatches)
        {
            content = content.Remove(offset, issue.OriginalText.Length)
                             .Insert(offset, issue.ReplacementText ?? string.Empty);
        }

        var patchedContent = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);
        var patchedStatus = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;

        // Feedback message grouped by issue type with all fields per fix
        var feedbackSb = BuildEditorialFeedback(appliedPatches.Select(p => p.Issue), skippedIssues);
        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = AgentRole.Editor,
            MessageType = MessageType.Feedback,
            Content = feedbackSb.ToString(),
            IsResolved = true
        });

        var patchVersion = new ChapterVersion
        {
            ChapterId = chapterId,
            BookId = bookId,
            Title = chapter.Title,
            Outline = chapter.Outline,
            Content = patchedContent,
            Status = patchedStatus,
            PovCharacter = chapter.PovCharacter,
            CharactersInvolvedJson = chapter.CharactersInvolvedJson,
            PlotThreadsJson = chapter.PlotThreadsJson,
            ForeshadowingNotes = chapter.ForeshadowingNotes,
            PayoffNotes = chapter.PayoffNotes,
            CreatedBy = "agent:Editor",
            HasEmbeddings = false,
        };
        await Repo.AddChapterVersionAsync(patchVersion);

        // Re-index the edited content so subsequent RAG queries see the corrected prose
        var (kernel, config) = await GetKernelAsync(bookId);
        try { await IndexChapterAsync(bookId, chapterId, patchVersion.Id, kernel, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Failed to index edited chapter version for embeddings.", bookId);
        }

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
    }

    /// <summary>
    /// Creative LLM edit path — used when no checker patches are available (manual edits,
    /// MCP tools). Streams edited prose via SignalR; splits off editorial-notes section.
    /// A new ChapterVersion is created so history is preserved.
    /// </summary>
    private async Task EditWithLlmAsync(int bookId, int chapterId, string? humanAttentionPoints,
        bool finalizeStatus, CancellationToken ct)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        var (kernel, config) = await GetKernelAsync(bookId);
        var bible = await Repo.GetStoryBibleAsync(bookId);

        // Cross-chapter context: synopsis spine + 4 targeted RAG queries for anti-repetition
        var synopsesBlock = await BuildChapterSynopsesAsync(bookId, chapter.Number, ct);

        string charRag = string.Empty, locationRag = string.Empty;
        string threadRag = string.Empty, phrasesRag = string.Empty;
        if (chapter.Number > 1)
        {
            List<string> charNames;
            try { charNames = JsonSerializer.Deserialize<List<string>>(chapter.CharactersInvolvedJson) ?? []; }
            catch { charNames = []; }
            var charQuery = charNames.Count > 0
                ? $"character appearance description personality {string.Join(' ', charNames)}"
                : "character appearance description personality backstory";

            var locationQuery = $"location place setting description {chapter.Outline}";

            List<string> threadNames;
            try { threadNames = JsonSerializer.Deserialize<List<string>>(chapter.PlotThreadsJson) ?? []; }
            catch { threadNames = []; }
            var threadQuery = threadNames.Count > 0
                ? $"plot thread events foreshadowing {string.Join(' ', threadNames)}"
                : $"plot events sequence foreshadowing {chapter.Outline}";

            var phrasesQuery = "repeated descriptions recurring phrases re-introduction established facts";

            charRag     = await GetRagContextAsync(bookId, charQuery,     4, LlmFactory, config, chapter.Id, ct);
            locationRag = await GetRagContextAsync(bookId, locationQuery, 3, LlmFactory, config, chapter.Id, ct);
            threadRag   = await GetRagContextAsync(bookId, threadQuery,   3, LlmFactory, config, chapter.Id, ct);
            phrasesRag  = await GetRagContextAsync(bookId, phrasesQuery,  4, LlmFactory, config, chapter.Id, ct);
        }

        var history = new ChatHistory();

        string systemPrompt;
        systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);
        history.AddSystemMessage(systemPrompt);

        // Build cross-chapter context for anti-repetition checks
        var sb = new System.Text.StringBuilder();

        if (synopsesBlock.Length > 0)
        {
            sb.AppendLine("## Story So Far — Chapter Synopses");
            sb.AppendLine(synopsesBlock);
            sb.AppendLine();
        }
        bool hasRagContext = !string.IsNullOrWhiteSpace(charRag) || !string.IsNullOrWhiteSpace(locationRag)
                          || !string.IsNullOrWhiteSpace(threadRag) || !string.IsNullOrWhiteSpace(phrasesRag);
        if (hasRagContext)
        {
            sb.AppendLine("## Prior Chapter Passages (use to identify re-introductions and repeated content)");
            if (!string.IsNullOrWhiteSpace(charRag))     { sb.AppendLine("\n### Character Details");                              sb.AppendLine(charRag); }
            if (!string.IsNullOrWhiteSpace(locationRag)) { sb.AppendLine("\n### Location & Setting Details");                     sb.AppendLine(locationRag); }
            if (!string.IsNullOrWhiteSpace(threadRag))   { sb.AppendLine("\n### Plot Thread Context");                            sb.AppendLine(threadRag); }
            if (!string.IsNullOrWhiteSpace(phrasesRag))  { sb.AppendLine("\n### Potentially Repeated Phrases & Descriptions");   sb.AppendLine(phrasesRag); }
            sb.AppendLine();
        }

        sb.AppendLine($"Please fix Chapter {chapter.Number}: {chapter.Title}");

        if (!string.IsNullOrWhiteSpace(humanAttentionPoints))
        {
            sb.AppendLine("\n**Additional attention points from the author:**");
            sb.AppendLine(humanAttentionPoints.Trim());
        }
        else
        {
            sb.AppendLine("\nNo specific issues listed — make only minor polish corrections if needed.");
        }

        sb.AppendLine($"\nOriginal content:\n{chapter.Content}");
        history.AddUserMessage(sb.ToString());

        // Stream prose only; stop forwarding tokens to SignalR once editorial-notes heading appears
        var edited = await StreamResponseAsync(kernel, config, history, bookId, chapterId, AgentRole.Editor, ct,
            stopStreamingAt: _notesHeadingRegex);

        // Split off the editorial notes section.
        var notesMatch = _notesHeadingRegex.Match(edited);

        string prose;
        if (notesMatch.Success)
        {
            var notes = edited[notesMatch.Index..];
            prose = edited[..notesMatch.Index].Trim();
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
        var editedContent = StripLeadingChapterHeading(prose, chapter.Number, chapter.Title);
        var editedStatus = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;

        // Save this edit as a new version so history is preserved
        var version = new ChapterVersion
        {
            ChapterId = chapterId,
            BookId = bookId,
            Title = chapter.Title,
            Outline = chapter.Outline,
            Content = editedContent,
            Status = editedStatus,
            PovCharacter = chapter.PovCharacter,
            CharactersInvolvedJson = chapter.CharactersInvolvedJson,
            PlotThreadsJson = chapter.PlotThreadsJson,
            ForeshadowingNotes = chapter.ForeshadowingNotes,
            PayoffNotes = chapter.PayoffNotes,
            CreatedBy = "agent:Editor",
            HasEmbeddings = false,
        };
        await Repo.AddChapterVersionAsync(version);

        // Re-index the edited content so subsequent RAG queries see the corrected prose
        try { await IndexChapterAsync(bookId, chapterId, version.Id, kernel, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* non-fatal — embeddings unavailable */ }

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
    }

    /// <summary>
    /// Locate an issue's OriginalText in chapter content. Strategy:
    ///   1. Normalize whitespace (CRLF→LF, trim trailing) and full-text IndexOf.
    ///      If exactly one match → apply immediately (no position needed).
    ///   2. If ambiguous (multiple matches), use position as a hint to narrow
    ///      the search to that specific line. Only applies if OriginalText is
    ///      confirmed present on the target line.
    /// Returns PatchMatch with offset and optional skip reason for failures.
    /// </summary>
    private static PatchMatch FindPatchLocation(string content, CheckerIssue issue)
    {
        var normalized = NormalizeWhitespace(content);
        var normOriginal = NormalizeWhitespace(issue.OriginalText);

        if (string.IsNullOrEmpty(normOriginal))
            return PatchMatch.Skipped("original text is empty");

        // 1. Full-text IndexOf on normalized content — try unique match first
        int firstIdx = normalized.IndexOf(normOriginal, StringComparison.Ordinal);
        if (firstIdx < 0) return PatchMatch.Skipped("text not found in chapter");

        int occurrenceCount = CountOccurrences(normalized, normOriginal);
        if (occurrenceCount == 1) return PatchMatch.Found(firstIdx);    // unique — done

        // 2. Ambiguous — use position as hint to disambiguate
        var lines = normalized.Split('\n');
        if (issue.Position > 0 && issue.Position <= lines.Length)
        {
            int lineStartOffset = GetLineStartOffset(normalized, issue.Position);
            string lineContent = lines[issue.Position - 1];
            int localIdx = lineContent.IndexOf(normOriginal, StringComparison.Ordinal);
            if (localIdx >= 0) return PatchMatch.Found(lineStartOffset + localIdx);  // confirmed on target line
        }

        return PatchMatch.Skipped("ambiguous — multiple matches and position did not confirm");
    }

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd(' ', '\r', '\t');
        return string.Join("\n", lines);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, idx = 0;
        while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0) { count++; idx += needle.Length; }
        return count;
    }

    private static int GetLineStartOffset(string content, int lineNumber)
    {
        var lines = content.Split('\n');
        int offset = 0;
        for (int i = 0; i < lineNumber - 1 && i < lines.Length; i++)
            offset += lines[i].Length + 1;
        return offset;
    }

    /// <summary>
    /// Build the editorial feedback message grouped by issue type with all fields per fix.
    /// </summary>
    private static System.Text.StringBuilder BuildEditorialFeedback(
        IEnumerable<CheckerIssue> applied, IEnumerable<(CheckerIssue Issue, string Reason)> skipped)
    {
        var sb = new System.Text.StringBuilder();
        var types = new[] { "continuity", "grammar", "repetition", "style" };

        // Group applied patches by type
        var appliedByType = applied.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.ToList());
        int totalApplied = applied.Count();

        sb.AppendLine($"## Editorial Notes ({totalApplied} patch(es) applied");
        if (skipped.Any())
            sb.Append($", {skipped.Count()} skipped)");
        else
            sb.AppendLine(")");

        foreach (var typeName in types)
        {
            if (!appliedByType.TryGetValue(typeName, out var items) || items.Count == 0) continue;

            sb.AppendLine($"\n### {Capitalize(typeName)} ({items.Count} fix(es))");
            for (int n = 0; n < items.Count; n++)
            {
                var issue = items[n];
                if (!string.IsNullOrWhiteSpace(issue.ReplacementText) && !string.IsNullOrEmpty(issue.OriginalText))
                    sb.AppendLine($"{n + 1}. **Line {issue.Position}**: `{EscapeMarkdown(issue.OriginalText)}` → `{EscapeMarkdown(issue.ReplacementText)}` — {issue.Description}");
                else
                    sb.AppendLine($"{n + 1}. **Line {issue.Position}**: {issue.Description}");
                if (!string.IsNullOrWhiteSpace(issue.OriginalText))
                    sb.Append($"   Original: `{EscapeMarkdown(issue.OriginalText)}`\n");
            }
        }

        // Skipped section — factual, with specific reason for each skip
        var skippedByType = skipped.GroupBy(s => s.Issue.Type).ToDictionary(g => g.Key, g => g.ToList());
        if (skippedByType.Count > 0)
        {
            sb.AppendLine("\n⚠️ Could not apply:");
            foreach (var (issue, reason) in skipped)
                sb.AppendLine($"- {Capitalize(issue.Type)} on line {issue.Position} — {reason}");
        }

        return sb;
    }

    /// <summary>
    /// Result of locating a patch: either a confirmed character offset, or a skip with reason.
    /// </summary>
    private sealed record PatchMatch(int Offset, string? Reason)
    {
        public static PatchMatch Found(int offset) => new(offset, null);
        public static PatchMatch Skipped(string reason) => new(-1, reason);
    }

    private static string Capitalize(string s) => char.ToUpper(s[0]) + s[1..];
    private static string EscapeMarkdown(string text) => text.Replace("`", "\\`");
}
