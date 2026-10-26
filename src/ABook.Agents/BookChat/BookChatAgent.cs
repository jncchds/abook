using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// The LLM the author writes an Interactive book with. Each reply is one request holding: the system prompt
/// with the action catalogue, a size-capped digest of the book as it stands, a rolling summary of older chat,
/// the last few messages (cut to a fixed length, past replies reduced to their text and what became of their
/// proposals), and the author's message with the passages of the written chapters it brings up. The reply's
/// changes are saved as proposals; nothing touches the book until the author applies them.
/// </summary>
public class BookChatAgent : AgentBase
{
    private readonly BookContentService _content;
    private readonly ChapterSummarizer _summarizer;

    private const string FailedPrefix = "❌";
    /// <summary>Starts the event of a two-step reply saying what is being read — not part of the conversation.</summary>
    internal const string ReadingPrefix = "📖";

    /// <summary>Stale chapter summaries refreshed before a reply, at most — the rest wait for the next one.</summary>
    private const int SummaryRefreshPerTurn = 2;

    public BookChatAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory,
        BookContentService content,
        ChapterSummarizer summarizer)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory)
    {
        _content = content;
        _summarizer = summarizer;
    }

    /// <summary>
    /// Answers the author's message <paramref name="authorMessageId"/> — or, when null, writes the opening reply of
    /// a new book (a Story Bible proposal and questions). A failed call marks the author's message Failed so it can
    /// be retried; only cancellation propagates.
    /// </summary>
    public async Task ReplyAsync(int bookId, int? authorMessageId, CancellationToken ct)
    {
        var author = authorMessageId is int id ? await Repo.GetChatMessageAsync(bookId, id) : null;
        if (authorMessageId is not null && author is null)
            throw new InvalidOperationException($"Chat message {authorMessageId} not found.");
        await ReportRunningAsync(bookId, AgentRole.BookChat, null, ct);

        try
        {
            await _summarizer.RefreshStaleAsync(bookId, SummaryRefreshPerTurn, ct);
            var (reply, parsed) = await ComposeReplyAsync(bookId, author, ct);
            await Repo.AddChatMessageAsync(reply);
            await ResolveDependenciesAsync(reply);
            await SupersedeOlderAsync(bookId, reply);
            // A revision's list replaces the revised reply's open suggestions — only once a list was actually read.
            if (parsed && author?.RevisesMessageId is int revised) await ReplaceRevisedAsync(bookId, revised, reply);
            if (author is not null)
            {
                author.Status = BookChatMessageStatus.Done;
                await Repo.UpdateChatMessageAsync(author);
            }
            await Notifier.NotifyBookChatAsync(bookId, reply.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            await MarkFailedAsync(bookId, author);
            throw;
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Book chat reply failed.", bookId);
            await MarkFailedAsync(bookId, author);
            await AddEventAsync(bookId, $"{FailedPrefix} The reply failed: {DescribeFailure(ex, false)}");
            return;
        }

        try { await UpdateMemoryAsync(bookId, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Updating the chat memory failed.", bookId); }
    }

    private async Task MarkFailedAsync(int bookId, BookChatMessage? author)
    {
        if (author is null) return;
        try
        {
            // The failure may have been a save: drop whatever it left queued before saving again.
            Repo.DiscardPendingChanges();
            author = await Repo.GetChatMessageAsync(bookId, author.Id) ?? author;
            author.Status = BookChatMessageStatus.Failed;
            await Repo.UpdateChatMessageAsync(author);
            await Notifier.NotifyBookChatAsync(bookId, author.Id, CancellationToken.None);
        }
        catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Marking the chat message failed did not work.", bookId); }
    }

    /// <summary>Adds a message saying what happened to the book, and tells the UI.</summary>
    public async Task<BookChatMessage> AddEventAsync(int bookId, string content, IEnumerable<ChatProposal>? proposals = null)
    {
        var message = await Repo.AddChatMessageAsync(new BookChatMessage
        {
            BookId = bookId,
            Role = proposals is null ? BookChatRole.Event : BookChatRole.Assistant,
            Content = content,
            Proposals = proposals?.ToList() ?? [],
        });
        if (proposals is not null) await SupersedeOlderAsync(bookId, message);
        await Notifier.NotifyBookChatAsync(bookId, message.Id, CancellationToken.None);
        return message;
    }

    /// <summary>
    /// Posts a reply that did not come from the chat model — e.g. the follow-up after a chapter is written —
    /// with <paramref name="actions"/> checked and turned into proposals like the model's own.
    /// </summary>
    /// <param name="maxActions">Most proposals kept — more than a reply's <see cref="BookChatActions.MaxActions"/> for a section's chapters.</param>
    public async Task<BookChatMessage> ProposeAsync(int bookId, string content, IReadOnlyList<RawChatAction> actions,
        int maxActions = BookChatActions.MaxActions)
    {
        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var chapters = (await Repo.GetChaptersAsync(bookId)).ToList();
        var state = BookChatState.Of(book, BookContentService.IsBibleLocked(book), await Repo.GetCharacterCardsAsync(bookId),
            await Repo.GetPlotThreadsAsync(bookId), chapters, await Repo.GetSectionsAsync(bookId));
        var (normalized, _) = BookChatActions.Normalize(actions, state, Math.Max(maxActions, BookChatActions.MaxActions));
        var proposals = new List<ChatProposal>();
        foreach (var (action, ordinal) in normalized.Select((a, i) => (a, i)))
            proposals.Add(await ToProposalAsync(bookId, action, ordinal, chapters));
        return await AddEventAsync(bookId, content, proposals);
    }

    // ── One reply ───────────────────────────────────────────────────────────────

    /// <summary>A suggestion still waiting for the author: what a revision replaces.</summary>
    public static bool IsRevisable(ChatProposal p) =>
        p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed or ChatProposalStatus.Queued;

    /// <summary>The open suggestions of the revised reply give way to the revised list.</summary>
    private async Task ReplaceRevisedAsync(int bookId, int revisedId, BookChatMessage reply)
    {
        if (await Repo.GetChatMessageAsync(bookId, revisedId) is not { } revised) return;
        foreach (var p in revised.Proposals.Where(IsRevisable))
        {
            p.Status = ChatProposalStatus.Superseded;
            p.ResultNote = "Replaced by the revised list below.";
            await Repo.UpdateProposalAsync(p);
        }
        await Notifier.NotifyBookChatAsync(bookId, revised.Id, CancellationToken.None);
    }

    /// <summary>The reply, and whether its JSON could be read (a reply that couldn't carries no list).</summary>
    private async Task<(BookChatMessage Reply, bool Parsed)> ComposeReplyAsync(int bookId, BookChatMessage? author, CancellationToken ct)
    {
        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var locked = BookContentService.IsBibleLocked(book);
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var characters = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        var threads = (await Repo.GetPlotThreadsAsync(bookId)).ToList();
        var chapters = (await Repo.GetChaptersAsync(bookId)).ToList();
        var sections = (await Repo.GetSectionsAsync(bookId)).ToList();
        var authorText = author?.Content ?? PromptTemplates.BookChatKickoff;

        var focus = characters.Select(c => c.Name)
            .Where(n => authorText.Contains(n, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var digest = PromptTemplates.BookChatDigest(book, locked, bible, characters, threads, chapters, focus, sections: sections);
        // Two-step replies: the model may first name the parts it needs in full. Not for the opening reply — the
        // new book has nothing to read yet.
        var twoStep = book.ChatPartRequestsEnabled && author is not null;
        var system = PromptTemplates.BookChatSystem(book, locked, BookChatActions.Available(locked), twoStep) + "\n\n" +
                     PromptTemplates.BookChatContext(digest, book.ChatMemory);

        var (client, config) = await GetChatClientAsync(bookId);
        var rag = author is null || !chapters.Any(c => c.Content.Length > 0)
            ? ""
            : await GetRagContextAsync(bookId, authorText + await RevisedLabelsAsync(bookId, author), 6, LlmFactory, config, ct: ct);

        var history = author is null ? [] : await HistoryAsync(bookId, author.Id);
        var revised = author?.RevisesMessageId is int revisedId ? await Repo.GetChatMessageAsync(bookId, revisedId) : null;
        var turn = revised is null
            ? PromptTemplates.BookChatTurn(rag, authorText)
            : PromptTemplates.BookChatReviseTurn(rag, StripNotes(revised.Content),
                revised.Proposals.OrderBy(p => p.Ordinal)
                    .Select(p => (p.Status.ToString(), p.Type, p.Label, p.PayloadJson, p.ConflictNote ?? (IsRevisable(p) ? p.ResultNote : null))),
                authorText);
        var messages = BuildMessages(system, history, turn);

        async Task<(string Raw, ParsedChatReply? Parsed)> CallAsync(List<LlmChatMessage> request, bool partRequests)
        {
            var schema = BookChatActions.ReplySchema(locked, partRequests);
            var raw = await StreamResponseAsync(client, config, request, bookId, null, AgentRole.BookChat, ct,
                suspiciousThreshold: 0, jsonSchema: schema);
            var parsed = BookChatActions.Parse(raw);
            if (parsed is not null) return (raw, parsed);
            // One repair attempt, not streamed: small models sometimes wrap the JSON in prose or break it.
            var repair = new List<LlmChatMessage>(request)
            {
                new(LlmChatRole.Assistant, raw),
                new(LlmChatRole.User, PromptTemplates.BookChatRepairRequest(partRequests)),
            };
            var repaired = await StreamResponseAsync(client, config, repair, bookId, null, AgentRole.BookChat, ct,
                suspiciousThreshold: 0, jsonSchema: schema, forwardToUi: false);
            parsed = BookChatActions.Parse(repaired);
            return (parsed is null ? raw : repaired, parsed);
        }

        var (raw, parsed) = await CallAsync(messages, twoStep);
        if (twoStep && parsed is { Parts.Count: > 0 })
        {
            // Step two: send the parts it asked for and take the answer to that as the reply. The first answer is
            // only the request — the event tells the author what is being read (and clears the streamed text).
            var resolved = ResolveParts(parsed.Parts, chapters, characters, threads, sections: sections);
            await AddEventAsync(bookId, resolved.Labels.Count > 0
                ? $"{ReadingPrefix} Reading {string.Join(", ", resolved.Labels)} before replying…"
                : $"{ReadingPrefix} The parts it asked for do not exist — replying without them…");
            var second = new List<LlmChatMessage>(messages)
            {
                new(LlmChatRole.Assistant, raw),
                new(LlmChatRole.User, PromptTemplates.BookChatPartsTurn(resolved.Parts, resolved.Missing)),
            };
            (raw, parsed) = await CallAsync(second, false);
        }

        if (parsed is null)
            return (new BookChatMessage
            {
                BookId = bookId,
                Role = BookChatRole.Assistant,
                Content = (raw.Trim().Length > 0 ? raw.Trim() : "(no reply)") +
                          "\n\n⚠️ _The reply was not in the expected format, so no changes could be read from it. Send your message again to retry._",
            }, false);

        var state = BookChatState.Of(book, locked, characters, threads, chapters, sections);
        var (actions, dropped) = BookChatActions.Normalize(parsed.Actions, state);

        var content = parsed.Message.Length > 0 ? parsed.Message : actions.Count > 0 ? "Here is what I propose." : "(no reply)";
        if (dropped.Count > 0)
            content += "\n\n⚠️ _Left out — these could not be proposed:_\n" + string.Join("\n", dropped.Select(d => $"- {d}"));

        var proposals = new List<ChatProposal>();
        foreach (var (action, ordinal) in actions.Select((a, i) => (a, i)))
            proposals.Add(await ToProposalAsync(bookId, action, ordinal, chapters));

        var reply = new BookChatMessage
        {
            BookId = bookId,
            Role = BookChatRole.Assistant,
            Content = content,
            ConflictsJson = JsonSerializer.Serialize(parsed.Conflicts, new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase }),
            Proposals = proposals,
        };
        // Dependencies point at proposals of this reply; ids exist only after saving, so they are resolved by the
        // executor through the ordinal stored here.
        foreach (var (action, ordinal) in actions.Select((a, i) => (a, i)))
            if (action.DependsOnIndex is int dep) proposals[ordinal].DependsOnProposalId = -(dep + 1);
        return (reply, true);
    }

    /// <summary>A revision's search also looks for the passages its suggestions are about.</summary>
    private async Task<string> RevisedLabelsAsync(int bookId, BookChatMessage? author) =>
        author?.RevisesMessageId is int id && await Repo.GetChatMessageAsync(bookId, id) is { } revised
            ? "\n" + string.Join("\n", revised.Proposals.Select(p => p.Label))
            : "";

    /// <summary>A proposal with its preview: the target as it reads now and as the change would leave it.</summary>
    private async Task<ChatProposal> ToProposalAsync(int bookId, NormalizedAction action, int ordinal, IReadOnlyList<Chapter> chapters)
    {
        var p = action.Payload;
        var before = await _content.RenderAsync(bookId, action.Target);
        string after = p.Type switch
        {
            BookChatActions.UpdatePremise => p.Text ?? "",
            BookChatActions.UpdateStoryBible => BookContentRender.Json(BookContentRender.Merge(
                before.Length > 0 ? BookContentRender.Parse(before) : BookContentRender.Fields(new StoryBible()), p.Changes)),
            BookChatActions.UpsertCharacter => BookContentRender.Json(BookContentRender.Merge(
                before.Length > 0 ? BookContentRender.Parse(before) : BookContentRender.Fields(new CharacterCard { Name = p.Name ?? "" }), p.Changes)),
            BookChatActions.UpsertPlotThread => BookContentRender.Json(BookContentRender.Merge(
                before.Length > 0 ? BookContentRender.Parse(before) : BookContentRender.Fields(new PlotThread { Name = p.Name ?? "", Type = PlotThreadType.Subplot }), p.Changes)),
            BookChatActions.AddChapter => BookContentRender.Json(BookContentRender.Merge(
                BookContentRender.Fields(new Chapter { Number = p.ChapterNumber ?? 0 }), p.Changes)),
            BookChatActions.AddSection => BookContentRender.Json(BookContentRender.Merge(
                BookContentRender.PlanFields(new Section { Number = p.SectionNumber ?? 0 }), p.Changes)),
            BookChatActions.UpdateChapterOutline or BookChatActions.UpdateSectionPlan =>
                BookContentRender.Json(BookContentRender.Merge(BookContentRender.Parse(before), p.Changes)),
            BookChatActions.WriteSection => p.Text ?? "",
            BookChatActions.PatchChapter => PatchPreview(before, p.OriginalText ?? "", p.ReplacementText ?? "").After,
            BookChatActions.AddNote or BookChatActions.UpdateNote => p.Text ?? "",
            BookChatActions.SetPartRequests => p.Text ?? "",
            _ => "",   // archives: gone; write/revise: the Writer or Editor decides
        };
        if (p.Type == BookChatActions.PatchChapter) before = PatchPreview(before, p.OriginalText ?? "", p.ReplacementText ?? "").Before;
        // Long actions show the current prose only as context — keep the stored preview small.
        if (p.Type is BookChatActions.WriteChapter or BookChatActions.ReviseChapter) before = "";

        return new ChatProposal
        {
            BookId = bookId,
            Ordinal = ordinal,
            Type = p.Type,
            Label = action.Label.Length > 400 ? action.Label[..400] : action.Label,
            PayloadJson = p.ToJson(),
            Target = action.Target,
            OptionGroup = action.OptionGroup,
            PreviewBefore = before,
            PreviewAfter = after,
        };
    }

    /// <summary>The paragraph(s) a patch touches, before and after — not the whole chapter.</summary>
    internal static (string Before, string After) PatchPreview(string content, string original, string replacement)
    {
        var normalized = EditorAgent.NormalizeWhitespace(content);
        var match = EditorAgent.FindPatchLocation(normalized, new CheckerIssue("continuity", "", "", original, replacement));
        if (match.Offset < 0) return ("", "");
        var length = EditorAgent.NormalizeWhitespace(original).Length;
        var start = normalized.LastIndexOf("\n\n", match.Offset, StringComparison.Ordinal);
        start = start < 0 ? 0 : start + 2;
        var end = normalized.IndexOf("\n\n", match.Offset + length, StringComparison.Ordinal);
        end = end < 0 ? normalized.Length : end;
        var paragraph = normalized[start..end];
        var local = match.Offset - start;
        return (paragraph, paragraph.Remove(local, length).Insert(local, replacement));
    }

    /// <summary>
    /// Points each dependency at the saved proposal. Before saving, a dependency is held as −(ordinal + 1) of the
    /// proposal it needs, since ids only exist once the reply is saved.
    /// </summary>
    private async Task ResolveDependenciesAsync(BookChatMessage reply)
    {
        var byOrdinal = reply.Proposals.ToDictionary(p => p.Ordinal);
        foreach (var p in reply.Proposals)
            if (p.DependsOnProposalId is int placeholder and < 0 && byOrdinal.TryGetValue(-placeholder - 1, out var needed))
            {
                p.DependsOnProposalId = needed.Id;
                await Repo.UpdateProposalAsync(p);
            }
    }

    /// <summary>A newer pending proposal on the same target makes older pending ones obsolete.</summary>
    private async Task SupersedeOlderAsync(int bookId, BookChatMessage reply)
    {
        var targets = reply.Proposals.Select(p => p.Target).OfType<string>().ToHashSet();
        if (targets.Count == 0) return;
        foreach (var old in await Repo.GetProposalsByStatusAsync(bookId, ChatProposalStatus.Pending))
            if (old.MessageId != reply.Id && old.Target is { } t && targets.Contains(t)
                && t != BookContentService.NoteTarget && !t.StartsWith("prose:"))
            {
                old.Status = ChatProposalStatus.Superseded;
                old.ResultNote = "A later reply proposed another change to the same thing.";
                await Repo.UpdateProposalAsync(old);
            }
    }

    // ── Context ─────────────────────────────────────────────────────────────────

    /// <summary>The last few messages before <paramref name="beforeId"/>, as resent turns.</summary>
    private async Task<IReadOnlyList<(LlmChatRole Role, string Content)>> HistoryAsync(int bookId, int beforeId)
    {
        var recent = await Repo.GetChatMessagesAsync(bookId, beforeId, PromptTemplates.BookChatHistoryMessages);
        return recent.Where(InConversation).Select(Turn).ToList();
    }

    /// <summary>A failed call and the "reading…" note of a two-step reply are not part of the conversation.</summary>
    private static bool InConversation(BookChatMessage m) =>
        !(m.Role == BookChatRole.Event && (m.Content.StartsWith(FailedPrefix) || m.Content.StartsWith(ReadingPrefix)));

    /// <summary>
    /// The parts a two-step reply asked for, found in the book and rendered for the second request — at most
    /// <see cref="PromptTemplates.BookChatPartsMax"/>, within <paramref name="budget"/> characters (a chapter that does
    /// not fit is cut, or left out when little room is left). <c>Missing</c> says why a request was not served;
    /// <c>Labels</c> name the served parts for the author.
    /// </summary>
    internal static (List<PromptTemplates.BookChatPart> Parts, List<string> Missing, List<string> Labels) ResolveParts(
        IReadOnlyList<ChatPartRequest> requests, IReadOnlyList<Chapter> chapters, IReadOnlyList<CharacterCard> characters,
        IReadOnlyList<PlotThread> threads, int budget = PromptTemplates.BookChatPartsBudget, IReadOnlyList<Section>? sections = null)
    {
        var parts = new List<PromptTemplates.BookChatPart>();
        var missing = new List<string>();
        var labels = new List<string>();
        var used = 0;
        var seen = new HashSet<string>();
        foreach (var r in requests)
        {
            // A section plan may be asked for with its number in "chapter" (or as plain text "section_plan 2").
            var sectionNumber = r.Section ?? (r.Kind == BookChatActions.PartSectionPlan ? r.Chapter : null);
            var request = r.Kind == BookChatActions.PartSectionPlan && sectionNumber is int s ? $"{r.Kind} {s}"
                : r.Chapter is int n ? $"{r.Kind} {n}" : r.Name is { } name ? $"{r.Kind} {name}" : r.Kind;
            if (!seen.Add(request.ToLowerInvariant())) continue;
            if (parts.Count >= PromptTemplates.BookChatPartsMax)
            {
                missing.Add(PromptTemplates.BookChatPartMissing(request, PromptTemplates.BookChatPartOverLimit));
                continue;
            }

            PromptTemplates.BookChatPart? part = null;
            string? label = null, reason = null;
            switch (r.Kind)
            {
                case BookChatActions.PartChapterText or BookChatActions.PartChapterPlan:
                {
                    var chapter = r.Chapter is int number ? chapters.FirstOrDefault(c => c.Number == number) : null;
                    if (chapter is null) reason = PromptTemplates.BookChatPartNoChapter;
                    else if (r.Kind == BookChatActions.PartChapterPlan)
                        (part, label) = (PromptTemplates.BookChatChapterPlanPart(chapter), $"the plan of Chapter {chapter.Number}");
                    else if (chapter.Content.Trim().Length == 0) reason = PromptTemplates.BookChatPartNotWritten;
                    else
                    {
                        var room = budget - used;
                        var text = chapter.Content.Trim();
                        if (text.Length > room && room < 2_000) reason = PromptTemplates.BookChatPartOverBudget;
                        else
                        {
                            if (text.Length > room) text = text[..room] + PromptTemplates.BookChatPartCut;
                            (part, label) = (PromptTemplates.BookChatChapterTextPart(chapter, text), $"Chapter {chapter.Number}");
                        }
                    }
                    break;
                }
                case BookChatActions.PartSectionPlan:
                {
                    var section = sectionNumber is int number
                        ? sections?.FirstOrDefault(x => !x.IsArchived && x.Number == number)
                        : null;
                    if (section is null) reason = PromptTemplates.BookChatPartNoSection;
                    else (part, label) = (PromptTemplates.BookChatSectionPlanPart(section, chapters.Where(c => c.SectionId == section.Id).ToList()),
                        $"the plan of Section {section.Number}");
                    break;
                }
                case BookChatActions.PartCharacter:
                {
                    var card = characters.FirstOrDefault(c => c.Name.Equals(r.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (card is null) reason = PromptTemplates.BookChatPartNoCharacter;
                    else (part, label) = (PromptTemplates.BookChatCharacterPart(card), card.Name);
                    break;
                }
                case BookChatActions.PartPlotThread:
                {
                    var thread = threads.FirstOrDefault(t => t.Name.Equals(r.Name?.Trim(), StringComparison.OrdinalIgnoreCase));
                    if (thread is null) reason = PromptTemplates.BookChatPartNoThread;
                    else (part, label) = (PromptTemplates.BookChatThreadPart(thread), $"the thread {thread.Name}");
                    break;
                }
                default:
                    reason = PromptTemplates.BookChatPartUnknownKind;
                    break;
            }

            if (part is not null && used + part.Body.Length > budget && r.Kind != BookChatActions.PartChapterText)
                (part, reason) = (null, PromptTemplates.BookChatPartOverBudget);
            if (part is null)
            {
                missing.Add(PromptTemplates.BookChatPartMissing(request, reason ?? PromptTemplates.BookChatPartUnknownKind));
                continue;
            }
            parts.Add(part);
            labels.Add(label!);
            used += part.Body.Length;
        }
        return (parts, missing, labels);
    }

    internal static (LlmChatRole Role, string Content) Turn(BookChatMessage m)
    {
        var content = m.Role switch
        {
            BookChatRole.Assistant => PromptTemplates.BookChatPastReply(StripNotes(m.Content),
                m.Proposals.OrderBy(p => p.Ordinal).Select(p => (p.Label, p.Status.ToString()))),
            BookChatRole.Event => PromptTemplates.BookChatPastEvent(m.Content),
            BookChatRole.Author when m.RevisesMessageId is not null => PromptTemplates.BookChatPastRevision(m.Content),
            _ => m.Content,
        };
        if (content.Length > PromptTemplates.BookChatHistoryMessageChars)
            content = content[..PromptTemplates.BookChatHistoryMessageChars] + " …";
        return (m.Role == BookChatRole.Assistant ? LlmChatRole.Assistant : LlmChatRole.User, content);
    }

    /// <summary>Drops the ⚠️ notes the app appended to a reply — they are for the author, not the model.</summary>
    private static string StripNotes(string content)
    {
        var i = content.IndexOf("\n\n⚠️", StringComparison.Ordinal);
        return i < 0 ? content : content[..i];
    }

    /// <summary>System prompt, the resent turns, then the author's turn — merging same-role neighbours, which some providers reject.</summary>
    internal static List<LlmChatMessage> BuildMessages(string system, IReadOnlyList<(LlmChatRole Role, string Content)> history, string authorTurn)
    {
        var messages = new List<LlmChatMessage> { new(LlmChatRole.System, system) };
        foreach (var (role, content) in history.Append((LlmChatRole.User, authorTurn)))
        {
            // A conversation must open with the author.
            if (messages.Count == 1 && role == LlmChatRole.Assistant) continue;
            if (messages[^1].Role == role)
                messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n\n" + content };
            else
                messages.Add(new LlmChatMessage(role, content));
        }
        return messages;
    }

    /// <summary>
    /// Folds the messages that left the resent window into the rolling memory, once enough of them have piled up —
    /// so the request stays the same size however long the chat gets.
    /// </summary>
    private async Task UpdateMemoryAsync(int bookId, CancellationToken ct)
    {
        var book = await Repo.GetByIdAsync(bookId);
        if (book is null) return;
        var all = await Repo.GetChatMessagesAsync(bookId);
        var outside = all.Take(Math.Max(0, all.Count - PromptTemplates.BookChatHistoryMessages - 1))
            .Where(m => m.Id > (book.ChatMemoryThroughMessageId ?? 0))
            .ToList();
        var through = outside.Count > 0 ? outside[^1].Id : 0;
        outside = outside.Where(InConversation).ToList();
        if (outside.Count < PromptTemplates.BookChatMemoryBatch) return;

        var transcript = outside.Select(m =>
        {
            var (role, content) = Turn(m);
            return $"{(m.Role == BookChatRole.Event ? "Event" : role == LlmChatRole.User ? "Author" : "Co-author")}: {content}";
        });
        var (client, config) = await GetChatClientAsync(bookId);
        var memory = await StreamResponseAsync(client, config,
            [
                new(LlmChatRole.System, PromptTemplates.BookChatMemorySystem),
                new(LlmChatRole.User, PromptTemplates.BookChatMemoryUser(book.ChatMemory, transcript)),
            ],
            bookId, null, AgentRole.BookChat, ct, suspiciousThreshold: 0, forwardToUi: false);
        if (memory.Trim().Length == 0) return;

        book = await Repo.GetByIdAsync(bookId) ?? book;
        book.ChatMemory = memory.Trim();
        book.ChatMemoryThroughMessageId = through;
        await Repo.UpdateAsync(book);
    }
}
