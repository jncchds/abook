using System.Text.Json;
using System.Text.RegularExpressions;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>The part of the book a human-assisted pause lets the author discuss.</summary>
public enum ChatSection
{
    StoryBible, Characters, PlotThreads,
    /// <summary>The whole outline list, after the Chapter Outlines phase.</summary>
    ChapterOutlines,
    /// <summary>A chapter's prose, before its creative rewrite.</summary>
    Chapter,
    /// <summary>One chapter's outline, after the pre-write check and before the Writer.</summary>
    ChapterOutline,
}

/// <summary>The section a pause is about; <see cref="ChapterId"/> is set for the per-chapter sections.</summary>
public sealed record SectionChatTarget(ChatSection Section, int? ChapterId)
{
    /// <summary>
    /// The section a pause question is about, or null when the question is not a human-assisted section
    /// pause. Those are the optional questions asked after each planning phase and before a chapter's
    /// creative rewrite (see <c>AgentOrchestrator</c>).
    /// </summary>
    public static SectionChatTarget? FromPause(AgentMessage question)
    {
        if (question is not { MessageType: MessageType.Question, IsOptional: true }) return null;
        return question.AgentRole switch
        {
            AgentRole.StoryBibleAgent  => new(ChatSection.StoryBible, null),
            AgentRole.CharactersAgent  => new(ChatSection.Characters, null),
            AgentRole.PlotThreadsAgent => new(ChatSection.PlotThreads, null),
            AgentRole.Planner when question.ChapterId is null => new(ChatSection.ChapterOutlines, null),
            AgentRole.Editor when question.ChapterId is int id => new(ChatSection.Chapter, id),
            AgentRole.Writer when question.ChapterId is int id => new(ChatSection.ChapterOutline, id),
            _ => null,
        };
    }
}

/// <summary>
/// One change the section chat made to the section: the section rendered before and after it, and the
/// reply that made it. Stored as a <see cref="MessageType.ChatChange"/> message; <see cref="Id"/> is that message's id.
/// </summary>
public sealed record SectionChatChange(int Id, int ReplyId, string Before, string After)
{
    private sealed record Stored(int ReplyId, string Before, string After);

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string ToJson() => JsonSerializer.Serialize(new Stored(ReplyId, Before, After), Options);

    /// <summary>Null when the message does not hold a readable change.</summary>
    public static SectionChatChange? FromMessage(AgentMessage message)
    {
        try
        {
            var s = JsonSerializer.Deserialize<Stored>(message.Content, Options);
            return s is null ? null : new SectionChatChange(message.Id, s.ReplyId, s.Before ?? "", s.After ?? "");
        }
        catch (JsonException) { return null; }
    }
}

/// <summary>
/// The chat the author holds with the LLM about one section while a human-assisted run is paused.
/// Every call sends the whole conversation: the section as it stood when the chat began, every
/// message back and forth, and — when the section has changed since — its current version. The
/// model answers and, when asked for a change, returns the complete updated section, which is
/// saved straight away (with history) so the author sees it on the page and the run's next step
/// reads it. The pause itself is only resolved when the author presses Complete.
/// </summary>
public class SectionChatAgent : AgentBase
{
    private static readonly Regex UpdateMarkerRegex = new(
        @"^[ \t]*" + Regex.Escape(PromptTemplates.SectionChatUpdateMarker) + @"[ \t]*\r?$",
        RegexOptions.Multiline);

    private const string FailedReplyPrefix = "❌";
    private const string UndoReplyPrefix = "↩️";

    private static readonly JsonSerializerOptions SectionJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public SectionChatAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>The chat messages (author's and LLM's) of one pause, oldest first. The context message is excluded.</summary>
    public static IEnumerable<AgentMessage> Conversation(IEnumerable<AgentMessage> messages, int questionMessageId) =>
        messages.Where(m => m.QuestionMessageId == questionMessageId
                            && m.MessageType is MessageType.ChatUser or MessageType.ChatReply)
                .OrderBy(m => m.Id);

    /// <summary>The changes the chat made to the section, oldest first.</summary>
    public static IEnumerable<SectionChatChange> Changes(IEnumerable<AgentMessage> messages, int questionMessageId) =>
        messages.Where(m => m.QuestionMessageId == questionMessageId && m.MessageType == MessageType.ChatChange)
                .OrderBy(m => m.Id)
                .Select(m => SectionChatChange.FromMessage(m))
                .OfType<SectionChatChange>();

    /// <summary>Everything one LLM call about the section needs.</summary>
    private sealed record Turn(
        string Label, Rendered Current, AgentMessage Context, List<AgentMessage> History,
        string System, ILlmChatClient Client, LlmConfiguration Config);

    /// <summary>
    /// Reads the section and the conversation of the pause and builds the system prompt. The context message
    /// (the section as it stood when the chat began) is saved on the first call.
    /// </summary>
    private async Task<Turn> PrepareAsync(int bookId, AgentMessage pause, SectionChatTarget target, CancellationToken ct)
    {
        var all = (await Repo.GetMessagesAsync(bookId)).Where(m => m.QuestionMessageId == pause.Id).ToList();
        var current = await RenderAsync(bookId, target);
        var label = Label(target, current.Chapter);

        var context = all.FirstOrDefault(m => m.MessageType == MessageType.ChatContext)
            ?? await AddChatMessageAsync(bookId, pause, MessageType.ChatContext, current.Text);

        var book = await Repo.GetByIdWithActiveDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var (client, config) = await GetChatClientAsync(bookId);
        var system = PromptTemplates.SectionChatSystem(book, label, UpdateFormat(target, book),
            await BackgroundAsync(bookId, target, current.Chapter, ct));
        return new Turn(label, current, context, Conversation(all, pause.Id).ToList(), system, client, config);
    }

    /// <summary>Saves the author's message, asks the LLM, saves its reply and applies any update it returned.</summary>
    public async Task ReplyAsync(int bookId, AgentMessage pause, SectionChatTarget target, string authorText, CancellationToken ct)
    {
        var turn = await PrepareAsync(bookId, pause, target, ct);
        await AddChatMessageAsync(bookId, pause, MessageType.ChatUser, authorText);
        await Notifier.NotifySectionChatAsync(bookId, pause.Id, busy: true, ct);

        var messages = BuildMessages(turn.System, turn.Label, turn.Context.Content, turn.Current.Text, turn.History, authorText);

        string reply;
        (string Before, string After)? change = null;
        try
        {
            var raw = await StreamResponseAsync(turn.Client, turn.Config, messages, bookId, target.ChapterId, AgentRole.SectionChat, ct,
                suspiciousThreshold: 0, stopStreamingAt: UpdateMarkerRegex);
            var (text, update) = SplitResponse(raw);
            reply = text.Length > 0 ? text : update is not null ? "Done." : "(no reply)";
            if (update is not null)
            {
                // Read again: the author may have edited the section while the model was writing.
                var before = (await RenderAsync(bookId, target)).Text;
                var (note, applied) = await ApplyUpdateAsync(bookId, target, update, ct);
                reply += "\n\n" + note;
                if (applied) change = (before, (await RenderAsync(bookId, target)).Text);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Section chat reply failed.", bookId);
            reply = $"{FailedReplyPrefix} The LLM call failed: {DescribeFailure(ex, false, turn.Config.TimeoutMs)}";
        }

        var saved = await AddChatMessageAsync(bookId, pause, MessageType.ChatReply, reply);
        if (change is { } c && c.Before != c.After)
            await AddChangeAsync(bookId, pause, saved.Id, c.Before, c.After);
    }

    /// <summary>
    /// Restores the section to what it was before the chat's latest change. The undo is itself recorded as a
    /// change, so undoing again redoes it. Edits the author made by hand since that change are replaced too.
    /// </summary>
    public async Task UndoAsync(int bookId, AgentMessage pause, SectionChatTarget target, CancellationToken ct)
    {
        var last = Changes(await Repo.GetMessagesAsync(bookId), pause.Id).LastOrDefault()
            ?? throw new InvalidOperationException("The chat has not changed the section yet.");
        await Notifier.NotifySectionChatAsync(bookId, pause.Id, busy: true, ct);

        var before = (await RenderAsync(bookId, target)).Text;
        var (note, applied) = await ApplyUpdateAsync(bookId, target, last.Before, ct);
        var saved = await AddChatMessageAsync(bookId, pause, MessageType.ChatReply,
            applied ? $"{UndoReplyPrefix} _Undid the last change._" : note);
        if (applied)
        {
            var after = (await RenderAsync(bookId, target)).Text;
            if (after != before) await AddChangeAsync(bookId, pause, saved.Id, before, after);
        }
    }

    /// <summary>True when the section still reads as the chat's latest change left it (no hand edits since).</summary>
    public async Task<bool> MatchesLatestChangeAsync(int bookId, int questionMessageId, SectionChatTarget target)
    {
        var last = Changes(await Repo.GetMessagesAsync(bookId), questionMessageId).LastOrDefault();
        return last is null || (await RenderAsync(bookId, target)).Text == last.After;
    }

    /// <summary>
    /// Drafts a note of the decisions the author made in the discussion, for later parts of the book. Empty
    /// when the chat is empty or holds nothing worth keeping. Not streamed — the author edits it before saving.
    /// </summary>
    public async Task<string> SummarizeAsync(int bookId, AgentMessage pause, SectionChatTarget target, CancellationToken ct)
    {
        var turn = await PrepareAsync(bookId, pause, target, ct);
        if (!turn.History.Any(m => m.MessageType == MessageType.ChatUser)) return string.Empty;

        // The request goes in as a last author turn after the whole conversation.
        var messages = BuildMessages(turn.System, turn.Label, turn.Context.Content, turn.Current.Text, turn.History,
            PromptTemplates.SectionChatSummaryRequest(turn.Label));
        var raw = await StreamResponseAsync(turn.Client, turn.Config, messages, bookId, target.ChapterId, AgentRole.SectionChat, ct,
            suspiciousThreshold: 0, forwardToUi: false);
        var note = SplitResponse(raw).Reply.Trim();
        return IsNothingToKeep(note) ? string.Empty : note;
    }

    internal static bool IsNothingToKeep(string note) =>
        note.Length == 0 || note.Trim('.', '*', '"', ' ').Equals(PromptTemplates.SectionChatNothingToKeep, StringComparison.OrdinalIgnoreCase);

    /// <summary>Saves the author's (edited) note as a clarification, so every later prompt carries it.</summary>
    public async Task SaveSummaryAsync(int bookId, AgentMessage pause, SectionChatTarget target, string note)
    {
        Chapter? chapter = target.ChapterId is not null ? await LoadChapterAsync(bookId, target) : null;
        await Repo.AddClarificationAsync(new Clarification
        {
            BookId = bookId,
            Source = ClarificationSource.ChatSummary,
            Question = PromptTemplates.SectionChatSummaryQuestion(ShortLabel(target, chapter)),
            Answer = note.Trim(),
            QuestionMessageId = pause.Id,
        });
        await Notifier.NotifyMessagesUpdatedAsync(bookId);   // the UI re-reads clarifications on this event
    }

    private Task AddChangeAsync(int bookId, AgentMessage pause, int replyId, string before, string after) =>
        AddChatMessageAsync(bookId, pause, MessageType.ChatChange, new SectionChatChange(0, replyId, before, after).ToJson());

    /// <summary>
    /// The prompt for one turn. The first author message carries the original section; the latest one
    /// carries the current version when it differs from the original, so the model never edits stale text.
    /// </summary>
    internal static List<LlmChatMessage> BuildMessages(
        string system, string label, string original, string current,
        IReadOnlyList<AgentMessage> history, string authorText)
    {
        var turns = history
            .Where(m => !m.Content.StartsWith(FailedReplyPrefix))   // failed calls are not part of the conversation
            .Select(m => (Role: m.MessageType == MessageType.ChatUser ? LlmChatRole.User : LlmChatRole.Assistant, m.Content))
            .Append((Role: LlmChatRole.User, Content: authorText))
            .ToList();

        var messages = new List<LlmChatMessage> { new(LlmChatRole.System, system) };
        for (int i = 0; i < turns.Count; i++)
        {
            var (role, content) = turns[i];
            bool first = i == 0, last = i == turns.Count - 1;
            if (first)
                // A first turn that is also the last one starts from what is saved now.
                content = PromptTemplates.SectionChatOpening(label, last ? current : original, content);
            else if (last && current != original)
                content = PromptTemplates.SectionChatWithCurrent(label, current, content);

            // Some providers reject two turns of the same role in a row (a reply that was never saved).
            if (messages[^1].Role == role && role != LlmChatRole.System)
                messages[^1] = messages[^1] with { Content = messages[^1].Content + "\n\n" + content };
            else
                messages.Add(new LlmChatMessage(role, content));
        }
        return messages;
    }

    /// <summary>Splits a response into the reply shown to the author and the updated section (null when unchanged).</summary>
    internal static (string Reply, string? Update) SplitResponse(string raw)
    {
        var match = UpdateMarkerRegex.Match(raw);
        if (!match.Success) return (raw.Trim(), null);
        var update = StripFences(raw[(match.Index + match.Length)..]).Trim();
        return (raw[..match.Index].Trim(), update.Length > 0 ? update : null);
    }

    private static string StripFences(string text)
    {
        var t = text.Trim();
        if (!t.StartsWith("```")) return t;
        var lines = t.Split('\n').Skip(1).ToList();
        if (lines.Count > 0 && lines[^1].TrimEnd().StartsWith("```")) lines.RemoveAt(lines.Count - 1);
        return string.Join('\n', lines);
    }

    // ── Rendering ───────────────────────────────────────────────────────────────

    internal sealed record Rendered(string Text, Chapter? Chapter);

    /// <summary>The section as the model sees it: JSON in the planning agents' shape, or the chapter's prose.</summary>
    private async Task<Rendered> RenderAsync(int bookId, SectionChatTarget target)
    {
        switch (target.Section)
        {
            case ChatSection.StoryBible:
                var bible = await Repo.GetStoryBibleAsync(bookId);
                return new(Json(new
                {
                    settingDescription = bible?.SettingDescription ?? "",
                    timePeriod = bible?.TimePeriod ?? "",
                    themes = bible?.Themes ?? "",
                    toneAndStyle = bible?.ToneAndStyle ?? "",
                    worldRules = bible?.WorldRules ?? "",
                    notes = bible?.Notes ?? "",
                }), null);

            case ChatSection.Characters:
                return new(Json((await Repo.GetCharacterCardsAsync(bookId)).Select(c => new
                {
                    name = c.Name,
                    role = c.Role.ToString(),
                    physicalDescription = c.PhysicalDescription,
                    personality = c.Personality,
                    backstory = c.Backstory,
                    goalMotivation = c.GoalMotivation,
                    arc = c.Arc,
                    firstAppearanceChapterNumber = c.FirstAppearanceChapterNumber,
                    notes = c.Notes,
                })), null);

            case ChatSection.PlotThreads:
                return new(Json((await Repo.GetPlotThreadsAsync(bookId)).Select(t => new
                {
                    name = t.Name,
                    description = t.Description,
                    type = t.Type.ToString(),
                    introducedChapterNumber = t.IntroducedChapterNumber,
                    resolvedChapterNumber = t.ResolvedChapterNumber,
                    status = t.Status.ToString(),
                })), null);

            case ChatSection.ChapterOutlines:
                return new(Json((await Repo.GetChaptersAsync(bookId)).OrderBy(c => c.Number).Select(OutlineOf)), null);

            case ChatSection.ChapterOutline:
                var outlined = await LoadChapterAsync(bookId, target);
                return new(Json(OutlineOf(outlined)), outlined);

            default:
                var chapter = await LoadChapterAsync(bookId, target);
                return new(chapter.Content ?? "", chapter);
        }
    }

    /// <summary>A chapter's outline fields in the Chapter Outlines schema shape.</summary>
    private static object OutlineOf(Chapter c) => new
    {
        number = c.Number,
        title = c.Title,
        outline = c.Outline,
        povCharacter = c.PovCharacter,
        charactersInvolved = ParseNameList(c.CharactersInvolvedJson),
        plotThreads = ParseNameList(c.PlotThreadsJson),
        foreshadowingNotes = c.ForeshadowingNotes,
        payoffNotes = c.PayoffNotes,
    };

    private async Task<Chapter> LoadChapterAsync(int bookId, SectionChatTarget target) =>
        EnsureNotArchived(await Repo.GetChapterAsync(bookId, target.ChapterId!.Value)
            ?? throw new InvalidOperationException($"Chapter {target.ChapterId} not found."));

    private static string Json<T>(T value) => JsonSerializer.Serialize(value, SectionJsonOptions);

    internal static string Label(SectionChatTarget target, Chapter? chapter) => target.Section switch
    {
        ChatSection.StoryBible      => "Story Bible",
        ChatSection.Characters      => "character list",
        ChatSection.PlotThreads     => "plot thread list",
        ChatSection.ChapterOutlines => "chapter outline list",
        ChatSection.ChapterOutline  => chapter is null ? "chapter outline" : $"outline of Chapter {chapter.Number} (\"{chapter.Title}\")",
        _ => chapter is null ? "chapter" : $"text of Chapter {chapter.Number} (\"{chapter.Title}\")",
    };

    /// <summary>The section's name as shown to the author ("Characters", "Chapter 3").</summary>
    internal static string ShortLabel(SectionChatTarget target, Chapter? chapter) => target.Section switch
    {
        ChatSection.StoryBible      => "Story Bible",
        ChatSection.Characters      => "Characters",
        ChatSection.PlotThreads     => "Plot Threads",
        ChatSection.ChapterOutlines => "Chapter Outlines",
        ChatSection.ChapterOutline  => chapter is null ? "Chapter outline" : $"Chapter {chapter.Number} outline",
        _ => chapter is null ? "Chapter" : $"Chapter {chapter.Number}",
    };

    /// <summary>
    /// Archived rows (id, name) to restore: one per incoming name that is not active but was archived —
    /// the most recently created one when the name was archived more than once.
    /// </summary>
    internal static IEnumerable<int> Returning(
        IEnumerable<string> incoming, IEnumerable<string> active, IEnumerable<(int Id, string Name)> archived)
    {
        var activeNames = active.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var latestArchived = archived
            .GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Max(a => a.Id), StringComparer.OrdinalIgnoreCase);
        return incoming
            .Where(n => !activeNames.Contains(n))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(latestArchived.ContainsKey)
            .Select(n => latestArchived[n]);
    }

    private static string UpdateFormat(SectionChatTarget target, Book book) => target.Section switch
    {
        ChatSection.StoryBible      => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatStoryBibleRemoval),
        ChatSection.Characters      => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatCharactersRemoval),
        ChatSection.PlotThreads     => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatPlotThreadsRemoval),
        ChatSection.ChapterOutlines => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatOutlinesRemoval),
        ChatSection.ChapterOutline  => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatOneOutlineRemoval),
        _                           => PromptTemplates.SectionChatProseFormat(book),
    };

    /// <summary>The rest of the plan the section has to stay consistent with.</summary>
    private async Task<string> BackgroundAsync(int bookId, SectionChatTarget target, Chapter? chapter, CancellationToken ct)
    {
        if (target.Section == ChatSection.Chapter && chapter is not null)
            return PromptTemplates.SectionChatChapterBackground(chapter, await BuildChapterSynopsesAsync(bookId, chapter.Number, ct));

        var bible = target.Section == ChatSection.StoryBible ? null : await Repo.GetStoryBibleAsync(bookId);
        var cast = target.Section is ChatSection.PlotThreads or ChatSection.ChapterOutlines or ChatSection.ChapterOutline
            ? await Repo.GetCharacterCardsAsync(bookId)
            : null;
        var plan = PromptTemplates.SectionChatPlanBackground(bible, cast);

        if (target.Section == ChatSection.ChapterOutline && chapter is not null)
            return PromptTemplates.SectionChatOutlineBackground(plan,
                await BuildChapterSynopsesAsync(bookId, chapter.Number, ct),
                (await Repo.GetPlotThreadsAsync(bookId)).Select(t => t.Name));
        return plan;
    }

    // ── Applying an update ──────────────────────────────────────────────────────

    /// <summary>
    /// Saves the updated section (in the shape <see cref="RenderAsync"/> produces) and returns the note
    /// appended to the reply — what changed, or why nothing was.
    /// </summary>
    private async Task<(string Note, bool Applied)> ApplyUpdateAsync(int bookId, SectionChatTarget target, string update, CancellationToken ct)
    {
        try
        {
            var note = target.Section switch
            {
                ChatSection.StoryBible      => await ApplyStoryBibleAsync(bookId, update),
                ChatSection.Characters      => await ApplyCharactersAsync(bookId, update),
                ChatSection.PlotThreads     => await ApplyPlotThreadsAsync(bookId, update),
                ChatSection.ChapterOutlines => await ApplyOutlinesAsync(bookId, update),
                ChatSection.ChapterOutline  => await ApplyChapterOutlineAsync(bookId, target, update, ct),
                _                           => await ApplyChapterAsync(bookId, target, update, ct),
            };
            return (note, true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Section chat update could not be applied.", bookId);
            return ($"⚠️ _The update could not be applied, nothing was changed: {ex.Message}_", false);
        }
    }

    private async Task<string> ApplyStoryBibleAsync(int bookId, string update)
    {
        var bible = StoryBibleAgent.ParseStoryBible(bookId, update);
        var existing = await Repo.GetStoryBibleAsync(bookId);
        if (existing is not null)
            await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, existing, "chat-overwrite"));
        var saved = await Repo.UpsertStoryBibleAsync(bible);
        await Repo.AddStoryBibleSnapshotAsync(BibleSnapshot(bookId, saved, "chat-revised"));
        return "✏️ _Updated the Story Bible._";
    }

    private static StoryBibleSnapshot BibleSnapshot(int bookId, StoryBible b, string reason) => new()
    {
        BookId = bookId,
        SettingDescription = b.SettingDescription,
        TimePeriod = b.TimePeriod,
        Themes = b.Themes,
        ToneAndStyle = b.ToneAndStyle,
        WorldRules = b.WorldRules,
        Notes = b.Notes,
        Reason = reason,
    };

    /// <summary>
    /// Merges by name, so kept characters keep their id, portraits and visual descriptions; characters the
    /// model left out are archived rather than deleted, so the author can restore them.
    /// </summary>
    private async Task<string> ApplyCharactersAsync(int bookId, string update)
    {
        var cards = CharactersAgent.Parse(bookId, update);
        var all = (await Repo.GetAllCharacterCardsAsync(bookId)).ToList();
        var existing = all.Where(c => !c.IsArchived).ToList();
        await Repo.AddCharactersSnapshotAsync(new CharactersSnapshot
        {
            BookId = bookId, DataJson = JsonSerializer.Serialize(existing), Reason = "chat-overwrite", Source = "chat-overwrite",
        });

        // A character coming back (e.g. on Undo) gets its archived card back, with its portrait.
        foreach (var id in Returning(cards.Select(c => c.Name), existing.Select(c => c.Name),
                     all.Where(c => c.IsArchived).Select(c => (c.Id, c.Name))))
            await Repo.UnarchiveCharacterCardAsync(bookId, id);

        var saved = await Repo.MergeCharacterCardsAsync(bookId, cards);
        var keep = cards.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = existing.Where(c => !keep.Contains(c.Name)).ToList();
        foreach (var card in dropped)
            await Repo.ArchiveCharacterCardAsync(bookId, card.Id);
        await Repo.AddCharacterVersionsBatchAsync(CharactersAgent.VersionsOf(bookId, saved, AgentCreatedBy.SectionChat));
        await Repo.AddCharactersSnapshotAsync(new CharactersSnapshot
        {
            BookId = bookId, DataJson = JsonSerializer.Serialize(saved), Reason = "chat-revised", Source = "chat-revised",
        });

        return "✏️ _Updated the characters" +
               (dropped.Count > 0 ? $"; archived {string.Join(", ", dropped.Select(c => c.Name))}" : "") + "._";
    }

    /// <summary>Same merge-and-archive rule as <see cref="ApplyCharactersAsync"/>.</summary>
    private async Task<string> ApplyPlotThreadsAsync(int bookId, string update)
    {
        var threads = PlotThreadsAgent.Parse(bookId, update);
        var all = (await Repo.GetAllPlotThreadsAsync(bookId)).ToList();
        var existing = all.Where(t => !t.IsArchived).ToList();
        await Repo.AddPlotThreadsSnapshotAsync(new PlotThreadsSnapshot
        {
            BookId = bookId, DataJson = JsonSerializer.Serialize(existing), Reason = "chat-overwrite", Source = "chat-overwrite",
        });

        foreach (var id in Returning(threads.Select(t => t.Name), existing.Select(t => t.Name),
                     all.Where(t => t.IsArchived).Select(t => (t.Id, t.Name))))
            await Repo.UnarchivePlotThreadAsync(bookId, id);

        var saved = await Repo.MergePlotThreadsAsync(bookId, threads);
        var keep = threads.Select(t => t.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dropped = existing.Where(t => !keep.Contains(t.Name)).ToList();
        foreach (var thread in dropped)
            await Repo.ArchivePlotThreadAsync(bookId, thread.Id);
        await Repo.AddPlotThreadVersionsBatchAsync(PlotThreadsAgent.VersionsOf(bookId, saved, AgentCreatedBy.SectionChat));
        await Repo.AddPlotThreadsSnapshotAsync(new PlotThreadsSnapshot
        {
            BookId = bookId, DataJson = JsonSerializer.Serialize(saved), Reason = "chat-revised", Source = "chat-revised",
        });

        return "✏️ _Updated the plot threads" +
               (dropped.Count > 0 ? $"; archived {string.Join(", ", dropped.Select(t => t.Name))}" : "") + "._";
    }

    /// <summary>
    /// Replaces the outlines like a re-plan does, but never removes a chapter that already has prose —
    /// when the model left one of those out, the outlines are merged instead.
    /// </summary>
    private async Task<string> ApplyOutlinesAsync(int bookId, string update)
    {
        var chapters = PlannerAgent.ParseChapterOutlines(bookId, update);
        var existing = (await Repo.GetChaptersAsync(bookId)).ToList();
        var numbers = chapters.Select(c => c.Number).ToHashSet();
        var missing = existing.Where(c => !numbers.Contains(c.Number)).ToList();
        var written = missing.Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();

        if (written.Count > 0)
        {
            await Repo.MergeChapterOutlinesAsync(bookId, chapters);
            return "✏️ _Updated the chapter outlines. Kept chapter(s) " +
                   string.Join(", ", written.Select(c => c.Number)) + " — they already have prose; archive them yourself to remove them._";
        }

        await Repo.ReplaceChaptersAsync(bookId, chapters);
        return "✏️ _Updated the chapter outlines" +
               (missing.Count > 0 ? $"; removed chapter(s) {string.Join(", ", missing.Select(c => c.Number))}" : "") + "._";
    }

    /// <summary>Updates one chapter's outline fields in place; its number, prose and status stay as they are.</summary>
    private async Task<string> ApplyChapterOutlineAsync(int bookId, SectionChatTarget target, string update, CancellationToken ct)
    {
        var json = update.TrimStart().StartsWith('{') ? $"[{update}]" : update;
        var parsed = PlannerAgent.ParseChapterOutlines(bookId, json).First();
        var chapter = await LoadChapterAsync(bookId, target);

        chapter.Title = parsed.Title.Length > 0 ? parsed.Title : chapter.Title;
        chapter.Outline = parsed.Outline;
        chapter.PovCharacter = parsed.PovCharacter;
        chapter.CharactersInvolvedJson = parsed.CharactersInvolvedJson;
        chapter.PlotThreadsJson = parsed.PlotThreadsJson;
        chapter.ForeshadowingNotes = parsed.ForeshadowingNotes;
        chapter.PayoffNotes = parsed.PayoffNotes;
        await Repo.UpdateChapterAsync(chapter);

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id, ct);
        return $"✏️ _Updated the outline of Chapter {chapter.Number}._";
    }

    private async Task<string> ApplyChapterAsync(int bookId, SectionChatTarget target, string update, CancellationToken ct)
    {
        var chapter = await LoadChapterAsync(bookId, target);
        var content = StripLeadingChapterHeading(update, chapter.Number, chapter.Title);
        if (content.Trim().Length == 0)
            throw new FormatException("the updated chapter was empty.");

        var version = CreateChapterVersion(chapter, content, chapter.Status, AgentCreatedBy.SectionChat);
        await Repo.AddChapterVersionAsync(version);

        var (_, config) = await GetChatClientAsync(bookId);
        try { await IndexChapterAsync(bookId, chapter.Id, version.Id, config, ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { Logger.LogWarning(ex, "[Book {BookId}] Indexing the chat-revised chapter failed.", bookId); }

        await Notifier.NotifyChapterUpdatedAsync(bookId, chapter.Id, ct);
        return $"✏️ _Updated Chapter {chapter.Number} (saved as version {version.VersionNumber})._";
    }

    private Task<AgentMessage> AddChatMessageAsync(int bookId, AgentMessage pause, MessageType type, string content) =>
        Repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId,
            ChapterId = pause.ChapterId,
            AgentRole = AgentRole.SectionChat,
            MessageType = type,
            Content = content,
            IsResolved = true,
            QuestionMessageId = pause.Id,
        });
}
