using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ABook.Core.Models;

namespace ABook.Api.Services;

/// <summary>Everything the metadata export draws on. Lists may come in any order.</summary>
internal sealed class MetadataSources
{
    /// <summary>The book with its active (non-archived) chapters.</summary>
    public required Book Book { get; init; }
    public StoryBible? Bible { get; init; }
    public IReadOnlyList<BookSnapshot> BookSnapshots { get; init; } = [];
    public IReadOnlyList<StoryBibleSnapshot> BibleSnapshots { get; init; } = [];
    /// <summary>All character cards, archived included.</summary>
    public IReadOnlyList<CharacterCard> Characters { get; init; } = [];
    public IReadOnlyList<CharacterCardVersion> CharacterVersions { get; init; } = [];
    public IReadOnlyList<CharactersSnapshot> CharacterSnapshots { get; init; } = [];
    public IReadOnlyList<PlotThreadVersion> PlotThreadVersions { get; init; } = [];
    public IReadOnlyList<PlotThreadsSnapshot> PlotThreadSnapshots { get; init; } = [];
    /// <summary>Versions of the active chapters.</summary>
    public IReadOnlyList<ChapterVersion> ChapterVersions { get; init; } = [];
    /// <summary>All clarifications, archived included.</summary>
    public IReadOnlyList<Clarification> Clarifications { get; init; } = [];
    public IReadOnlyList<AgentMessage> Messages { get; init; } = [];
    public IReadOnlyList<TokenUsageRecord> TokenUsage { get; init; } = [];
    public IReadOnlyList<AgentRun> Runs { get; init; } = [];
    public IReadOnlyList<Illustration> Illustrations { get; init; } = [];
    /// <summary>Rendered images keyed by id. Only portraits are embedded — cover and scenes are part of the book.</summary>
    public IReadOnlyDictionary<int, BookImage> Images { get; init; } = new Dictionary<int, BookImage>();
    public IReadOnlyList<VisualAsset> VisualAssets { get; init; } = [];
}

/// <summary>
/// The metadata export: a single chronological log of everything that happened to a book — planning,
/// edits, agent conversation and reasoning, LLM calls, runs and illustration work. The book text and the
/// images shown in the book are left out; they are what the other export formats are for.
/// </summary>
internal static class MetadataExport
{
    private enum Cat { Planning, Writing, Conversation, Thinking, Llm, Runs, Art }

    private static readonly (Cat Cat, string Label)[] Categories =
    [
        (Cat.Planning,     "Planning"),
        (Cat.Writing,      "Chapter versions"),
        (Cat.Conversation, "Conversation"),
        (Cat.Thinking,     "Thinking"),
        (Cat.Art,          "Illustrations"),
        (Cat.Runs,         "Runs & steps"),
        (Cat.Llm,          "LLM calls"),
    ];

    /// <param name="Compact">One-line entry (LLM calls, workflow steps); <paramref name="Body"/> follows inline.</param>
    private sealed record Ev(
        DateTime At, Cat Cat, string Actor, string Title, string Body = "", string? Chapter = null, bool Compact = false);

    private const string Author = "Author";

    public static string Generate(MetadataSources s)
    {
        var book     = s.Book;
        var chapters = book.Chapters.Where(c => !c.IsArchived).OrderBy(c => c.Number).ToList();
        var chapterLabels = chapters.ToDictionary(c => c.Id, c => $"Ch. {c.Number}");
        string? ChapterLabel(int? id) =>
            id is not int i ? null : chapterLabels.TryGetValue(i, out var l) ? l : "archived chapter";

        var events = new List<Ev>();
        events.AddRange(BookEvents(book, s.BookSnapshots));
        var clarificationSources = ClarificationMessages(s.Clarifications, s.Messages);
        events.AddRange(ClarificationEvents(s.Clarifications, clarificationSources));
        events.AddRange(BibleEvents(s.Bible, s.BibleSnapshots));
        events.AddRange(SetSnapshotEvents(s.CharacterSnapshots.Select(x => (x.CreatedAt, x.Reason, x.DataJson)),
            "Characters", "character", Deserialize<CharacterCard>, c => c.Name, c => $"{c.Name} · {c.Role}", CharacterFields));
        events.AddRange(ItemVersionEvents(s.CharacterVersions, v => v.CharacterCardId, v => v.VersionNumber,
            v => v.CreatedBy, v => v.CreatedAt, v => v.Name, "Character", "agent:Characters", CharacterFields));
        events.AddRange(SetSnapshotEvents(s.PlotThreadSnapshots.Select(x => (x.CreatedAt, x.Reason, x.DataJson)),
            "Plot Threads", "plot thread", Deserialize<PlotThread>, t => t.Name, t => $"{t.Name} · {t.Type}", ThreadFields));
        events.AddRange(ItemVersionEvents(s.PlotThreadVersions, v => v.PlotThreadId, v => v.VersionNumber,
            v => v.CreatedBy, v => v.CreatedAt, v => v.Name, "Plot thread", "agent:PlotThreads", ThreadFields));
        events.AddRange(ChapterEvents(chapters, s.ChapterVersions));
        events.AddRange(IllustrationEvents(s, ChapterLabel));
        var coveredMessages = clarificationSources.Values
            .SelectMany(p => new[] { p.Question?.Id, p.Answer?.Id }).OfType<int>().ToHashSet();
        events.AddRange(MessageEvents(s.Messages.Where(m => !coveredMessages.Contains(m.Id)).ToList(), ChapterLabel));
        events.AddRange(RunEvents(s.Runs, ChapterLabel));
        events.AddRange(UsageEvents(s.TokenUsage, ChapterLabel));

        // Stable sort: same-instant events keep the logical order they were added in.
        var timeline = events.OrderBy(e => e.At).ToList();

        var html = new StringBuilder();
        html.Append("<!DOCTYPE html>\n");
        html.Append($"<html lang=\"{BookExportService.GetLang2(book.Language)}\">\n<head>\n");
        html.Append("  <meta charset=\"UTF-8\">\n");
        html.Append("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        html.Append($"  <title>{Esc(book.Title)} — Metadata</title>\n");
        html.Append("  <style>").Append(Css).Append("\n  </style>\n</head>\n<body>\n");
        AppendToolbar(html, timeline);
        html.Append("  <main>\n");
        html.Append("    <header class=\"doc-header\">\n");
        html.Append($"      <h1>{Esc(book.Title)}</h1>\n");
        html.Append("      <p class=\"subtitle\">How this book was made — every planning step, edit, conversation and LLM call in the order it happened. ");
        html.Append("The book text and the images shown in the book are not included. Times are UTC.</p>\n");
        html.Append("    </header>\n");
        AppendOverview(html, s, chapters, timeline);
        AppendTimeline(html, timeline);
        html.Append("  </main>\n  <script>\n").Append(Js).Append("\n  </script>\n</body>\n</html>\n");
        return html.ToString();
    }

    // ── Book details ──────────────────────────────────────────────────────────

    private static List<(string, string?)> BookFields(
        string title, string genre, string language, int target, bool humanAssisted, string premise) =>
    [
        ("Title", title), ("Genre", genre), ("Language", language), ("Target chapters", target.ToString()),
        ("Human-assisted", humanAssisted ? "yes" : "no"), ("Premise", premise),
    ];

    /// <summary>
    /// A book snapshot holds the values in force <em>before</em> the edit made at its timestamp, so the
    /// creation values are the oldest snapshot's and each edit runs from one snapshot to the next.
    /// </summary>
    private static IEnumerable<Ev> BookEvents(Book book, IReadOnlyList<BookSnapshot> snapshots)
    {
        var states = snapshots.OrderBy(x => x.CreatedAt)
            .Select(x => BookFields(x.Title, x.Genre, x.Language, x.TargetChapterCount, x.HumanAssisted, x.Premise))
            .Append(BookFields(book.Title, book.Genre, book.Language, book.TargetChapterCount, book.HumanAssisted, book.Premise))
            .ToList();

        var created = Fields(states[0]);
        if (book.BaseBookId is int baseId)
            created = $"<p class=\"note\">Continuation of book #{baseId} — its planning is inherited.</p>" + created;
        yield return new Ev(book.CreatedAt, Cat.Planning, Author, "Book created", created);

        var ordered = snapshots.OrderBy(x => x.CreatedAt).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var diff = Diff(states[i], states[i + 1]);
            if (diff.Length > 0) yield return new Ev(ordered[i].CreatedAt, Cat.Planning, Author, "Book details edited", diff);
        }
    }

    // ── Clarifications ────────────────────────────────────────────────────────

    /// <summary>
    /// The chat question and answer a clarification came from, by a recorded link only: a phase note stores its
    /// question's id (and the answer message points back at that question); an Imported clarification has the
    /// exact timestamps of its messages, copied by the import (question → <c>CreatedAt</c>, answer →
    /// <c>AnsweredAt</c>). Phase notes saved before the link existed have no entry.
    /// </summary>
    private static Dictionary<Clarification, (AgentMessage? Question, AgentMessage? Answer)> ClarificationMessages(
        IReadOnlyList<Clarification> clarifications, IReadOnlyList<AgentMessage> messages)
    {
        var byId = messages.GroupBy(m => m.Id).ToDictionary(g => g.Key, g => g.First());
        var answerTo = messages.Where(m => m.MessageType == MessageType.Answer && m.QuestionMessageId is not null)
            .GroupBy(m => m.QuestionMessageId!.Value).ToDictionary(g => g.Key, g => g.First());
        var questionsAt = messages.Where(m => m.MessageType == MessageType.Question)
            .GroupBy(m => m.CreatedAt).ToDictionary(g => g.Key, g => g.First());
        var answersAt = messages.Where(m => m.MessageType == MessageType.Answer)
            .GroupBy(m => m.CreatedAt).ToDictionary(g => g.Key, g => g.First());

        var result = new Dictionary<Clarification, (AgentMessage? Question, AgentMessage? Answer)>();
        foreach (var c in clarifications)
        {
            if (c.Source == ClarificationSource.PhaseNote && c.QuestionMessageId is int qid && byId.TryGetValue(qid, out var q))
                result[c] = (q, answerTo.GetValueOrDefault(qid));
            else if (c.Source == ClarificationSource.Imported)
                result[c] = (questionsAt.GetValueOrDefault(c.CreatedAt),
                             c.AnsweredAt is DateTime at ? answersAt.GetValueOrDefault(at) : null);
        }
        return result;
    }

    /// <summary>
    /// One entry per clarification, at the time it was raised, telling its whole story: the question, how the
    /// author answered (or skipped) it, and whether it was later edited or archived. A clarification that came
    /// from a chat question (imported, or a linked phase note) stands in for that question and its answer,
    /// which are left out of the timeline. Older phase notes without a link are already in the timeline as
    /// chat messages, so only their later edits and archiving get entries here. Only the latest wording is
    /// stored: an edit shows the text as it is now.
    /// </summary>
    private static IEnumerable<Ev> ClarificationEvents(
        IReadOnlyList<Clarification> clarifications,
        IReadOnlyDictionary<Clarification, (AgentMessage? Question, AgentMessage? Answer)> sources)
    {
        foreach (var c in clarifications.OrderBy(c => c.CreatedAt))
        {
            sources.TryGetValue(c, out var source);
            var askedAt = source.Question?.CreatedAt ?? c.CreatedAt;
            var settled = c.AnsweredAt ?? c.CreatedAt;
            var edited = !c.IsArchived && c.UpdatedAt > settled.AddSeconds(1);
            var aftermath = (c.IsArchived && c.AnsweredAt is not null ? Reply($"{Author} archived it", askedAt, c.UpdatedAt) : "")
                + (edited ? Reply("Edited later — shown as it reads now", askedAt, c.UpdatedAt) : "");

            var fromQuestion = c.Source is ClarificationSource.Planner or ClarificationSource.Imported
                || (c.Source == ClarificationSource.PhaseNote && source.Question is not null);
            if (fromQuestion)
            {
                var answeredAt = source.Answer?.CreatedAt ?? c.AnsweredAt;
                var outcome = answeredAt is DateTime at
                    ? Reply($"{Author} answered", askedAt, at) + Field("Answer", c.Answer)
                    : c.IsArchived
                        ? Reply($"{Author} skipped it", askedAt, c.UpdatedAt)
                        : "<p class=\"note\">Not answered yet.</p>";
                // A question from the chat may have come from a planning phase agent rather than the planner.
                var asker = source.Question?.AgentRole ?? AgentRole.Planner;
                yield return new Ev(askedAt, Cat.Conversation, Role(asker), "Asked a question",
                    Field("Question", c.Question) + outcome + aftermath);
                continue;
            }

            switch (c.Source)
            {
                case ClarificationSource.Author:
                    yield return new Ev(c.CreatedAt, Cat.Planning, Author, "Added a clarification",
                        ClarificationQa(c) + aftermath);
                    break;
                case ClarificationSource.ChatSummary:
                    yield return new Ev(c.CreatedAt, Cat.Planning, Author, "Kept the decisions of a section chat",
                        ClarificationQa(c) + aftermath);
                    break;
                default:
                    if (c.IsArchived)
                        yield return new Ev(c.UpdatedAt, Cat.Planning, Author, "Archived a clarification", ClarificationQa(c));
                    else if (edited)
                        yield return new Ev(c.UpdatedAt, Cat.Planning, Author, "Edited a clarification",
                            ClarificationQa(c) + "<p class=\"note\">Shown as it reads now.</p>");
                    break;
            }
        }
    }

    /// <summary>A labelled value with the label above it.</summary>
    private static string Field(string label, string? value) => Fields([(label, value)], stacked: true);

    /// <summary>
    /// A later step inside an entry ("Author answered · 14:34:58"); the date is added when it is not the
    /// day the entry is filed under.
    /// </summary>
    private static string Reply(string what, DateTime entryAt, DateTime at) =>
        $"<p class=\"reply\">{Esc(what)} · <time>{(at.Date == entryAt.Date ? $"{at:HH:mm:ss}" : $"{at:yyyy-MM-dd HH:mm}")}</time></p>";

    /// <summary>A clarification as question then answer, each label above its text rather than beside it.</summary>
    private static string ClarificationQa(Clarification c) =>
        Fields([("Question", c.Question), ("Answer", c.Answer)], stacked: true);

    // ── Story bible ───────────────────────────────────────────────────────────

    private static List<(string, string?)> BibleFields(
        string setting, string period, string themes, string tone, string rules, string notes) =>
    [
        ("Setting", setting), ("Time period", period), ("Themes", themes),
        ("Tone & style", tone), ("World rules", rules), ("Notes", notes),
    ];

    /// <summary>
    /// Bible snapshots are of two kinds: "agent-generated" records the new content, every other reason
    /// records the content <em>about to be replaced</em>. A replacement by the author is only visible in
    /// the next snapshot (or the current bible), so it is held as pending until that content turns up.
    /// </summary>
    private static IEnumerable<Ev> BibleEvents(StoryBible? bible, IReadOnlyList<StoryBibleSnapshot> snapshots)
    {
        var events = new List<Ev>();
        List<(string, string?)>? last = null;
        DateTime? pendingEdit = null;

        bool Changed(List<(string, string?)> fields) => last is null || Key(fields) != Key(last);
        void Emit(DateTime at, string actor, string title, List<(string, string?)> fields, bool asDiff = false)
        {
            events.Add(new Ev(at, Cat.Planning, actor, title, asDiff && last is not null ? Diff(last, fields) : Fields(fields)));
            last = fields;
        }
        void Flush(List<(string, string?)> fields)
        {
            if (pendingEdit is DateTime at && Changed(fields)) Emit(at, Author, "Story bible edited", fields, asDiff: true);
            pendingEdit = null;
        }

        foreach (var snap in snapshots.OrderBy(x => x.CreatedAt))
        {
            var fields = BibleFields(snap.SettingDescription, snap.TimePeriod, snap.Themes, snap.ToneAndStyle, snap.WorldRules, snap.Notes);
            switch (snap.Reason)
            {
                case "agent-generated":
                    pendingEdit = null;
                    Emit(snap.CreatedAt, Role(AgentRole.StoryBibleAgent), "Story bible generated", fields);
                    break;
                case "planning-phase-reset":
                    Flush(fields);
                    events.Add(new Ev(snap.CreatedAt, Cat.Planning, Author, "Story bible cleared"));
                    last = null;
                    break;
                default:
                    Flush(fields);
                    // Content nobody recorded on arrival (e.g. written by hand before the first snapshot).
                    if (Changed(fields))
                        Emit(snap.CreatedAt, Author, "Story bible as it stood before the next change", fields);
                    if (snap.Reason == "user-update") pendingEdit = snap.CreatedAt;
                    break;
            }
        }

        if (bible is not null)
        {
            var current = BibleFields(bible.SettingDescription, bible.TimePeriod, bible.Themes, bible.ToneAndStyle, bible.WorldRules, bible.Notes);
            Flush(current);
            if (Changed(current))
                Emit(last is null ? bible.CreatedAt : bible.UpdatedAt, Author, last is null ? "Story bible written" : "Story bible edited", current, asDiff: true);
        }
        return events;
    }

    // ── Characters & plot threads ─────────────────────────────────────────────

    private static List<(string, string?)> CharacterFields(CharacterCard c) =>
        CharacterFields(c.Name, c.Role, c.PhysicalDescription, c.Personality, c.Backstory, c.GoalMotivation, c.Arc, c.FirstAppearanceChapterNumber, c.Notes);

    private static List<(string, string?)> CharacterFields(CharacterCardVersion v) =>
        CharacterFields(v.Name, v.Role, v.PhysicalDescription, v.Personality, v.Backstory, v.GoalMotivation, v.Arc, v.FirstAppearanceChapterNumber, v.Notes);

    private static List<(string, string?)> CharacterFields(
        string name, CharacterRole role, string looks, string personality, string backstory,
        string goal, string arc, int? firstChapter, string notes) =>
    [
        ("Name", name), ("Role", role.ToString()), ("Appearance", looks), ("Personality", personality),
        ("Backstory", backstory), ("Goal / motivation", goal), ("Arc", arc),
        ("First appears", firstChapter is int n ? $"Chapter {n}" : null), ("Notes", notes),
    ];

    private static List<(string, string?)> ThreadFields(PlotThread t) =>
        ThreadFields(t.Name, t.Type, t.Status, t.Description, t.IntroducedChapterNumber, t.ResolvedChapterNumber);

    private static List<(string, string?)> ThreadFields(PlotThreadVersion v) =>
        ThreadFields(v.Name, v.Type, v.Status, v.Description, v.IntroducedChapterNumber, v.ResolvedChapterNumber);

    private static List<(string, string?)> ThreadFields(
        string name, PlotThreadType type, PlotThreadStatus status, string description, int? introduced, int? resolved) =>
    [
        ("Name", name), ("Type", type.ToString()), ("Status", status.ToString()), ("Description", description),
        ("Introduced", introduced is int i ? $"Chapter {i}" : null), ("Resolved", resolved is int r ? $"Chapter {r}" : null),
    ];

    /// <summary>
    /// Whole-set snapshots: what an agent generated, what it replaced, and what the author cleared. The
    /// individual cards' own edits come from their versions.
    /// </summary>
    private static IEnumerable<Ev> SetSnapshotEvents<T>(
        IEnumerable<(DateTime At, string Reason, string Json)> snapshots, string agent, string noun,
        Func<string, List<T>> parse, Func<T, string> name, Func<T, string> heading, Func<T, List<(string, string?)>> fields)
    {
        foreach (var (at, reason, json) in snapshots.OrderBy(x => x.At))
        {
            var items = parse(json);
            var count = Plural(items.Count, noun);
            var names = $"<p>{Esc(string.Join(", ", items.Select(name)))}</p>";
            var cards = string.Concat(items.Select(i => Fold(Esc(heading(i)), Fields(fields(i)))));
            yield return reason switch
            {
                "agent-generated"      => new Ev(at, Cat.Planning, agent, $"Generated {count}", cards),
                "agent-partial"        => new Ev(at, Cat.Planning, agent, $"Kept {count} from an interrupted run", cards),
                "agent-overwrite"      => new Ev(at, Cat.Planning, agent, $"Replaced the previous {count}", names),
                "planning-phase-reset" => new Ev(at, Cat.Planning, Author, $"Cleared {count}", names),
                _                      => new Ev(at, Cat.Planning, Author, $"Recorded {count} ({reason})", cards),
            };
        }
    }

    /// <summary>Per-item versions written outside the generating agent: the author's additions, edits, restores and archiving.</summary>
    private static IEnumerable<Ev> ItemVersionEvents<T>(
        IEnumerable<T> versions, Func<T, int> itemId, Func<T, int> number, Func<T, string> createdBy,
        Func<T, DateTime> at, Func<T, string> name, string noun, string agentCreatedBy, Func<T, List<(string, string?)>> fields)
    {
        foreach (var item in versions.GroupBy(itemId))
        {
            List<(string, string?)>? previous = null;
            foreach (var v in item.OrderBy(number))
            {
                var current = fields(v);
                var by = createdBy(v);
                if (by != agentCreatedBy)
                {
                    var label = $"{noun} “{name(v)}”";
                    var (actor, title, body) = by switch
                    {
                        "archived" => (Author, $"{label} archived", ""),
                        _ when by.StartsWith("restore:") => (Author, $"{label} restored to {by["restore:".Length..]}", previous is null ? Fields(current) : Diff(previous, current)),
                        _ when previous is null => (Actor(by), $"{label} added", Fields(current)),
                        _ => (Actor(by), $"{label} edited", Diff(previous, current)),
                    };
                    yield return new Ev(at(v), Cat.Planning, actor, title, body);
                }
                previous = current;
            }
        }
    }

    // ── Chapters (planning and versions — never the prose) ────────────────────

    private static List<(string, string?)> ChapterFields(
        string title, string pov, string charactersJson, string threadsJson, string outline, string foreshadowing, string payoff) =>
    [
        ("Title", title), ("POV", pov), ("Characters", JsonList(charactersJson)), ("Plot threads", JsonList(threadsJson)),
        ("Outline", outline), ("Foreshadowing", foreshadowing), ("Payoff", payoff),
    ];

    private static IEnumerable<Ev> ChapterEvents(List<Chapter> chapters, IReadOnlyList<ChapterVersion> allVersions)
    {
        var byChapter = allVersions.ToLookup(v => v.ChapterId);
        foreach (var c in chapters)
        {
            var label    = $"Ch. {c.Number}";
            var versions = byChapter[c.Id].OrderBy(v => v.VersionNumber).ToList();
            var first    = versions.FirstOrDefault();

            // The outline as planned: the first version kept it, otherwise only the current one is known.
            var planned = first is null
                ? ChapterFields(c.Title, c.PovCharacter, c.CharactersInvolvedJson, c.PlotThreadsJson, c.Outline, c.ForeshadowingNotes, c.PayoffNotes)
                : ChapterFields(first.Title, first.PovCharacter, first.CharactersInvolvedJson, first.PlotThreadsJson, first.Outline, first.ForeshadowingNotes, first.PayoffNotes);
            yield return new Ev(c.CreatedAt, Cat.Planning, Role(AgentRole.ChaptersAgent), $"Chapter {c.Number} outlined", Fields(planned), label);

            var previousPlan  = planned;
            var previousWords = 0;
            foreach (var v in versions)
            {
                var plan  = ChapterFields(v.Title, v.PovCharacter, v.CharactersInvolvedJson, v.PlotThreadsJson, v.Outline, v.ForeshadowingNotes, v.PayoffNotes);
                var words = WordCount(v.Content);
                var delta = v == first ? "" : $" ({(words - previousWords >= 0 ? "+" : "−")}{Math.Abs(words - previousWords):N0})";
                var title = v.CreatedBy switch
                {
                    "agent:Writer" => $"Chapter {c.Number} drafted",
                    "agent:Editor" => $"Chapter {c.Number} revised",
                    "user"         => $"Chapter {c.Number} edited by the author",
                    var by when by.StartsWith("restore:") => $"Chapter {c.Number} restored to {by["restore:".Length..]}",
                    _              => $"Chapter {c.Number} saved",
                };
                var body = $"<p class=\"note\">Version {v.VersionNumber} · {words:N0} words{delta} · {Esc(v.Status.ToString())}" +
                           (v.IsActive ? " · <strong>the version in the book</strong>" : "") + "</p>" +
                           Diff(previousPlan, plan);
                yield return new Ev(v.CreatedAt, Cat.Writing, Actor(v.CreatedBy), title, body, label);
                previousPlan  = plan;
                previousWords = words;
            }

            // Planning edited after the last version (outline edits don't create versions).
            var now = ChapterFields(c.Title, c.PovCharacter, c.CharactersInvolvedJson, c.PlotThreadsJson, c.Outline, c.ForeshadowingNotes, c.PayoffNotes);
            var late = Diff(previousPlan, now);
            if (late.Length > 0)
                yield return new Ev(c.UpdatedAt, Cat.Planning, Author, $"Chapter {c.Number} planning updated", late, label);
        }
    }

    // ── Illustrations ─────────────────────────────────────────────────────────

    private static IEnumerable<Ev> IllustrationEvents(MetadataSources s, Func<int?, string?> chapterLabel)
    {
        var planner     = Role(AgentRole.IllustrationPlanner);
        var illustrator = Role(AgentRole.Illustrator);
        var characters  = s.Characters.ToDictionary(c => c.Id, c => c.Name);

        foreach (var a in s.VisualAssets)
            yield return new Ev(a.CreatedAt, Cat.Art, planner, $"Visual asset “{a.Name}” registered", Fields(
            [
                ("Kind", a.Kind.ToString()),
                ("Worn by", a.CharacterCardId is int id && characters.TryGetValue(id, out var who) ? who : null),
                ("First shown", a.FirstChapterNumber is int n ? $"Chapter {n}" : null),
                ("Description", a.Description),
            ]));

        foreach (var i in s.Illustrations)
        {
            var subject = i.Kind switch
            {
                IllustrationKind.Cover    => "Cover",
                IllustrationKind.Portrait => $"Portrait of {(i.CharacterCardId is int id && characters.TryGetValue(id, out var n) ? n : "a character")}",
                _                         => "Scene",
            };
            var chapter = i.Kind == IllustrationKind.Scene ? chapterLabel(i.ChapterId) : null;
            var planBody = Fields(
            [
                ("Caption", i.Caption),
                ("Placed after", i.Kind == IllustrationKind.Scene ? $"paragraph {i.ParagraphNumber}" + (i.AnchorText.Length > 0 ? $": “{i.AnchorText}”" : "") : null),
                ("Orientation", i.Orientation.ToString()),
                ("Characters", JsonList(i.CharacterNamesJson)),
                ("Focus character", i.FocusCharacterId is int f && characters.TryGetValue(f, out var fn) ? fn : null),
                ("Prompt", i.Prompt),
                ("Negative prompt", i.NegativePrompt),
            ]);
            if (i.UpdatedAt > i.CreatedAt.AddSeconds(1))
                planBody += $"<p class=\"note\">Shown as last edited ({i.UpdatedAt:yyyy-MM-dd HH:mm}).</p>";
            yield return new Ev(i.CreatedAt, Cat.Art, planner, $"{subject} planned", planBody, chapter);

            if (i.ImageId is int imageId && s.Images.TryGetValue(imageId, out var image))
            {
                var info = $"<p class=\"note\">{image.Width}×{image.Height} · seed {image.Seed}" +
                           (i.Status == IllustrationStatus.Planned ? " · the plan was edited afterwards and awaits a re-render" : "") + "</p>";
                if (i.Kind == IllustrationKind.Portrait)
                {
                    var (data, type) = ExportImageCompressor.Compress(image.Data, image.ContentType);
                    info = $"<img class=\"portrait\" src=\"data:{type};base64,{Convert.ToBase64String(data)}\" alt=\"{Esc(subject)}\">" + info;
                }
                else info += "<p class=\"note\">The image is part of the book.</p>";
                yield return new Ev(image.CreatedAt, Cat.Art, illustrator, $"{subject} rendered", info, chapter);
            }
            if (i.Status == IllustrationStatus.Failed)
                yield return new Ev(i.UpdatedAt, Cat.Art, illustrator, $"{subject} render failed", Text(i.Error), chapter);
        }
    }

    // ── Conversation, runs and LLM calls ──────────────────────────────────────

    private const string ThinkingPrefix = "\U0001F4AD Thinking";

    private static IEnumerable<Ev> MessageEvents(IReadOnlyList<AgentMessage> messages, Func<int?, string?> chapterLabel)
    {
        // Answers saved since v0.3.0 point at their question: the pair becomes one entry. Older answers
        // carry no link and stay separate entries.
        var questionIds = messages.Where(m => m.MessageType == MessageType.Question).Select(m => m.Id).ToHashSet();
        var answerTo = messages
            .Where(m => m.MessageType == MessageType.Answer && m.QuestionMessageId is int q && questionIds.Contains(q))
            .GroupBy(m => m.QuestionMessageId!.Value).ToDictionary(g => g.Key, g => g.First());

        foreach (var m in messages)
        {
            var chapter = chapterLabel(m.ChapterId);
            if (m.MessageType == MessageType.SystemNote && m.Content.StartsWith(ThinkingPrefix))
            {
                var thinking = m.Content[ThinkingPrefix.Length..].Trim();
                yield return new Ev(m.CreatedAt, Cat.Thinking, Role(m.AgentRole), "Thinking",
                    Fold($"Reasoning · {WordCount(thinking):N0} words", Text(thinking)), chapter);
                continue;
            }
            if (m.MessageType == MessageType.Answer && m.QuestionMessageId is int answered && questionIds.Contains(answered))
                continue;
            // Section snapshots of the section chat — the section's own history covers them.
            if (m.MessageType is MessageType.ChatContext or MessageType.ChatChange)
                continue;
            if (m.MessageType == MessageType.Question && answerTo.TryGetValue(m.Id, out var answer))
            {
                var outcome = string.IsNullOrWhiteSpace(answer.Content)
                    ? Reply($"{Author} skipped it", m.CreatedAt, answer.CreatedAt)
                    : Reply($"{Author} answered", m.CreatedAt, answer.CreatedAt) + Field("Answer", answer.Content);
                yield return new Ev(m.CreatedAt, Cat.Conversation, Role(m.AgentRole),
                    m.IsOptional ? "Asked a question (optional)" : "Asked a question",
                    Field("Question", m.Content) + outcome, chapter);
                continue;
            }
            var (actor, title) = m.MessageType switch
            {
                MessageType.Question => (Role(m.AgentRole),
                    (m.IsOptional ? "Asked the author (optional)" : "Asked the author") + (m.IsResolved ? "" : " — unanswered")),
                MessageType.Answer   => (Author, $"Answered the {Role(m.AgentRole)} agent"),
                MessageType.Feedback => (Role(m.AgentRole), "Feedback"),
                MessageType.SystemNote => (Role(m.AgentRole), "Note"),
                MessageType.ChatUser  => (Author, "Section chat"),
                MessageType.ChatReply => (Role(m.AgentRole), "Section chat reply"),
                _                    => (Role(m.AgentRole), "Message"),
            };
            yield return new Ev(m.CreatedAt, Cat.Conversation, actor, title, LongText(m.Content), chapter);
        }
    }

    private static IEnumerable<Ev> RunEvents(IReadOnlyList<AgentRun> runs, Func<int?, string?> chapterLabel)
    {
        foreach (var r in runs)
        {
            var chapter = chapterLabel(r.ChapterId);
            yield return new Ev(r.CreatedAt, Cat.Runs, "Run", $"Started “{r.RunType}”", "", chapter, Compact: true);
            if (r.Status is AgentRunPersistStatus.Running or AgentRunPersistStatus.WaitingForInput) continue;
            var outcome = r.Status switch
            {
                AgentRunPersistStatus.Completed => "Completed",
                AgentRunPersistStatus.Failed    => "Failed",
                AgentRunPersistStatus.Cancelled => "Cancelled",
                _                               => "Interrupted by a restart",
            };
            yield return new Ev(r.UpdatedAt, Cat.Runs, "Run", $"{outcome} “{r.RunType}”",
                $"after {Duration(r.UpdatedAt - r.CreatedAt)} · last agent: {Esc(Role(r.CurrentRole))}", chapter, Compact: true);
        }
    }

    private static IEnumerable<Ev> UsageEvents(IReadOnlyList<TokenUsageRecord> usage, Func<int?, string?> chapterLabel)
    {
        foreach (var r in usage)
        {
            if (r.AgentRole == AgentRole.WorkflowStep)
            {
                yield return new Ev(r.CreatedAt, Cat.Runs, "Step", r.StepLabel ?? "Workflow step", "", chapterLabel(r.ChapterId), Compact: true);
                continue;
            }
            var model  = r.ModelName is { Length: > 0 } m ? $" · {Esc(m)}" : "";
            var detail = $"{r.PromptTokens:N0} in → {r.CompletionTokens:N0} out{model}" +
                         (r.Failed ? $" · <span class=\"fail\">failed{(r.FailureReason is { Length: > 0 } why ? ": " + Esc(why) : "")}</span>" : "");
            yield return new Ev(r.CreatedAt, Cat.Llm, Role(r.AgentRole), "LLM call", detail, chapterLabel(r.ChapterId), Compact: true);
        }
    }

    // ── Overview ──────────────────────────────────────────────────────────────

    private static void AppendOverview(StringBuilder html, MetadataSources s, List<Chapter> chapters, List<Ev> timeline)
    {
        var book  = s.Book;
        var calls = s.TokenUsage.Where(r => r.AgentRole != AgentRole.WorkflowStep).ToList();
        // Chat questions (human-assisted pauses, older clarification rounds) plus the planner's questions
        // asked on the Clarifications page.
        var questions = s.Messages.Count(m => m.MessageType == MessageType.Question)
                      + s.Clarifications.Count(c => c.Source == ClarificationSource.Planner);

        html.Append("<section id=\"overview\" class=\"md-section\">\n  <h2>Overview</h2>\n");
        html.Append(Fields(
        [
            ("Genre", book.Genre),
            ("Language", book.Language),
            ("Status", book.Status.ToString()),
            ("Chapters", $"{chapters.Count(c => c.Status == ChapterStatus.Done)} done of {chapters.Count} planned (target {book.TargetChapterCount})"),
            ("Words in the book", $"{chapters.Sum(c => WordCount(c.Content)):N0}"),
            ("Created", $"{book.CreatedAt:yyyy-MM-dd HH:mm}"),
            ("Timeline", timeline.Count == 0 ? null : $"{timeline[0].At:yyyy-MM-dd HH:mm} → {timeline[^1].At:yyyy-MM-dd HH:mm} · {timeline.Count:N0} events"),
            ("LLM calls", calls.Count == 0 ? null : $"{calls.Count:N0} ({calls.Count(c => c.Failed):N0} failed) · {calls.Sum(c => (long)c.PromptTokens):N0} tokens in, {calls.Sum(c => (long)c.CompletionTokens):N0} out"),
            ("Questions to the author", questions == 0 ? null : $"{questions:N0}"),
            ("Premise", book.Premise),
        ]));

        if (calls.Count > 0)
        {
            html.Append("  <div class=\"tables\">\n");
            AppendUsageTable(html, "By agent", calls.GroupBy(c => Role(c.AgentRole)));
            AppendUsageTable(html, "By model", calls.GroupBy(c => c.ModelName is { Length: > 0 } m ? m : "unknown"));
            html.Append("  </div>\n");
        }

        if (book.Clarifications.Count > 0)
            html.Append(Fold($"Clarifications (current) · {book.Clarifications.Count}",
                string.Concat(book.Clarifications.Select(ClarificationQa))));

        var art = Fields(
        [
            ("Style", book.IllustrationStyle), ("Portrait framing", book.IllustrationPortraitFraming),
            ("Negative prompt", book.IllustrationNegativePrompt),
        ]);
        if (art.Length > 0) html.Append(Fold("Art direction (current)", art));

        var prompts = Fields(
        [
            ("Story bible", book.StoryBibleSystemPrompt), ("Characters", book.CharactersSystemPrompt),
            ("Plot threads", book.PlotThreadsSystemPrompt), ("Chapter outlines", book.ChapterOutlinesSystemPrompt),
            ("Writer", book.WriterSystemPrompt), ("Editor", book.EditorSystemPrompt),
            ("Continuity checker", book.ContinuityCheckerSystemPrompt), ("Illustrator", book.IllustratorSystemPrompt),
        ]);
        if (prompts.Length > 0) html.Append(Fold("Custom system prompts (current)", prompts));
        html.Append("</section>\n");
    }

    private static void AppendUsageTable(StringBuilder html, string caption, IEnumerable<IGrouping<string, TokenUsageRecord>> groups)
    {
        html.Append($"  <table class=\"stats-table\"><caption>{Esc(caption)}</caption>\n");
        html.Append("    <thead><tr><th></th><th class=\"num\">Calls</th><th class=\"num\">In</th><th class=\"num\">Out</th></tr></thead><tbody>\n");
        foreach (var g in groups.OrderByDescending(g => g.Sum(r => (long)r.PromptTokens + r.CompletionTokens)))
            html.Append($"      <tr><td>{Esc(g.Key)}</td><td class=\"num\">{g.Count():N0}</td>" +
                        $"<td class=\"num\">{g.Sum(r => (long)r.PromptTokens):N0}</td><td class=\"num\">{g.Sum(r => (long)r.CompletionTokens):N0}</td></tr>\n");
        html.Append("    </tbody></table>\n");
    }

    // ── Timeline ──────────────────────────────────────────────────────────────

    private static void AppendTimeline(StringBuilder html, List<Ev> timeline)
    {
        html.Append("<section id=\"timeline\" class=\"md-section\">\n  <h2>Timeline</h2>\n");
        if (timeline.Count == 0) html.Append("  <p class=\"note\">Nothing has happened yet.</p>\n");

        var days = timeline.GroupBy(e => e.At.Date).ToList();
        if (days.Count > 1)
        {
            html.Append("  <nav class=\"days\">");
            foreach (var d in days)
                html.Append($"<a href=\"#day-{d.Key:yyyy-MM-dd}\">{d.Key:MMM d}</a>");
            html.Append("</nav>\n");
        }

        foreach (var day in days)
        {
            html.Append($"  <h3 class=\"day\" id=\"day-{day.Key:yyyy-MM-dd}\">{day.Key:dddd, d MMMM yyyy}</h3>\n");
            foreach (var e in day)
            {
                var cat     = e.Cat.ToString().ToLowerInvariant();
                var chapter = e.Chapter is null ? "" : $"<span class=\"chap\">{Esc(e.Chapter)}</span>";
                var head    = $"<time>{e.At:HH:mm:ss}</time><span class=\"actor\">{Esc(e.Actor)}</span>{chapter}<span class=\"title\">{Esc(e.Title)}</span>";
                html.Append(e.Compact
                    ? $"  <div class=\"ev compact cat-{cat}\">{head}{(e.Body.Length > 0 ? $"<span class=\"detail\">{e.Body}</span>" : "")}</div>\n"
                    : $"  <div class=\"ev cat-{cat}\"><div class=\"ev-head\">{head}</div>{(e.Body.Length > 0 ? $"<div class=\"ev-body\">{e.Body}</div>" : "")}</div>\n");
            }
        }
        html.Append("</section>\n");
    }

    private const string EyeIcon =
        "<svg width=\"16\" height=\"16\" viewBox=\"0 0 24 24\" fill=\"none\" stroke=\"currentColor\" stroke-width=\"2\" " +
        "stroke-linecap=\"round\" stroke-linejoin=\"round\" aria-hidden=\"true\"><path d=\"M2 12s3.5-7 10-7 10 7 10 7-3.5 7-10 7S2 12 2 12z\"/>" +
        "<circle cx=\"12\" cy=\"12\" r=\"3\"/></svg>";

    private static void AppendToolbar(StringBuilder html, List<Ev> timeline)
    {
        (string Bg, string Fg, string Border, string Name)[] presets =
        [
            ("#ffffff", "#1a1a1a", "#aaaaaa", "Light"),
            ("#1c1c1e", "#e8e8e8", "#e8e8e8", "Dark"),
            ("#f4ecd8", "#5c4a1e", "#5c4a1e", "Sepia"),
            ("#002b36", "#93a1a1", "#93a1a1", "Solarized"),
        ];
        html.Append("  <div id=\"toolbar\">\n");
        foreach (var p in presets)
            html.Append($"    <button class=\"preset-btn\" data-bg=\"{p.Bg}\" data-fg=\"{p.Fg}\" " +
                        $"style=\"background:{p.Bg};color:{p.Fg};border:2px solid {p.Border}\">{p.Name}</button>\n");
        html.Append("    <div class=\"toolbar-sep\"></div>\n");
        html.Append("    <button class=\"size-btn\" id=\"font-smaller\" title=\"Decrease font size\">A−</button>\n");
        html.Append("    <button class=\"size-btn\" id=\"font-larger\" title=\"Increase font size\">A+</button>\n");

        // Category filters live in a popup behind the eye button.
        html.Append("    <div class=\"filter-wrap\">\n");
        html.Append("      <button class=\"size-btn\" id=\"filter-btn\" title=\"Show or hide kinds of events\" aria-haspopup=\"true\" aria-expanded=\"false\">")
            .Append(EyeIcon).Append("<span id=\"filter-hidden\"></span></button>\n");
        html.Append("      <div id=\"filter-menu\" role=\"menu\" hidden>\n");
        foreach (var (cat, label) in Categories)
        {
            var count = timeline.Count(e => e.Cat == cat);
            if (count == 0) continue;
            var key = cat.ToString().ToLowerInvariant();
            html.Append($"        <label class=\"filter cat-{key}\"><input type=\"checkbox\" data-cat=\"{key}\" checked><span class=\"swatch\"></span>{label}<span class=\"count\">{count:N0}</span></label>\n");
        }
        html.Append("      </div>\n    </div>\n");
        html.Append("  </div>\n");
    }

    // ── Rendering helpers ─────────────────────────────────────────────────────

    private static string Esc(string? text) => BookExportService.EscHtml(text);

    private static string Text(string? text) => $"<div class=\"text\">{Esc(text?.Trim())}</div>";

    /// <summary>Short text inline; long text folded behind its opening words.</summary>
    private static string LongText(string text)
    {
        text = text.Trim();
        if (text.Length <= 1200) return Text(text);
        var cut = text.LastIndexOf(' ', 240) is var sp and > 0 ? sp : 240;
        return Fold(Esc(text[..cut]) + "…", Text(text));
    }

    private static string Fold(string summaryHtml, string bodyHtml) =>
        $"<details><summary>{summaryHtml}</summary>{bodyHtml}</details>";

    /// <param name="stacked">Each label above its value instead of beside it.</param>
    private static string Fields(IEnumerable<(string Label, string? Value)> fields, bool stacked = false)
    {
        var rows = fields.Where(f => !string.IsNullOrWhiteSpace(f.Value))
            .Select(f => $"<dt>{Esc(f.Label)}</dt><dd>{Esc(f.Value!.Trim())}</dd>").ToList();
        return rows.Count == 0 ? "" : $"<dl class=\"prop-list{(stacked ? " stacked" : "")}\">{string.Concat(rows)}</dl>";
    }

    /// <summary>The fields that changed, old value struck through above the new one.</summary>
    private static string Diff(List<(string Label, string? Value)> before, List<(string Label, string? Value)> after)
    {
        var old = before.ToDictionary(f => f.Label, f => (f.Value ?? "").Trim());
        var rows = after
            .Where(f => old.GetValueOrDefault(f.Label, "") != (f.Value ?? "").Trim())
            .Select(f =>
            {
                var was = old.GetValueOrDefault(f.Label, "");
                var now = (f.Value ?? "").Trim();
                return $"<dt>{Esc(f.Label)}</dt><dd>" +
                       (was.Length > 0 ? $"<del>{Esc(was)}</del>" : "") +
                       (now.Length > 0 ? $"<ins>{Esc(now)}</ins>" : "<em>removed</em>") + "</dd>";
            }).ToList();
        return rows.Count == 0 ? "" : $"<dl class=\"prop-list diff\">{string.Concat(rows)}</dl>";
    }

    private static string Key(List<(string Label, string? Value)> fields) =>
        string.Join("\u001f", fields.Select(f => (f.Value ?? "").Trim()));

    private static List<T> Deserialize<T>(string json)
    {
        try { return JsonSerializer.Deserialize<List<T>>(json) ?? []; }
        catch (JsonException) { return []; }
    }

    private static string? JsonList(string json)
    {
        try { return string.Join(", ", JsonSerializer.Deserialize<List<string>>(json) ?? []); }
        catch (JsonException) { return json; }
    }

    private static int WordCount(string? text) => string.IsNullOrWhiteSpace(text) ? 0 : Regex.Matches(text, @"\S+").Count;

    private static string Plural(int n, string noun) => $"{n} {noun}{(n == 1 ? "" : "s")}";

    private static string Duration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" : t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{t.Seconds}s";

    /// <summary>Who a version's CreatedBy names: "agent:Writer" → "Writer", "user"/"restore:…" → the author.</summary>
    private static string Actor(string createdBy) =>
        createdBy.StartsWith("agent:") ? createdBy["agent:".Length..] : Author;

    private static string Role(AgentRole role) => role switch
    {
        AgentRole.StoryBibleAgent     => "Story Bible",
        AgentRole.CharactersAgent     => "Characters",
        AgentRole.PlotThreadsAgent    => "Plot Threads",
        AgentRole.ChaptersAgent       => "Chapter Outlines",
        AgentRole.ContinuityChecker   => "Continuity Checker",
        AgentRole.IllustrationPlanner => "Illustration Planner",
        AgentRole.SectionChat         => "Section Chat",
        AgentRole.WorkflowStep        => "Workflow",
        _                             => role.ToString(),
    };

    // ── Styles and script ─────────────────────────────────────────────────────

    private static readonly string Css = @"
    :root { --bg: #ffffff; --fg: #1a1a1a; --fs: 16px; --border: rgba(128,128,128,0.2); --soft: rgba(128,128,128,0.06); }
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body { background: var(--bg); color: var(--fg); font-family: system-ui, -apple-system, sans-serif; font-size: var(--fs); line-height: 1.55; }
    #toolbar { position: sticky; top: 0; z-index: 100; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 8px 16px; background: var(--bg); border-bottom: 1px solid var(--border); font-size: 0.85em; }
    .toolbar-sep { width: 1px; height: 22px; background: var(--border); margin: 0 4px; }
    .preset-btn, .size-btn { padding: 2px 9px; border-radius: 4px; cursor: pointer; font-size: 0.9em; font-weight: 600; }
    .size-btn { background: transparent; color: var(--fg); border: 1px solid var(--border); }
    .filter-wrap { position: relative; }
    #filter-btn { display: inline-flex; align-items: center; gap: 4px; min-height: 24px; }
    #filter-btn svg { display: block; }
    #filter-btn[aria-expanded=""true""], #filter-btn.has-hidden { border-color: var(--fg); }
    #filter-hidden { font-size: 0.85em; }
    #filter-menu { position: absolute; top: calc(100% + 6px); left: 0; z-index: 200; min-width: 210px; max-width: calc(100vw - 32px);
                   padding: 6px; background: var(--bg); color: var(--fg); border: 1px solid var(--border); border-radius: 8px;
                   box-shadow: 0 8px 24px rgba(0,0,0,0.18); }
    #filter-menu[hidden] { display: none; }
    .filter { display: flex; align-items: center; gap: 8px; padding: 5px 8px; border-radius: 6px; cursor: pointer; user-select: none; white-space: nowrap; }
    .filter:hover { background: var(--soft); }
    .filter .swatch { width: 10px; height: 10px; border-radius: 50%; background: var(--c); flex: none; }
    .filter .count { margin-left: auto; padding-left: 12px; opacity: 0.5; font-size: 0.85em; font-variant-numeric: tabular-nums; }
    main { padding: 32px 16px 96px; }
    header.doc-header { margin-bottom: 32px; padding-bottom: 20px; border-bottom: 2px solid var(--border); }
    header.doc-header h1 { font-size: 1.9em; margin-bottom: 6px; }
    .subtitle { opacity: 0.6; font-size: 0.9em; }
    .md-section { margin-bottom: 40px; }
    .md-section > h2 { font-size: 1.3em; margin-bottom: 16px; }
    .prop-list { display: grid; grid-template-columns: max-content 1fr; gap: 4px 14px; font-size: 0.92em; }
    .prop-list dt { font-weight: 600; opacity: 0.6; }
    .prop-list dd { white-space: pre-wrap; word-break: break-word; }
    .prop-list.stacked { display: block; margin-bottom: 10px; }
    .prop-list.stacked dd { margin: 0 0 6px; }
    .reply { font-weight: 600; font-size: 0.9em; margin: 8px 0 2px; }
    .reply time { font-weight: 400; opacity: 0.6; }
    .diff del { display: block; opacity: 0.5; }
    .diff ins { display: block; text-decoration: none; }
    .tables { display: flex; flex-wrap: wrap; align-items: flex-start; gap: 24px; margin: 20px 0; }
    .stats-table { border-collapse: collapse; font-size: 0.85em; min-width: 280px; }
    .stats-table caption { text-align: left; font-weight: 600; opacity: 0.6; padding-bottom: 4px; }
    .stats-table th, .stats-table td { padding: 3px 10px; border-bottom: 1px solid var(--border); text-align: left; }
    .stats-table .num { text-align: right; font-variant-numeric: tabular-nums; }
    details { margin: 6px 0; }
    details > summary { cursor: pointer; opacity: 0.8; }
    details > :not(summary) { margin: 6px 0 6px 14px; }
    .note { opacity: 0.6; font-size: 0.88em; margin: 4px 0; }
    .text { white-space: pre-wrap; word-break: break-word; font-size: 0.92em; }
    .fail { color: #dc2626; }
    nav.days { display: flex; flex-wrap: wrap; gap: 6px 12px; margin-bottom: 16px; font-size: 0.85em; }
    nav.days a { color: inherit; opacity: 0.7; }
    h3.day { font-size: 1em; padding: 10px 0 6px; margin-top: 18px; border-bottom: 1px solid var(--border); scroll-margin-top: 80px; }
    .ev { border-left: 3px solid var(--c, #94a3b8); padding: 6px 10px; margin: 6px 0; background: var(--soft); border-radius: 0 6px 6px 0; }
    .ev.compact { background: none; padding: 1px 10px; margin: 1px 0; font-size: 0.82em; opacity: 0.8; display: flex; flex-wrap: wrap; align-items: baseline; gap: 0 8px; }
    .ev-head { display: flex; flex-wrap: wrap; align-items: baseline; gap: 0 8px; }
    .ev-body { margin-top: 6px; }
    .ev time { font-variant-numeric: tabular-nums; opacity: 0.5; font-size: 0.85em; }
    .actor { font-weight: 700; color: var(--c, inherit); }
    .chap { font-size: 0.78em; padding: 0 6px; border-radius: 4px; border: 1px solid var(--border); }
    .ev:not(.compact) .title { font-weight: 600; }
    .detail { opacity: 0.85; }
    img.portrait { float: right; width: 160px; height: auto; margin: 0 0 8px 12px; border-radius: 6px; }
    .ev-body::after { content: ''; display: block; clear: both; }
    .cat-planning { --c: #6366f1; } .cat-writing { --c: #16a34a; } .cat-conversation { --c: #d97706; }
    .cat-thinking { --c: #64748b; } .cat-art { --c: #db2777; } .cat-runs { --c: #0891b2; } .cat-llm { --c: #94a3b8; }
    body.hide-planning .ev.cat-planning, body.hide-writing .ev.cat-writing, body.hide-conversation .ev.cat-conversation,
    body.hide-thinking .ev.cat-thinking, body.hide-art .ev.cat-art, body.hide-runs .ev.cat-runs, body.hide-llm .ev.cat-llm { display: none; }
    @media print { #toolbar { display: none; } }";

    private static readonly string Js = @"    (function () {
      var store = { get: function (k) { try { return localStorage.getItem(k); } catch (e) { return null; } },
                    set: function (k, v) { try { localStorage.setItem(k, v); } catch (e) {} } };
      var hidden = JSON.parse(store.get('abook-meta-hidden') || '[]');
      var filterBtn = document.getElementById('filter-btn');
      var filterMenu = document.getElementById('filter-menu');
      var boxes = document.querySelectorAll('.filter input');
      function showHiddenCount() {
        var n = 0;
        boxes.forEach(function (b) { if (!b.checked) n++; });
        document.getElementById('filter-hidden').textContent = n > 0 ? n + ' hidden' : '';
        filterBtn.classList.toggle('has-hidden', n > 0);
      }
      boxes.forEach(function (box) {
        var cat = box.dataset.cat;
        box.checked = hidden.indexOf(cat) < 0;
        document.body.classList.toggle('hide-' + cat, !box.checked);
        box.addEventListener('change', function () {
          document.body.classList.toggle('hide-' + cat, !box.checked);
          hidden = hidden.filter(function (c) { return c !== cat; });
          if (!box.checked) hidden.push(cat);
          store.set('abook-meta-hidden', JSON.stringify(hidden));
          showHiddenCount();
        });
      });
      showHiddenCount();
      function setMenu(open) {
        filterMenu.hidden = !open;
        filterBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
      }
      filterBtn.addEventListener('click', function (e) { e.stopPropagation(); setMenu(filterMenu.hidden); });
      document.addEventListener('click', function (e) { if (!filterMenu.hidden && !filterMenu.contains(e.target)) setMenu(false); });
      document.addEventListener('keydown', function (e) { if (e.key === 'Escape' && !filterMenu.hidden) { setMenu(false); filterBtn.focus(); } });
      function applyPreset(btn) {
        document.documentElement.style.setProperty('--bg', btn.dataset.bg);
        document.documentElement.style.setProperty('--fg', btn.dataset.fg);
        store.set('abook-meta-preset', btn.textContent);
      }
      var presets = document.querySelectorAll('.preset-btn');
      presets.forEach(function (btn) {
        btn.addEventListener('click', function () { applyPreset(btn); });
        if (btn.textContent === store.get('abook-meta-preset')) applyPreset(btn);
      });
      var fontSize = Number(store.get('abook-meta-fontsize')) || 16;
      function applyFontSize(size) {
        fontSize = Math.min(Math.max(size, 11), 28);
        document.documentElement.style.setProperty('--fs', fontSize + 'px');
        store.set('abook-meta-fontsize', fontSize);
      }
      applyFontSize(fontSize);
      document.getElementById('font-smaller').addEventListener('click', function () { applyFontSize(fontSize - 1); });
      document.getElementById('font-larger').addEventListener('click', function () { applyFontSize(fontSize + 1); });
    })();";
}
