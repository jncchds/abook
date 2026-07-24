using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

public class EditorAgent : AgentBase
{
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

        var (client, config) = await GetChatClientAsync(bookId);

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

            charRag     = await GetRagContextAsync(bookId, charQuery,     4, LlmFactory, config, chapter.Id, ct);
            locationRag = await GetRagContextAsync(bookId, locationQuery, 3, LlmFactory, config, chapter.Id, ct);
            threadRag   = await GetRagContextAsync(bookId, threadQuery,   3, LlmFactory, config, chapter.Id, ct);
            phrasesRag  = await GetRagContextAsync(bookId, phrasesQuery,  4, LlmFactory, config, chapter.Id, ct);
        }

        if (checkerResult is { HasIssues: true, Issues.Length: > 0 })
        {
            var patches = checkerResult.Issues.Where(i =>
                i.Type != "rewrite" && !string.IsNullOrEmpty(i.OriginalText)).ToArray();
            var rewrites = checkerResult.Issues.Where(i => i.Type == "rewrite").ToList();

            if (patches.Length > 0)
                await ApplyPatchesAsync(bookId, chapterId, new CheckerResult(true, patches, string.Empty), finalizeStatus, ct);

            if (rewrites.Count > 0)
            {
                ChapterVersion? patchedVersion = null;
                if (patches.Length > 0)
                {
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
                await EditWithLlmAsync(bookId, chapterId, humanAttentionPoints, finalizeStatus, ct);
            }
        }
        else
        {
            await EditWithLlmAsync(bookId, chapterId, humanAttentionPoints, finalizeStatus, ct);
        }
    }

    private async Task ApplyPatchesAsync(int bookId, int chapterId, CheckerResult result,
        bool finalizeStatus, CancellationToken ct)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");

        var content = NormalizeWhitespace(chapter.Content ?? string.Empty);

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

        appliedPatches.Sort((a, b) => b.Offset.CompareTo(a.Offset));

        foreach (var (offset, issue) in appliedPatches)
        {
            var removeLength = NormalizeWhitespace(issue.OriginalText).Length;
            content = content.Remove(offset, removeLength)
                             .Insert(offset, issue.ReplacementText ?? string.Empty);
        }

        var patchedContent = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);
        var patchedStatus = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;

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

        var (_, config) = await GetChatClientAsync(bookId);
        try { await IndexChapterAsync(bookId, chapterId, patchVersion.Id, config!, ct); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Failed to index edited chapter version for embeddings.", bookId);
        }

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
    }

    private async Task EditWithLlmAsync(int bookId, int chapterId, string? humanAttentionPoints,
        bool finalizeStatus, CancellationToken ct)
    {
        var chapter = await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var book = await Repo.GetByIdAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");

        var (client, config) = await GetChatClientAsync(bookId);
        var bible = await Repo.GetStoryBibleAsync(bookId);

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

        var messages = new List<LlmChatMessage>();

        var systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);
        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));

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
            if (!string.IsNullOrWhiteSpace(charRag))     { sb.AppendLine("\n### Character Details");                             sb.AppendLine(charRag); }
            if (!string.IsNullOrWhiteSpace(locationRag)) { sb.AppendLine("\n### Location & Setting Details");                    sb.AppendLine(locationRag); }
            if (!string.IsNullOrWhiteSpace(threadRag))   { sb.AppendLine("\n### Plot Thread Context");                           sb.AppendLine(threadRag); }
            if (!string.IsNullOrWhiteSpace(phrasesRag))  { sb.AppendLine("\n### Potentially Repeated Phrases & Descriptions");  sb.AppendLine(phrasesRag); }
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
        messages.Add(new LlmChatMessage(LlmChatRole.User, sb.ToString()));

        var edited = await StreamResponseAsync(client, config, messages, bookId, chapterId, AgentRole.Editor, ct,
            stopStreamingAt: _notesHeadingRegex);

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

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
    }

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

        var (client, config) = await GetChatClientAsync(bookId);
        var bible = await Repo.GetStoryBibleAsync(bookId);

        var systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);

        var messages = new List<LlmChatMessage>();
        messages.Add(new LlmChatMessage(LlmChatRole.System, systemPrompt));

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
            if (!string.IsNullOrWhiteSpace(charRag))     { sb.AppendLine("\n### Character Details");                             sb.AppendLine(charRag); }
            if (!string.IsNullOrWhiteSpace(locationRag)) { sb.AppendLine("\n### Location & Setting Details");                    sb.AppendLine(locationRag); }
            if (!string.IsNullOrWhiteSpace(threadRag))   { sb.AppendLine("\n### Plot Thread Context");                           sb.AppendLine(threadRag); }
            if (!string.IsNullOrWhiteSpace(phrasesRag))  { sb.AppendLine("\n### Potentially Repeated Phrases & Descriptions");  sb.AppendLine(phrasesRag); }
            sb.AppendLine();
        }

        sb.AppendLine($"Please fix Chapter {chapter.Number}: {chapter.Title}");
        sb.AppendLine(rewriteInstructions);
        sb.AppendLine($"\nChapter content (already mechanically patched — only resolve the creative issues above):");
        sb.AppendLine(contentToEdit);
        messages.Add(new LlmChatMessage(LlmChatRole.User, sb.ToString()));

        var edited = await StreamResponseAsync(client, config, messages, bookId, chapterId, AgentRole.Editor, ct,
            stopStreamingAt: _notesHeadingRegex);

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

    private static PatchMatch FindPatchLocation(string content, CheckerIssue issue)
    {
        var normalized = NormalizeWhitespace(content);
        var normOriginal = NormalizeWhitespace(issue.OriginalText);

        if (string.IsNullOrEmpty(normOriginal))
            return PatchMatch.Skipped("original text is empty");

        var p1 = TryLocate(normalized, normOriginal, issue.Position);
        if (p1.Offset >= 0) return p1;

        var p2 = TryLocate(NormalizeQuotes(normalized), NormalizeQuotes(normOriginal), issue.Position);
        if (p2.Offset >= 0) return p2;

        return PatchMatch.Skipped("text not found in chapter");
    }

    private static PatchMatch TryLocate(string haystack, string needle, int? positionHint)
    {
        if (positionHint.HasValue && positionHint.Value > 0)
        {
            int windowIdx = FindInLineWindow(haystack, needle, positionHint.Value, window: 5);
            if (windowIdx >= 0) return PatchMatch.Found(windowIdx);
        }

        int firstIdx = haystack.IndexOf(needle, StringComparison.Ordinal);
        if (firstIdx < 0) return PatchMatch.Skipped("not found");

        int count = CountOccurrences(haystack, needle);
        if (count == 1) return PatchMatch.Found(firstIdx);

        if (positionHint.HasValue && positionHint.Value > 0)
        {
            var lines = haystack.Split('\n');
            if (positionHint.Value <= lines.Length)
            {
                int lineStart = GetLineStartOffset(haystack, positionHint.Value);
                int localIdx = lines[positionHint.Value - 1].IndexOf(needle, StringComparison.Ordinal);
                if (localIdx >= 0) return PatchMatch.Found(lineStart + localIdx);
            }

            int targetOffset = GetLineStartOffset(haystack, positionHint.Value);
            int best = -1, bestDist = int.MaxValue, idx = 0;
            while ((idx = haystack.IndexOf(needle, idx, StringComparison.Ordinal)) >= 0)
            {
                int dist = Math.Abs(idx - targetOffset);
                if (dist < bestDist) { bestDist = dist; best = idx; }
                idx += needle.Length;
            }
            if (best >= 0) return PatchMatch.Found(best);
        }

        return PatchMatch.Skipped("ambiguous — multiple matches and position did not confirm");
    }

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

    private static string NormalizeWhitespace(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
            lines[i] = lines[i].TrimEnd(' ', '\r', '\t');
        return string.Join("\n", lines);
    }

    private static string NormalizeQuotes(string text) =>
        text.Replace('‘', '\'').Replace('’', '\'')
            .Replace('“', '"').Replace('”', '"');

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

    private static System.Text.StringBuilder BuildEditorialFeedback(
        IEnumerable<CheckerIssue> applied, IEnumerable<(CheckerIssue Issue, string Reason)> skipped)
    {
        var sb = new System.Text.StringBuilder();
        var types = new[] { "continuity", "grammar", "repetition", "style" };

        var appliedList = applied.ToList();
        var skippedList = skipped.ToList();
        var appliedByType = appliedList.GroupBy(i => i.Type).ToDictionary(g => g.Key, g => g.ToList());

        var skippedPart = skippedList.Count > 0 ? $", {skippedList.Count} skipped" : string.Empty;
        sb.AppendLine($"✏️ Editorial Notes — {appliedList.Count} patch(es) applied{skippedPart}");

        foreach (var typeName in types)
        {
            if (!appliedByType.TryGetValue(typeName, out var items) || items.Count == 0) continue;

            sb.AppendLine($"\n### {Capitalize(typeName)} ({items.Count} fix(es))");
            for (int n = 0; n < items.Count; n++)
            {
                var issue = items[n];
                var lineHint = issue.Position.HasValue ? $"Line {issue.Position.Value}: " : string.Empty;
                if (!string.IsNullOrWhiteSpace(issue.ReplacementText) && !string.IsNullOrEmpty(issue.OriginalText))
                    sb.AppendLine($"{n + 1}. **{lineHint}`{EscapeMarkdown(issue.OriginalText)}` → `{EscapeMarkdown(issue.ReplacementText)}`** — {issue.Description}");
                else
                    sb.AppendLine($"{n + 1}. **{lineHint}{issue.Description}**");
                if (!string.IsNullOrWhiteSpace(issue.OriginalText))
                    sb.Append($"   Original: `{EscapeMarkdown(issue.OriginalText)}`\n");
            }
        }

        if (skippedList.Count > 0)
        {
            sb.AppendLine("\n⚠️ Could not apply:");
            foreach (var (issue, reason) in skippedList)
            {
                var posHint = issue.Position.HasValue ? $" on line {issue.Position.Value}" : string.Empty;
                sb.AppendLine($"- {Capitalize(issue.Type)}{posHint} — {reason}");
            }
        }

        return sb;
    }

    private sealed record PatchMatch(int Offset, string? Reason)
    {
        public static PatchMatch Found(int offset) => new(offset, null);
        public static PatchMatch Skipped(string reason) => new(-1, reason);
    }

    private static string Capitalize(string s) => char.ToUpper(s[0]) + s[1..];
    private static string EscapeMarkdown(string text) => text.Replace("`", "\\`");
}
