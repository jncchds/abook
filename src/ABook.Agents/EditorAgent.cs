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

        bool hasCheckerIssues = checkerResult is { HasIssues: true, Issues.Length: > 0 };

        if (hasCheckerIssues)
        {
            // ── MECHANICAL PATCH PATH ──────────────────────────────────────────────────
            // Apply verbatim patches from checker; no LLM call needed.
            var content = chapter.Content ?? string.Empty;
            var applied = new List<string>();
            var skipped = new List<string>();

            foreach (var issue in checkerResult!.Issues)
            {
                if (string.IsNullOrEmpty(issue.OriginalText))
                {
                    skipped.Add($"- [{issue.Type}] {issue.Description} (no verbatim patch — manual review needed)");
                    continue;
                }

                // Exact-match first; case-insensitive fallback for minor capitalisation drift
                var idx = content.IndexOf(issue.OriginalText, StringComparison.Ordinal);
                if (idx < 0) idx = content.IndexOf(issue.OriginalText, StringComparison.OrdinalIgnoreCase);

                if (idx >= 0)
                {
                    content = content.Remove(idx, issue.OriginalText.Length)
                                     .Insert(idx, issue.ReplacementText ?? string.Empty);
                    applied.Add($"- [{issue.Type}] {issue.Description}");
                }
                else
                {
                    skipped.Add($"- [{issue.Type}] {issue.Description} (could not locate passage — manual review needed)");
                }
            }

            var patchedContent = StripLeadingChapterHeading(content, chapter.Number, chapter.Title);
            var patchedStatus = finalizeStatus ? ChapterStatus.Done : ChapterStatus.Review;

            // Feedback message summarising what was applied / skipped
            var feedbackSb = new System.Text.StringBuilder();
            feedbackSb.AppendLine("## Editorial Notes");
            if (applied.Count > 0)
            {
                feedbackSb.AppendLine($"\n**Applied {applied.Count} patch(es):**");
                foreach (var a in applied) feedbackSb.AppendLine(a);
            }
            if (skipped.Count > 0)
            {
                feedbackSb.AppendLine($"\n⚠️ **Could not apply {skipped.Count} fix(es) — manual review recommended:**");
                foreach (var s in skipped) feedbackSb.AppendLine(s);
            }
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

            try { await IndexChapterAsync(bookId, chapterId, patchVersion.Id, kernel, config!, ct); }
            catch (OperationCanceledException) { throw; }
            catch { /* non-fatal — embeddings unavailable */ }

            await Notifier.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
            await Notifier.NotifyStatusChangedAsync(bookId, AgentRole.Editor, "Done", chapterId, ct);
            return;
        }

        // ── CREATIVE LLM PATH ─────────────────────────────────────────────────────────
        // Used for manual edits (MCP tools, standalone Edit button) when no checker patches are available.

        string systemPrompt;
        systemPrompt = !string.IsNullOrWhiteSpace(book.EditorSystemPrompt)
            ? InterpolateSystemPrompt(book.EditorSystemPrompt, book, bible)
            : InterpolateSystemPrompt(DefaultPrompts.Editor, book, bible);
        history.AddSystemMessage(systemPrompt);

        // Build cross-chapter context for anti-repetition checks
        var sb = new System.Text.StringBuilder();

        if (synopsesBlock.Length > 0)
        {
            sb.AppendLine("## Story So Far \u2014 Chapter Synopses");
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
            sb.AppendLine("\nNo specific issues listed \u2014 make only minor polish corrections if needed.");
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
}
