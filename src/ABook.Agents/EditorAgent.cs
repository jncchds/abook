
using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

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

        var config = await GetConfigAsync(bookId);

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

        // History variable intentionally removed — not used in this branch.

        if (checkerResult is { HasIssues: true, Issues.Length: > 0 })
        {
            // Separate patchable issues from rewrite-only issues
            var patches = checkerResult.Issues.Where(i =>
                i.Type != "rewrite" && !string.IsNullOrEmpty(i.OriginalText)).ToArray();
            var rewrites = checkerResult.Issues.Where(i => i.Type == "rewrite").ToList();

            if (patches.Length > 0)
            {
                // Phase 1: mechanical patches
                await ApplyPatchesAsync(bookId, chapterId, new CheckerResult(true, patches, string.Empty), finalizeStatus, ct);
            }

            // Phase 2: creative pass for rewrite issues (on patched content if applicable)
            if (rewrites.Count > 0)
            {
                ChapterVersion? patchedVersion = null;
                if (patches.Length > 0)
                {
                    // Load the version we just saved to get patched content
                    var versions = await Repo.GetChapterVersionsAsync(chapterId);
                    patchedVersion = versions.OrderByDescending(v => v.CreatedAt).FirstOrDefault();
                }

                string contentToEdit = patchedVersion?.Content ?? chapter.Content;
                string instructions = BuildRewriteInstructions(rewrites);
                await EditWithLlmForRewrites(bookId, chapterId, contentToEdit, instructions,
                    finalizeStatus, synopsesBlock.ToString(), rewrites, charRag, locationRag, threadRag, phrasesRag, ct);
            }
            else if (patches.Length == 0)
            {
                // No issues at all — minor polish creative pass
                await EditWithLlmAsync(bookId, chapterId, humanAttentionPoints, finalizeStatus, ct);
            }
        }
        else
        {
            // ── CREATIVE LLM PATH (manual edits / no checker patches) ─────────────────
            await EditWithLlmAsync(bookId, chapterId, humanAttentionPoints, finalizeStatus, ct);
        }
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

            var match = FindPatchLocationV2(content, issue);
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

        // ── POST-APPLY VERIFICATION ────────────────────────────────────────
        // Re-run matching on the final content to catch phantom artifacts
        // (offset misalignment that left residual originalText behind).
        var stillPresent = VerifyPatchesApplied(chapter.Content ?? string.Empty, patchedContent, result);
        if (stillPresent.Count > 0)
        {
            Logger.LogWarning("[Book {BookId}] Patch verification: {Count} issue(s) still present after apply.",
                bookId, stillPresent.Count);
            foreach (var remaining in stillPresent)
                Logger.LogWarning("[Book {BookId}] Unresolved: [{Type}] {Description}",
                    bookId, remaining.Type,
                    remaining.Description?.Trim().Substring(0, Math.Min(80, remaining.Description?.Length ?? 0)));
            // Add unresolved items to skippedIssues for the feedback message
            foreach (var u in stillPresent)
                skippedIssues.Add((u, "still present after patch application — possible offset misalignment"));
        }

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
        var config = await GetConfigAsync(bookId);
        try { await IndexChapterAsync(bookId, chapterId, patchVersion.Id, config!, ct); }
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

        var config = await GetConfigAsync(bookId);
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

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();

        string systemPrompt;
        systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, systemPrompt));

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
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, sb.ToString()));

        // Stream prose only; stop forwarding tokens to SignalR once editorial-notes heading appears
        var edited = await StreamResponseAsync(config, messages, bookId, chapterId, AgentRole.Editor, ct,
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
        try { await IndexChapterAsync(bookId, chapterId, version.Id, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* non-fatal — embeddings unavailable */ }

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
    }

    /// <summary>
    /// Build human-readable rewrite instructions from checker-flagged rewrite issues.
    /// Used when the Editor needs a creative pass on already-patched content to resolve
    /// inconsistencies that can't be fixed by mechanical patching (e.g. clothing contradictions,
    /// timeline impossibilities, state changes without transition).
    /// </summary>
    private static string BuildRewriteInstructions(List<CheckerIssue> rewrites)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## Issues Requiring Creative Resolution\n");

        for (int n = 0; n < rewrites.Count; n++)
        {
            var issue = rewrites[n];
            sb.AppendLine($"**Issue {n + 1}:** {issue.Description}");
            if (!string.IsNullOrWhiteSpace(issue.Problem))
                sb.AppendLine($"   Problem: {issue.Problem}");
            if (!string.IsNullOrWhiteSpace(issue.CanonicalFact))
                sb.AppendLine($"   Canonical fact: {issue.CanonicalFact}");
            if (!string.IsNullOrWhiteSpace(issue.Location))
                sb.AppendLine($"   Location: {issue.Location}");
            if (!string.IsNullOrWhiteSpace(issue.SuggestedRewrite))
                sb.AppendLine($"   Suggested approach: {issue.SuggestedRewrite}");
            sb.AppendLine();
        }

        sb.AppendLine("Fix ALL of the above issues by rewriting the affected passages. Maintain consistency " +
                      "throughout the chapter — if a character's appearance, state, or a location detail " +
                      "contradicts itself across paragraphs, choose one version and apply it everywhere " +
                      "the affected element appears. Preserve all other prose exactly as written.");

        return sb.ToString();
    }

    /// <summary>
    /// Creative LLM edit path for resolving rewrite issues flagged by the Checker.
    /// Takes already-patched content plus rewrite instructions, streams a full-chapter
    /// creative pass that resolves contradictions while preserving everything else.
    /// </summary>
    private async Task EditWithLlmForRewrites(int bookId, int chapterId, string contentToEdit,
        string rewriteInstructions, bool finalizeStatus,
        string synopsesBlockStr,
        List<CheckerIssue> rewrites,
        string charRag, string locationRag, string threadRag, string phrasesRag,
        CancellationToken ct)
    {
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        var config = await GetConfigAsync(bookId);
        var bible = await Repo.GetStoryBibleAsync(bookId);

        string systemPrompt;
        systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);

        var messages = new List<Microsoft.Extensions.AI.ChatMessage>();
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.System, systemPrompt));

        // Build cross-chapter context (same structure as EditWithLlmAsync)
        var sb = new System.Text.StringBuilder();

        if (!string.IsNullOrWhiteSpace(synopsesBlockStr))
        {
            sb.AppendLine("## Story So Far — Chapter Synopses");
            sb.AppendLine(synopsesBlockStr);
            sb.AppendLine();
        }
        bool hasRagContext = !string.IsNullOrWhiteSpace(charRag) || !string.IsNullOrWhiteSpace(locationRag)
                          || !string.IsNullOrWhiteSpace(threadRag) || !string.IsNullOrWhiteSpace(phrasesRag);
        if (hasRagContext)
        {
            sb.AppendLine("## Prior Chapter Passages (for context — do not re-introduce or restate)");
            if (!string.IsNullOrWhiteSpace(charRag))     { sb.AppendLine("\n### Character Details");                              sb.AppendLine(charRag); }
            if (!string.IsNullOrWhiteSpace(locationRag)) { sb.AppendLine("\n### Location & Setting Details");                     sb.AppendLine(locationRag); }
            if (!string.IsNullOrWhiteSpace(threadRag))   { sb.AppendLine("\n### Plot Thread Context");                            sb.AppendLine(threadRag); }
            if (!string.IsNullOrWhiteSpace(phrasesRag))  { sb.AppendLine("\n### Potentially Repeated Phrases & Descriptions");   sb.AppendLine(phrasesRag); }
            sb.AppendLine();
        }

        sb.AppendLine($"Please fix Chapter {chapter.Number}: {chapter.Title}");
        sb.AppendLine(rewriteInstructions);
        sb.AppendLine($"\nChapter content (already mechanically patched — only resolve the creative issues above):");
        sb.AppendLine(contentToEdit);
        messages.Add(new Microsoft.Extensions.AI.ChatMessage(Microsoft.Extensions.AI.ChatRole.User, sb.ToString()));

        // Stream prose only; stop forwarding tokens to SignalR once editorial-notes heading appears
        var edited = await StreamResponseAsync(config, messages, bookId, chapterId, AgentRole.Editor, ct,
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

        var editedContent = StripLeadingChapterHeading(prose, chapter.Number, chapter.Title);
        var editedStatus = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;

        // Save as new version — this represents the creative pass resolving rewrite issues
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

        try { await IndexChapterAsync(bookId, chapterId, version.Id, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch { /* non-fatal */ }

        // Post a system note explaining what the creative pass addressed
        var noteSb = new System.Text.StringBuilder();
        noteSb.AppendLine($"✏️ Creative edit — resolved {rewrites.Count} inconsistency issue(s):");
        foreach (var issue in rewrites)
            noteSb.AppendLine($"  - {issue.Description}");
        await Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = chapterId,
            AgentRole = AgentRole.Editor,
            MessageType = MessageType.SystemNote,
            Content = noteSb.ToString(),
            IsResolved = true
        });

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
    }

    /// <summary>
    /// Locate an issue's OriginalText in chapter content. Strategy:
    ///   1. If Position is provided, search within ±3 lines of that line number first.
    ///   2. Fall back to full-text IndexOf on normalized content.
    ///   3. If multiple matches remain and position was provided, try the position line
    ///      as a disambiguator.
    /// Returns PatchMatch with offset and optional skip reason for failures.
    /// </summary>
    /// <summary>
    /// Locate an issue's OriginalText in chapter content. Strategy (v2):
    ///   1. Normalize BOTH sides identically (unify \r\n, trim trailing whitespace per line).
    ///   2. Build offset mapping from normalized→original positions.
    ///   3. IndexOf on the normalized strings to find a candidate match.
    ///   4. If found: translate normalized offset → original offset via the mapping.
    ///   5. Uniqueness check — if the needle appears multiple times in normalized text,
    ///      use Position as disambiguator (search ±3 lines around reported line).
    ///   6. Fallback: paragraph-level matching if exact IndexOf fails or remains ambiguous.
    /// Returns PatchMatch with offset and optional skip reason for failures.
    /// </summary>
    private static PatchMatch FindPatchLocationV2(string content, CheckerIssue issue)
    {
        var normalizedContent = NormalizeForMatch(content);
        var normNeedle = NormalizeForMatch(issue.OriginalText);

        if (string.IsNullOrEmpty(normNeedle))
            return PatchMatch.Skipped("original text is empty");

        // Build offset mapping: normalized position → original position
        int[] mapping = BuildOffsetMapping(content, normalizedContent);

        // 1. Full-text IndexOf on normalized strings
        int firstNormIdx = normalizedContent.IndexOf(normNeedle, StringComparison.Ordinal);
        if (firstNormIdx < 0) return PatchMatch.Skipped("text not found in chapter (even after normalization)");

        // Translate to original offset
        int origOffset;
        if (firstNormIdx >= mapping.Length)
            return PatchMatch.Skipped("offset translation failed — normalized index out of range");
        origOffset = mapping[firstNormIdx];

        // 2. Uniqueness check
        int occurrenceCount = CountOccurrences(normalizedContent, normNeedle);
        if (occurrenceCount == 1)
            return PatchMatch.Found(origOffset);

        // 3. Ambiguous — use Position as disambiguator
        if (issue.Position.HasValue && issue.Position.Value > 0)
        {
            var matchNearPosition = FindInLineWindow(normalizedContent, normNeedle, issue.Position.Value);
            if (matchNearPosition >= 0) return PatchMatch.Found(mapping[matchNearPosition]);

            // Fallback: try line-window on original content directly
            // (in case position refers to original lines rather than normalized)
            var origLines = content.Split('\n');
            int startLine = Math.Max(0, issue.Position.Value - 1 - 3);
            int endLine = Math.Min(origLines.Length - 1, issue.Position.Value - 1 + 3);
            for (int i = startLine; i <= endLine; i++)
            {
                string origLine = origLines[i].TrimEnd(' ', '\t');
                int localIdx = origLine.IndexOf(issue.OriginalText.TrimEnd(' ', '\t'), StringComparison.Ordinal);
                if (localIdx >= 0) return PatchMatch.Found(GetLineStartOffset(content, i + 1) + localIdx);
            }
        }

        // 4. Paragraph-level fallback: split content into paragraphs (blank-line-separated),
        //    search each for the needle normalized. This handles cases where IndexOf fails due to
        //    smart-quote differences or subtle character variations.
        var paragraphs = SplitParagraphs(normalizedContent);
        foreach (var para in paragraphs)
        {
            int paraNormIdx = para.IndexOf(normNeedle, StringComparison.Ordinal);
            if (paraNormIdx >= 0) return PatchMatch.Found(paraNormIdx);
        }

        return PatchMatch.Skipped("ambiguous — multiple matches and position did not confirm");
    }

    /// <summary>
    /// Search for normOriginal within ±window lines of the given lineNumber.
    /// Returns character offset in normalized content, or -1 if no match found.
    /// </summary>
    private static int FindInLineWindow(string normalized, string needle, int lineNumber, int window = 3)
    {
        var lines = normalized.Split('\n');
        int startLine = Math.Max(0, lineNumber - 1 - window);
        int endLine = Math.Min(lines.Length - 1, lineNumber - 1 + window);

        for (int i = startLine; i <= endLine; i++)
        {
            int offset = GetLineStartOffset(normalized, i + 1);
            string lineContent = lines[i];
            int localIdx = lineContent.IndexOf(needle, StringComparison.Ordinal);
            if (localIdx >= 0) return offset + localIdx;
        }

        return -1;
    }

    /// <summary>
    /// Normalizes text for patch matching. Unifies line endings and trims trailing whitespace per line,
    /// preserving newline boundaries. Returns the normalized string.
    /// </summary>
    private static string NormalizeForMatch(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        // Unify \r\n → \n first
        var unified = text.Replace("\r\n", "\n").Replace("\r", "\n");
        // Trim trailing whitespace per line, keep newlines intact
        var lines = unified.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd(' ', '\t');
        return string.Join("\n", lines);
    }

    /// <summary>
    /// Builds a parallel index mapping each character position in the normalized text to its
    /// corresponding position in the original (pre-normalization) text. Handles \r\n→\n shrinking
    /// and trailing-whitespace trimming. The returned array has length == normalized.Length;
    /// mapping[i] is the original string index of the character at normalized position i.
    /// </summary>
    private static int[] BuildOffsetMapping(string original, string normalized)
    {
        if (string.IsNullOrEmpty(normalized)) return Array.Empty<int>();

        var mapping = new int[normalized.Length];
        int ni = 0; // current position in normalized output

        // Split both strings by \n — NormalizeForMatch does:\n.Replace("\r\n", "\n") then split.
        // We walk each line's trimmed characters and track positions from the original.
        var origLines = original.Split('\n');
        var normLines = normalized.Split('\n');

        for (int lineIdx = 0; lineIdx < origLines.Length && ni < mapping.Length; lineIdx++)
        {
            var origLine = origLines[lineIdx];
            // Trim trailing whitespace EXACTLY as NormalizeForMatch does it
            var trimmedOrig = origLine.TrimEnd(' ', '\t');

            for (int ci = 0; ci < trimmedOrig.Length && ni < mapping.Length; ci++)
            {
                char c = trimmedOrig[ci];

                if (c == '\r' && ci + 1 < trimmedOrig.Length && trimmedOrig[ci + 1] == '\n')
                {
                    // \r\n within this line → single \n in normalized output.
                    // Both original positions map to the same normalized position.
                    if (ni < mapping.Length) mapping[ni] = ci;
                    ni++;
                    ci++; // skip the '\n' — consumed by the '\r' line ending
                }
                else if (c == '\r')
                {
                    // Standalone '\r' → single '\n' in normalized output
                    mapping[ni] = ci;
                    ni++;
                }
                else
                {
                    mapping[ni] = ci;
                    ni++;
                }
            }
        }

        return mapping;
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
    /// Split text into paragraphs separated by blank lines (\n\n or more). Returns array of
    /// paragraph strings (without the separating newlines). Empty input returns empty array.
    /// </summary>
    private static string[] SplitParagraphs(string text)
    {
        if (string.IsNullOrEmpty(text)) return Array.Empty<string>();
        var parts = text.Split(new[] { "\n\n" }, StringSplitOptions.None);
        // Trim each paragraph's trailing whitespace
        for (int i = 0; i < parts.Length; i++)
            parts[i] = parts[i].TrimEnd(' ', '\t');
        return parts;
    }

    /// <summary>
    /// After all patches are applied, re-run patch matching to verify each issue was actually
    /// resolved. Returns a list of issues whose originalText STILL exists in the patched content.
    /// This catches phantom artifacts where offset misalignment left residual text behind.
    /// </summary>
    private static List<CheckerIssue> VerifyPatchesApplied(string originalContent, string patchedContent, CheckerResult result)
    {
        var stillPresent = new List<CheckerIssue>();
        foreach (var issue in result.Issues)
        {
            if (string.IsNullOrEmpty(issue.OriginalText)) continue; // rewrite-type — not verbatim matchable

            var normOrig = NormalizeForMatch(issue.OriginalText);
            var normPatched = NormalizeForMatch(patchedContent);
            int idx = normPatched.IndexOf(normOrig, StringComparison.Ordinal);
            if (idx >= 0)
                stillPresent.Add(issue); // this patch didn't actually remove the original text
        }
        return stillPresent;
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
                var lineHint = issue.Position.HasValue ? $"Line {issue.Position.Value}: " : string.Empty;
                if (!string.IsNullOrWhiteSpace(issue.ReplacementText) && !string.IsNullOrEmpty(issue.OriginalText))
                    sb.AppendLine($"{n + 1}. **{lineHint}`{EscapeMarkdown(issue.OriginalText)}` → `{EscapeMarkdown(issue.ReplacementText)}` — {issue.Description}");
                else
                    sb.AppendLine($"{n + 1}. **{lineHint}{issue.Description}");
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
        {
            var posHint = issue.Position.HasValue ? $" on line {issue.Position.Value}" : string.Empty;
            sb.AppendLine($"- {Capitalize(issue.Type)}{posHint} — {reason}");
        }
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
