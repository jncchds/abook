using System.Text.Json;
using System.Text.RegularExpressions;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>The part of the book a human-assisted pause lets the author discuss.</summary>
public enum ChatSection { StoryBible, Characters, PlotThreads, ChapterOutlines, Chapter }

/// <summary>The section a pause is about; <see cref="ChapterId"/> is set for <see cref="ChatSection.Chapter"/>.</summary>
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
            _ => null,
        };
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

    /// <summary>Saves the author's message, asks the LLM, saves its reply and applies any update it returned.</summary>
    public async Task ReplyAsync(int bookId, AgentMessage pause, SectionChatTarget target, string authorText, CancellationToken ct)
    {
        var all = (await Repo.GetMessagesAsync(bookId)).Where(m => m.QuestionMessageId == pause.Id).ToList();
        var current = await RenderAsync(bookId, target);
        var label = Label(target, current.Chapter);

        // The section as it stood when the chat began — the first thing the model reads on every turn.
        var context = all.FirstOrDefault(m => m.MessageType == MessageType.ChatContext)
            ?? await AddChatMessageAsync(bookId, pause, MessageType.ChatContext, current.Text);

        var history = Conversation(all, pause.Id).ToList();
        await AddChatMessageAsync(bookId, pause, MessageType.ChatUser, authorText);
        await Notifier.NotifySectionChatAsync(bookId, pause.Id, busy: true, ct);

        var book = await Repo.GetByIdWithActiveDetailsAsync(bookId)
            ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var (client, config) = await GetChatClientAsync(bookId);
        var system = PromptTemplates.SectionChatSystem(book, label, UpdateFormat(target, book),
            await BackgroundAsync(bookId, target, current.Chapter, ct));
        var messages = BuildMessages(system, label, context.Content, current.Text, history, authorText);

        string reply;
        try
        {
            var raw = await StreamResponseAsync(client, config, messages, bookId, target.ChapterId, AgentRole.SectionChat, ct,
                suspiciousThreshold: 0, stopStreamingAt: UpdateMarkerRegex);
            var (text, update) = SplitResponse(raw);
            reply = text.Length > 0 ? text : update is not null ? "Done." : "(no reply)";
            if (update is not null)
                reply += "\n\n" + await ApplyUpdateAsync(bookId, target, update, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Section chat reply failed.", bookId);
            reply = $"{FailedReplyPrefix} The LLM call failed: {DescribeFailure(ex, false, config.TimeoutMs)}";
        }

        await AddChatMessageAsync(bookId, pause, MessageType.ChatReply, reply);
    }

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
                return new(Json((await Repo.GetChaptersAsync(bookId)).OrderBy(c => c.Number).Select(c => new
                {
                    number = c.Number,
                    title = c.Title,
                    outline = c.Outline,
                    povCharacter = c.PovCharacter,
                    charactersInvolved = ParseNameList(c.CharactersInvolvedJson),
                    plotThreads = ParseNameList(c.PlotThreadsJson),
                    foreshadowingNotes = c.ForeshadowingNotes,
                    payoffNotes = c.PayoffNotes,
                })), null);

            default:
                var chapter = await LoadChapterAsync(bookId, target);
                return new(chapter.Content ?? "", chapter);
        }
    }

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
        _ => chapter is null ? "chapter" : $"text of Chapter {chapter.Number} (\"{chapter.Title}\")",
    };

    private static string UpdateFormat(SectionChatTarget target, Book book) => target.Section switch
    {
        ChatSection.StoryBible      => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatStoryBibleRemoval),
        ChatSection.Characters      => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatCharactersRemoval),
        ChatSection.PlotThreads     => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatPlotThreadsRemoval),
        ChatSection.ChapterOutlines => PromptTemplates.SectionChatJsonFormat(PromptTemplates.SectionChatOutlinesRemoval),
        _                           => PromptTemplates.SectionChatProseFormat(book),
    };

    /// <summary>The rest of the plan the section has to stay consistent with.</summary>
    private async Task<string> BackgroundAsync(int bookId, SectionChatTarget target, Chapter? chapter, CancellationToken ct)
    {
        if (target.Section == ChatSection.Chapter && chapter is not null)
            return PromptTemplates.SectionChatChapterBackground(chapter, await BuildChapterSynopsesAsync(bookId, chapter.Number, ct));

        var bible = target.Section == ChatSection.StoryBible ? null : await Repo.GetStoryBibleAsync(bookId);
        var cast = target.Section is ChatSection.PlotThreads or ChatSection.ChapterOutlines
            ? await Repo.GetCharacterCardsAsync(bookId)
            : null;
        return PromptTemplates.SectionChatPlanBackground(bible, cast);
    }

    // ── Applying an update ──────────────────────────────────────────────────────

    /// <summary>Saves the updated section and returns the note appended to the reply (success or why it failed).</summary>
    private async Task<string> ApplyUpdateAsync(int bookId, SectionChatTarget target, string update, CancellationToken ct)
    {
        try
        {
            return target.Section switch
            {
                ChatSection.StoryBible      => await ApplyStoryBibleAsync(bookId, update),
                ChatSection.Characters      => await ApplyCharactersAsync(bookId, update),
                ChatSection.PlotThreads     => await ApplyPlotThreadsAsync(bookId, update),
                ChatSection.ChapterOutlines => await ApplyOutlinesAsync(bookId, update),
                _                           => await ApplyChapterAsync(bookId, target, update, ct),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Section chat update could not be applied.", bookId);
            return $"⚠️ _The update could not be applied, nothing was changed: {ex.Message}_";
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
        var existing = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        await Repo.AddCharactersSnapshotAsync(new CharactersSnapshot
        {
            BookId = bookId, DataJson = JsonSerializer.Serialize(existing), Reason = "chat-overwrite", Source = "chat-overwrite",
        });

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
        var existing = (await Repo.GetPlotThreadsAsync(bookId)).ToList();
        await Repo.AddPlotThreadsSnapshotAsync(new PlotThreadsSnapshot
        {
            BookId = bookId, DataJson = JsonSerializer.Serialize(existing), Reason = "chat-overwrite", Source = "chat-overwrite",
        });

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
