using System.Text.Json;
using ABook.Core.Models;

namespace ABook.Agents;

/// <summary>A conflict with the book's canon the LLM flagged in a reply.</summary>
public sealed record ChatConflict(string Description, string Canon, int? Chapter);

/// <summary>One action as the LLM wrote it, before it is checked against the book.</summary>
/// <param name="Section">The section the action refers to (Section N), for the section actions and add_chapter.</param>
public sealed record RawChatAction(
    string Type, string Label, int? Chapter, string? Name, IReadOnlyList<KeyValuePair<string, string>> Changes,
    string? Instructions, string? OriginalText, string? ReplacementText, string? Text, string? Option, int? Section = null);

/// <summary>A parsed reply: the message to the author, the conflicts it flags and the changes it proposes.</summary>
public sealed record ParsedChatReply(
    string Message, IReadOnlyList<ChatConflict> Conflicts, IReadOnlyList<RawChatAction> Actions, IReadOnlyList<ChatPartRequest>? Parts = null)
{
    /// <summary>The parts of the book the reply asks to read before answering (two-step replies); empty when none.</summary>
    public IReadOnlyList<ChatPartRequest> Parts { get; init; } = Parts ?? [];
}

/// <summary>A part of the book the chat asks to read in full: one of <see cref="BookChatActions.PartKinds"/>.</summary>
public sealed record ChatPartRequest(string Kind, int? Chapter, string? Name, int? Section = null);

/// <summary>A checked action, resolved against the book — what a <see cref="ChatProposal"/> stores as its payload.</summary>
/// <param name="AfterChapterId">add_chapter / move_chapter: the chapter it goes after (with <paramref name="AtStart"/> false; neither = at the end).</param>
/// <param name="UndoAfterChapterId">move_chapter, set when applied: where it was (after this chapter, or at the start) — for undo.</param>
/// <param name="SectionId">The section actions' section; add_chapter: the section it is added at the end of.</param>
/// <param name="AfterSectionId">add_section / move_section: the section it goes after (with <paramref name="AtStart"/> false; neither = at the end).</param>
/// <param name="UndoAfterSectionId">move_section, set when applied: where it was (after this section, or at the start) — for undo.</param>
public sealed record ChatActionPayload(
    string Type, int? ChapterId, int? ChapterNumber, string? Name, Dictionary<string, string> Changes,
    string? Instructions, string? OriginalText, string? ReplacementText, string? Text,
    int? AfterChapterId = null, bool AtStart = false, int? UndoAfterChapterId = null, bool UndoAtStart = false,
    int? SectionId = null, int? SectionNumber = null, int? AfterSectionId = null, int? UndoAfterSectionId = null)
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string ToJson() => JsonSerializer.Serialize(this, Options);
    public static ChatActionPayload FromJson(string json) =>
        JsonSerializer.Deserialize<ChatActionPayload>(json, Options) ?? throw new InvalidOperationException("Unreadable proposal.");
}

/// <summary>A proposal ready to save: its payload plus how it relates to the rest of the reply.</summary>
public sealed record NormalizedAction(
    ChatActionPayload Payload, string Label, string? Target, int? DependsOnIndex, string? OptionGroup);

/// <summary>The parts of the book the normalizer checks actions against.</summary>
public sealed record BookChatState(
    bool BibleLocked,
    IReadOnlyList<string> Characters,
    IReadOnlyList<string> Threads,
    IReadOnlyList<BookChatChapter> Chapters,
    IReadOnlyList<BookChatNote>? Notes = null,
    IReadOnlyList<BookChatSection>? Sections = null)
{
    /// <summary>The state of a book as the chat sees it: active chapters with their sections, notes, cast and threads.</summary>
    public static BookChatState Of(Book book, bool bibleLocked, IEnumerable<CharacterCard> characters, IEnumerable<PlotThread> threads,
        IEnumerable<Chapter> chapters, IEnumerable<Section> sections) => new(
        bibleLocked,
        characters.Select(c => c.Name).ToList(),
        threads.Select(t => t.Name).ToList(),
        chapters.Select(c => new BookChatChapter(c.Id, c.Number, c.Title, c.Content, c.Outline, c.SectionId)).ToList(),
        BookChatNote.Of(book.Clarifications),
        sections.Where(s => !s.IsArchived).Select(s => new BookChatSection(s.Id, s.Number, s.Title, s.PlannedChapters)).ToList());
}

/// <summary>An active section as the chat numbers it (Section N).</summary>
public sealed record BookChatSection(int Id, int Number, string Title, int PlannedChapters = 1);

/// <summary>An answered clarification as the chat numbers it (N1, N2 … — <see cref="PromptTemplates.AnsweredClarifications"/>).</summary>
public sealed record BookChatNote(int Id, int Number, string Text)
{
    public static IReadOnlyList<BookChatNote> Of(IEnumerable<Clarification> clarifications) =>
        PromptTemplates.AnsweredClarifications(clarifications).Select((c, i) => new BookChatNote(c.Id, i + 1, c.Answer)).ToList();
}

public sealed record BookChatChapter(int Id, int Number, string Title, string Content, string Outline = "", int? SectionId = null);

/// <summary>
/// The book chat's action catalogue: the JSON schema its replies follow, the parser, and the normalizer that
/// checks each proposed action against the book — resolving chapter numbers and names, keeping only the fields
/// an action may change, and dropping (with a note for the author) whatever cannot be applied.
/// </summary>
public static class BookChatActions
{
    public const string UpdatePremise = "update_premise";
    public const string UpdateStoryBible = "update_story_bible";
    public const string UpsertCharacter = "upsert_character";
    public const string ArchiveCharacter = "archive_character";
    public const string UpsertPlotThread = "upsert_plot_thread";
    public const string ArchivePlotThread = "archive_plot_thread";
    public const string AddChapter = "add_chapter";
    public const string UpdateChapterOutline = "update_chapter_outline";
    public const string ArchiveChapter = "archive_chapter";
    public const string MoveChapter = "move_chapter";
    public const string WriteChapter = "write_chapter";
    public const string ReviseChapter = "revise_chapter";
    public const string PatchChapter = "patch_chapter";
    public const string AddNote = "add_note";
    public const string UpdateNote = "update_note";
    public const string ArchiveNote = "archive_note";
    public const string SetPartRequests = "set_part_requests";
    public const string CheckOutline = "check_outline";
    public const string CheckChapter = "check_chapter";
    public const string SummarizeChapter = "summarize_chapter";
    public const string AddSection = "add_section";
    public const string UpdateSectionPlan = "update_section_plan";
    public const string MoveSection = "move_section";
    public const string ArchiveSection = "archive_section";
    public const string WriteSection = "write_section";
    public const string BreakDownSection = "break_down_section";

    /// <summary>Actions only offered before the premise and Story Bible are locked.</summary>
    public static readonly string[] FoundationOnly = [UpdatePremise, UpdateStoryBible];

    public static readonly string[] All =
    [
        UpdatePremise, UpdateStoryBible, UpsertCharacter, ArchiveCharacter, UpsertPlotThread, ArchivePlotThread,
        AddSection, UpdateSectionPlan, MoveSection, ArchiveSection, AddChapter, UpdateChapterOutline, ArchiveChapter,
        MoveChapter, WriteChapter, WriteSection, ReviseChapter, PatchChapter, AddNote, UpdateNote, ArchiveNote,
        SetPartRequests, CheckOutline, CheckChapter, SummarizeChapter, BreakDownSection,
    ];

    /// <summary>Actions that change chapter prose — they run the Writer, the Editor or the patcher.</summary>
    public static readonly string[] ProseActions = [WriteChapter, ReviseChapter, PatchChapter];

    /// <summary>
    /// Actions that re-run a generation step (outline check, chapter check, chapter summary, section breakdown). They
    /// change nothing themselves: what they find is posted as new proposals. They run after every other change of the
    /// batch, can be run again once done, and have nothing to undo.
    /// </summary>
    public static readonly string[] StepActions = [CheckOutline, CheckChapter, SummarizeChapter, BreakDownSection];

    public static bool IsStep(string type) => StepActions.Contains(type);

    /// <summary>
    /// Whether an applied proposal of this type can be undone: not a step, and not write_section — it only queues the
    /// section's chapter writes, which are undone one by one.
    /// </summary>
    public static bool HasUndo(string type) => !IsStep(type) && type != WriteSection;

    /// <summary>Most chapters a section is planned for or broken down into from the chat.</summary>
    public const int MaxSectionChapters = 6;

    /// <summary>The most actions one reply may propose; the rest are dropped.</summary>
    public const int MaxActions = 6;

    public static IReadOnlyList<string> Available(bool bibleLocked) =>
        bibleLocked ? All.Except(FoundationOnly).ToList() : All;

    public const string PartChapterText = "chapter_text";
    public const string PartChapterPlan = "chapter_plan";
    public const string PartCharacter = "character";
    public const string PartPlotThread = "plot_thread";
    public const string PartSectionPlan = "section_plan";
    /// <summary>The kinds of part a two-step reply may ask for.</summary>
    public static readonly string[] PartKinds = [PartChapterText, PartChapterPlan, PartSectionPlan, PartCharacter, PartPlotThread];

    /// <summary>
    /// The reply schema for the book's stage: once the bible is locked, bible actions do not exist. With
    /// <paramref name="partRequests"/> (the first step of a two-step reply) it also has the "parts" key.
    /// </summary>
    public static string ReplySchema(bool bibleLocked, bool partRequests = false)
    {
        var types = JsonSerializer.Serialize(Available(bibleLocked));
        var parts = partRequests
            ? $$"""
              ,
                "parts": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "kind": { "type": "string", "enum": {{JsonSerializer.Serialize(PartKinds)}} },
                      "chapter": { "type": ["integer", "null"] },
                      "section": { "type": ["integer", "null"] },
                      "name": { "type": ["string", "null"] }
                    },
                    "required": ["kind", "chapter", "section", "name"],
                    "additionalProperties": false
                  }
                }
              """
            : "";
        var required = partRequests ? "\"message\", \"conflicts\", \"actions\", \"parts\"" : "\"message\", \"conflicts\", \"actions\"";
        return $$"""
            {
              "type": "object",
              "properties": {
                "message": { "type": "string" },
                "conflicts": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "description": { "type": "string" },
                      "canon": { "type": "string" },
                      "chapter": { "type": ["integer", "null"] }
                    },
                    "required": ["description", "canon", "chapter"],
                    "additionalProperties": false
                  }
                },
                "actions": {
                  "type": "array",
                  "items": {
                    "type": "object",
                    "properties": {
                      "type": { "type": "string", "enum": {{types}} },
                      "label": { "type": "string" },
                      "chapter": { "type": ["integer", "null"] },
                      "section": { "type": ["integer", "null"] },
                      "name": { "type": ["string", "null"] },
                      "changes": {
                        "type": "array",
                        "items": {
                          "type": "object",
                          "properties": { "field": { "type": "string" }, "value": { "type": "string" } },
                          "required": ["field", "value"],
                          "additionalProperties": false
                        }
                      },
                      "instructions": { "type": ["string", "null"] },
                      "originalText": { "type": ["string", "null"] },
                      "replacementText": { "type": ["string", "null"] },
                      "text": { "type": ["string", "null"] },
                      "option": { "type": ["string", "null"] }
                    },
                    "required": ["type", "label", "chapter", "section", "name", "changes", "instructions", "originalText", "replacementText", "text", "option"],
                    "additionalProperties": false
                  }
                }{{parts}}
              },
              "required": [{{required}}],
              "additionalProperties": false
            }
            """;
    }

    /// <summary>The fields each action may change (canonical spelling).</summary>
    public static IReadOnlyList<string> FieldsOf(string type) => type switch
    {
        UpdateStoryBible => BookContentRender.BibleFields,
        UpsertCharacter => BookContentRender.CharacterFields,
        UpsertPlotThread => BookContentRender.ThreadFields,
        AddChapter or UpdateChapterOutline => BookContentRender.ChapterFields,
        AddSection or UpdateSectionPlan => BookContentRender.SectionPlanFields,
        MoveChapter or MoveSection => ["after"],
        BreakDownSection => [BookContentRender.PlannedChaptersField],
        _ => [],
    };

    // ── Parsing ─────────────────────────────────────────────────────────────────

    /// <summary>Reads a reply. Null when it holds no JSON object with a message — the caller may ask for a repair.</summary>
    public static ParsedChatReply? Parse(string raw)
    {
        JsonDocument doc;
        try { doc = PlanningParse.OpenObject(raw, "Chat reply"); }
        catch (FormatException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("message", out _)) return null;
            var message = LenientJson.Text(root, "message").Trim();

            var conflicts = Items(root, "conflicts")
                .Select(c => new ChatConflict(LenientJson.Text(c, "description").Trim(), LenientJson.Text(c, "canon").Trim(), LenientJson.Int(c, "chapter")))
                .Where(c => c.Description.Length > 0)
                .ToList();

            var actions = Items(root, "actions").Select(a => new RawChatAction(
                LenientJson.Text(a, "type").Trim().ToLowerInvariant(),
                LenientJson.Text(a, "label").Trim(),
                LenientJson.Int(a, "chapter"),
                NullIfBlank(LenientJson.Text(a, "name")),
                Changes(a),
                NullIfBlank(LenientJson.Text(a, "instructions")),
                NullIfEmpty(LenientJson.Text(a, "originalText")),
                a.TryGetProperty("replacementText", out var r) && r.ValueKind == JsonValueKind.String ? r.GetString() : null,
                NullIfBlank(LenientJson.Text(a, "text")),
                NullIfBlank(LenientJson.Text(a, "option"))?.ToUpperInvariant(),
                LenientJson.Int(a, "section"))).ToList();

            var parts = Items(root, "parts")
                .Select(p => p.ValueKind == JsonValueKind.String
                    ? PartFromText(p.GetString() ?? "")
                    : new ChatPartRequest(LenientJson.Text(p, "kind").Trim().ToLowerInvariant(), LenientJson.Int(p, "chapter"),
                        NullIfBlank(LenientJson.Text(p, "name")), LenientJson.Int(p, "section")))
                .Where(p => p.Kind.Length > 0)
                .ToList();

            return new ParsedChatReply(message, conflicts, actions, parts);
        }
    }

    /// <summary>A part asked for as plain text ("chapter_text 3", "character Mara") by a model that ignored the schema.</summary>
    private static ChatPartRequest PartFromText(string text)
    {
        var words = text.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0) return new("", null, null);
        var rest = words.Length > 1 ? words[1] : null;
        return int.TryParse(rest, out var n)
            ? new(words[0].ToLowerInvariant(), n, null)
            : new(words[0].ToLowerInvariant(), null, rest);
    }

    /// <summary>Accepts the schema's [{field, value}] list and also a plain {field: value} object.</summary>
    private static List<KeyValuePair<string, string>> Changes(JsonElement action)
    {
        if (!action.TryGetProperty("changes", out var c)) return [];
        if (c.ValueKind == JsonValueKind.Object)
            return c.EnumerateObject().Select(p => KeyValuePair.Create(p.Name, LenientJson.Text(p.Value))).ToList();
        if (c.ValueKind != JsonValueKind.Array) return [];
        return c.EnumerateArray()
            .Select(e => KeyValuePair.Create(LenientJson.Text(e, "field").Trim(), LenientJson.Text(e, "value")))
            .Where(kv => kv.Key.Length > 0)
            .ToList();
    }

    private static IEnumerable<JsonElement> Items(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray().ToList() : [];

    private static string? NullIfBlank(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    private static string? NullIfEmpty(string s) => string.IsNullOrEmpty(s) ? null : s;

    // ── Normalizing ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Checks the reply's actions against the book. Returns the actions that can be proposed, in order, and a
    /// note for each one dropped.
    /// </summary>
    public static (IReadOnlyList<NormalizedAction> Actions, IReadOnlyList<string> Dropped) Normalize(
        IReadOnlyList<RawChatAction> raw, BookChatState state, int maxActions = MaxActions)
    {
        var actions = new List<NormalizedAction>();
        var dropped = new List<string>();
        var chapters = state.Chapters.OrderBy(c => c.Number).ToList();
        var lastNumber = chapters.Count == 0 ? 0 : chapters[^1].Number;
        // Chapters this reply adds at the end: number → index of the add_chapter / add_section action in `actions`.
        var added = new Dictionary<int, int>();
        var sections = (state.Sections ?? []).OrderBy(s => s.Number).ToList();
        var lastSection = sections.Count == 0 ? 0 : sections[^1].Number;
        // Sections this reply adds at the end: number → index of the add_section action in `actions`.
        var addedSections = new Dictionary<int, int>();
        // The section at the end of the book while it is still an existing one: chapters added to it go at the end.
        int? tailSectionId = sections.Count == 0 ? null : sections[^1].Id;
        // New sections this reply adds at the end (by add_section, or by an add_chapter of a section of its own).
        var appendedSections = 0;
        List<BookChatChapter> ChaptersOf(BookChatSection s) => chapters.Where(c => c.SectionId == s.Id).ToList();
        static bool Written(BookChatChapter c) => c.Content.Trim().Length > 0;
        var characters = state.Characters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var threads = state.Threads.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (a, i) in raw.Select((a, i) => (a, i + 1)))
        {
            if (actions.Count >= maxActions) { dropped.Add($"#{i} {a.Type}: more than {maxActions} changes in one reply."); continue; }
            var error = Check(a, i);
            if (error is not null) dropped.Add($"#{i} {(a.Label.Length > 0 ? a.Label : a.Type)}: {error}");
        }
        return (actions, dropped);

        string? Check(RawChatAction a, int index)
        {
            if (!All.Contains(a.Type)) return $"unknown action \"{a.Type}\".";
            if (state.BibleLocked && FoundationOnly.Contains(a.Type))
                return "the premise and Story Bible are locked now that writing has started.";

            var (changes, unknown) = Fields(a);
            string? group = a.Option is { Length: > 0 } o ? o[..Math.Min(o.Length, 20)] : null;
            NormalizedAction Make(ChatActionPayload p, string label, string? target, int? dependsOn = null) =>
                new(p, a.Label.Length > 0 ? a.Label : label, target, dependsOn, group);
            string UnknownNote() => unknown.Count > 0 ? $" (ignored unknown field(s): {string.Join(", ", unknown)})" : "";

            switch (a.Type)
            {
                case UpdatePremise:
                    if (a.Text is null) return "no new premise text.";
                    actions.Add(Make(Payload(a.Type, text: a.Text), "Update the premise", BookContentService.PremiseTarget));
                    return null;

                case UpdateStoryBible:
                    if (changes.Count == 0) return "no Story Bible fields to change." + UnknownNote();
                    actions.Add(Make(Payload(a.Type, changes: changes), "Update the Story Bible", BookContentService.BibleTarget));
                    return null;

                case UpsertCharacter or UpsertPlotThread:
                {
                    if (a.Name is null) return "no name.";
                    var isCharacter = a.Type == UpsertCharacter;
                    var known = (isCharacter ? characters : threads).TryGetValue(a.Name, out var existing);
                    var name = known ? existing! : a.Name;
                    if (known && changes.Count == 0) return "nothing to change." + UnknownNote();
                    (isCharacter ? characters : threads).Add(name);
                    var what = isCharacter ? "character" : "plot thread";
                    actions.Add(Make(Payload(a.Type, name: name, changes: changes),
                        known ? $"Update {what} {name}" : $"Add {what} {name}",
                        isCharacter ? BookContentService.CharacterTarget(name) : BookContentService.ThreadTarget(name)));
                    return null;
                }

                case ArchiveCharacter or ArchivePlotThread:
                {
                    if (a.Name is null) return "no name.";
                    var isCharacter = a.Type == ArchiveCharacter;
                    if (!(isCharacter ? characters : threads).TryGetValue(a.Name, out var name))
                        return $"there is no {(isCharacter ? "character" : "plot thread")} called {a.Name}.";
                    actions.Add(Make(Payload(a.Type, name: name), $"Archive {(isCharacter ? "character" : "plot thread")} {name}",
                        isCharacter ? BookContentService.CharacterTarget(name) : BookContentService.ThreadTarget(name)));
                    return null;
                }

                // At the end of Section N: a chapter number of its own only when that is the end of the book.
                case AddChapter when a.Section is int sectionNumber:
                {
                    if (!changes.ContainsKey("title") && !changes.ContainsKey("outline")) return "no title or outline." + UnknownNote();
                    if (addedSections.ContainsKey(sectionNumber))
                        return $"Section {sectionNumber} is added in this same reply — give it its chapter count (\"chapters\") and break it down once it is applied.";
                    var section = sections.FirstOrDefault(s => s.Number == sectionNumber);
                    if (section is null) return $"the book has no Section {sectionNumber}.";
                    var title = changes.GetValueOrDefault("title", "").Trim();
                    var titlePart = title.Length > 0 ? $": {title}" : "";
                    if (section.Id == tailSectionId)
                    {
                        var number = lastNumber + added.Count + 1;
                        added[number] = actions.Count;
                        actions.Add(Make(Payload(a.Type, number: number, changes: changes) with { SectionId = section.Id, SectionNumber = sectionNumber },
                            $"Add Chapter {number} to Section {sectionNumber}{titlePart}", null));
                        return null;
                    }
                    // Mid-book: later chapters move down, so it cannot be referred to by number in the same reply.
                    var lastOfSection = ChaptersOf(section).LastOrDefault();
                    actions.Add(Make(Payload(a.Type, number: (lastOfSection?.Number ?? 0) + 1, changes: changes) with { SectionId = section.Id, SectionNumber = sectionNumber },
                        $"Add a chapter at the end of Section {sectionNumber}{titlePart}", null));
                    return null;
                }

                // "chapter" past the last one (or null) = at the end, as before.
                case AddChapter when a.Chapter is int anchor && anchor >= 0 && anchor < lastNumber:
                {
                    // Inserted after chapter `anchor` (0 = at the start): later chapters move down. It cannot be
                    // referred to by number in the same reply — its Write card comes once it is applied.
                    if (!changes.ContainsKey("title") && !changes.ContainsKey("outline")) return "no title or outline." + UnknownNote();
                    var after = anchor == 0 ? null : chapters.FirstOrDefault(c => c.Number == anchor);
                    if (anchor != 0 && after is null) return $"the book has no Chapter {anchor} to insert after.";
                    var title = changes.GetValueOrDefault("title", "").Trim();
                    actions.Add(Make(Payload(a.Type, number: anchor + 1, changes: changes) with { AfterChapterId = after?.Id, AtStart = after is null },
                        (anchor == 0 ? "Insert a chapter at the start" : $"Insert a chapter after Chapter {anchor}") + (title.Length > 0 ? $": {title}" : ""),
                        null));
                    return null;
                }

                case AddChapter:
                {
                    if (!changes.ContainsKey("title") && !changes.ContainsKey("outline")) return "no title or outline." + UnknownNote();
                    var number = lastNumber + added.Count + 1;
                    added[number] = actions.Count;
                    // It goes in a section of its own at the end.
                    appendedSections++;
                    tailSectionId = null;
                    var title = changes.GetValueOrDefault("title", "").Trim();
                    actions.Add(Make(Payload(a.Type, number: number, changes: changes),
                        title.Length > 0 ? $"Add Chapter {number}: {title}" : $"Add Chapter {number}", null));
                    return null;
                }

                case AddSection:
                {
                    if (!changes.ContainsKey("title") && !changes.ContainsKey("outline")) return "no title or outline (the section's plan)." + UnknownNote();
                    if (PlannedError(changes, 1) is { } error) return error;
                    var title = changes.GetValueOrDefault("title", "").Trim();
                    var planned = BookContentRender.Number(changes.GetValueOrDefault(BookContentRender.PlannedChaptersField)) ?? 1;
                    var detail = (planned > 1 ? $" ({planned} chapters)" : "") + (title.Length > 0 ? $": {title}" : "");
                    if (a.Section is int anchor && anchor >= 0 && anchor < lastSection)
                    {
                        // Inserted after Section `anchor` (0 = at the start); like an inserted chapter, it cannot be
                        // referred to by number in the same reply.
                        var after = anchor == 0 ? null : sections.FirstOrDefault(s => s.Number == anchor);
                        if (anchor != 0 && after is null) return $"the book has no Section {anchor} to insert after.";
                        actions.Add(Make(Payload(a.Type, changes: changes) with { SectionNumber = anchor + 1, AfterSectionId = after?.Id, AtStart = after is null },
                            (anchor == 0 ? "Insert a section at the start" : $"Insert a section after Section {anchor}") + detail, null));
                        return null;
                    }
                    var number = lastSection + appendedSections + 1;
                    var chapterNumber = lastNumber + added.Count + 1;
                    addedSections[number] = actions.Count;
                    added[chapterNumber] = actions.Count;
                    appendedSections++;
                    tailSectionId = null;
                    actions.Add(Make(Payload(a.Type, number: chapterNumber, changes: changes) with { SectionNumber = number },
                        $"Add Section {number}" + detail, null));
                    return null;
                }

                case UpdateSectionPlan or MoveSection or ArchiveSection or WriteSection or BreakDownSection:
                {
                    if (a.Section is not int number) return "no section number (\"section\").";
                    var section = sections.FirstOrDefault(s => s.Number == number);
                    if (section is null)
                    {
                        if (a.Type == BreakDownSection && addedSections.TryGetValue(number, out var addIndex))
                        {
                            if (PlannedError(changes, 2) is { } addedError) return addedError;
                            actions.Add(Make(Payload(a.Type, changes: changes) with { SectionNumber = number },
                                BreakDownLabel(number, changes), null, addIndex));
                            return null;
                        }
                        return addedSections.ContainsKey(number)
                            ? $"Section {number} is added in this same reply — put its plan in add_section."
                            : $"the book has no Section {number}.";
                    }
                    var own = ChaptersOf(section);
                    var written = own.Where(Written).ToList();
                    var target = BookContentService.SectionTarget(section.Id);
                    ChatActionPayload SectionPayload(Dictionary<string, string>? fields = null) =>
                        Payload(a.Type, changes: fields) with { SectionId = section.Id, SectionNumber = number };
                    switch (a.Type)
                    {
                        case UpdateSectionPlan:
                            if (changes.Count == 0) return "no plan fields to change." + UnknownNote();
                            if (PlannedError(changes, 1) is { } planError) return planError;
                            actions.Add(Make(SectionPayload(changes), $"Update the plan of Section {number}", target));
                            return null;
                        case MoveSection:
                        {
                            if (written.Count > 0) return $"Section {number} has written chapters — only sections with nothing written can be moved.";
                            var afterText = changes.GetValueOrDefault("after")?.Trim();
                            if (BookContentRender.Number(afterText) is null && afterText != "0")
                                return "no \"after\" section number (0 = to the start)." + UnknownNote();
                            var to = BookContentRender.Number(afterText) ?? 0;
                            var afterSection = to == 0 ? null : sections.FirstOrDefault(s => s.Number == to);
                            if (to != 0 && afterSection is null) return $"the book has no Section {to} to move it after.";
                            if (to == number || to == number - 1) return $"Section {number} is already there.";
                            actions.Add(Make(SectionPayload() with { AfterSectionId = afterSection?.Id, AtStart = afterSection is null },
                                to == 0 ? $"Move Section {number} to the start" : $"Move Section {number} after Section {to}", target));
                            return null;
                        }
                        case ArchiveSection:
                            if (written.Count > 0) return $"Section {number} has written chapters — only sections with nothing written can be removed.";
                            actions.Add(Make(SectionPayload(),
                                $"Remove Section {number}: {section.Title}" + (own.Count > 1 ? $" ({own.Count} chapters)" : ""), target));
                            return null;
                        case WriteSection:
                        {
                            var unwritten = own.Where(c => !Written(c)).ToList();
                            if (unwritten.Count == 0) return $"every chapter of Section {number} is written — revise them instead.";
                            if (own.Count == 1 && section.PlannedChapters > 1)
                                return $"Section {number} is planned for {section.PlannedChapters} chapters but has one — break it down first " +
                                       "(break_down_section), or plan it for 1 chapter (update_section_plan with \"chapters\" = 1).";
                            var list = string.Join("\n", unwritten.Select(c => $"Chapter {c.Number}: {c.Title}"));
                            actions.Add(Make(SectionPayload() with { Instructions = a.Instructions, Text = list },
                                unwritten.Count == 1
                                    ? $"Write Section {number} (Chapter {unwritten[0].Number})"
                                    : $"Write Section {number}: Chapters {string.Join(", ", unwritten.Select(c => c.Number))}, one at a time",
                                null));
                            return null;
                        }
                        default: // BreakDownSection — a step: no target
                            if (written.Count > 0)
                                return $"Section {number} has written chapters — it can no longer be broken down (split a chapter instead).";
                            if (PlannedError(changes, 2) is { } countError) return countError;
                            if (!changes.ContainsKey(BookContentRender.PlannedChaptersField) && section.PlannedChapters > 1)
                                changes[BookContentRender.PlannedChaptersField] = Math.Min(section.PlannedChapters, MaxSectionChapters).ToString();
                            actions.Add(Make(SectionPayload(changes), BreakDownLabel(number, changes), null));
                            return null;
                    }
                }

                case UpdateChapterOutline or ArchiveChapter or MoveChapter or WriteChapter or ReviseChapter or PatchChapter
                    or CheckOutline or CheckChapter or SummarizeChapter:
                {
                    if (a.Chapter is not int number) return "no chapter number.";
                    var chapter = chapters.FirstOrDefault(c => c.Number == number);
                    if (chapter is null)
                    {
                        if (added.TryGetValue(number, out var addIndex) && a.Type is WriteChapter or CheckOutline)
                        {
                            actions.Add(Make(Payload(a.Type, number: number, instructions: a.Instructions),
                                a.Type == WriteChapter ? $"Write Chapter {number}" : $"Check the outline of Chapter {number}",
                                null, addIndex));
                            return null;
                        }
                        return added.ContainsKey(number)
                            ? $"Chapter {number} is added in this same reply — put its outline in add_chapter."
                            : $"the book has no Chapter {number}.";
                    }
                    var hasProse = chapter.Content.Trim().Length > 0;
                    switch (a.Type)
                    {
                        case UpdateChapterOutline:
                            if (changes.Count == 0) return "no outline fields to change." + UnknownNote();
                            actions.Add(Make(Payload(a.Type, chapter.Id, number, changes: changes),
                                $"Update the outline of Chapter {number}", BookContentService.ChapterTarget(chapter.Id)));
                            return null;
                        case ArchiveChapter:
                            if (hasProse && (number != lastNumber || added.Count > 0))
                                return $"Chapter {number} is written — of the written chapters only the last one (Chapter {lastNumber}) can be removed.";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number),
                                $"Remove Chapter {number}: {chapter.Title}", BookContentService.ChapterTarget(chapter.Id)));
                            return null;
                        case MoveChapter:
                        {
                            if (hasProse) return $"Chapter {number} is written — only chapters not written yet can be moved.";
                            var afterText = changes.GetValueOrDefault("after")?.Trim();
                            if (BookContentRender.Number(afterText) is null && afterText != "0")
                                return "no \"after\" chapter number (0 = to the start)." + UnknownNote();
                            var target = BookContentRender.Number(afterText) ?? 0;
                            var afterChapter = target == 0 ? null : chapters.FirstOrDefault(c => c.Number == target);
                            if (target != 0 && afterChapter is null) return $"the book has no Chapter {target} to move it after.";
                            if (target == number || target == number - 1) return $"Chapter {number} is already there.";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number) with { AfterChapterId = afterChapter?.Id, AtStart = afterChapter is null },
                                target == 0 ? $"Move Chapter {number} to the start" : $"Move Chapter {number} after Chapter {target}",
                                BookContentService.ChapterTarget(chapter.Id)));
                            return null;
                        }
                        case WriteChapter:
                            actions.Add(Make(Payload(a.Type, chapter.Id, number, instructions: a.Instructions),
                                hasProse ? $"Rewrite Chapter {number} from its outline" : $"Write Chapter {number}",
                                BookContentService.ProseTarget(chapter.Id)));
                            return null;
                        case ReviseChapter:
                            if (!hasProse) return $"Chapter {number} is not written yet — write it first.";
                            if (a.Instructions is null) return "no instructions for the revision.";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number, instructions: a.Instructions),
                                $"Revise Chapter {number}", BookContentService.ProseTarget(chapter.Id)));
                            return null;
                        // Steps have no target: they change nothing, so they never supersede or block a change.
                        case CheckOutline:
                            if (chapter.Outline.Trim().Length == 0) return $"Chapter {number} has no outline to check.";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number),
                                $"Check the outline of Chapter {number}", null));
                            return null;
                        case CheckChapter:
                            if (!hasProse) return $"Chapter {number} is not written yet — check its outline instead.";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number),
                                $"Run the continuity check on Chapter {number}", null));
                            return null;
                        case SummarizeChapter:
                            if (!hasProse) return $"Chapter {number} is not written yet.";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number),
                                $"Re-summarize Chapter {number}", null));
                            return null;
                        default: // PatchChapter
                            if (!hasProse) return $"Chapter {number} is not written yet.";
                            if (a.OriginalText is null) return "no text to replace.";
                            var match = EditorAgent.FindPatchLocation(chapter.Content,
                                new CheckerIssue("continuity", "", "", a.OriginalText, a.ReplacementText ?? "", null));
                            if (match.Offset < 0) return $"the text to replace was not found in Chapter {number} ({match.Reason}).";
                            actions.Add(Make(Payload(a.Type, chapter.Id, number, originalText: a.OriginalText, replacementText: a.ReplacementText ?? ""),
                                $"Edit a passage of Chapter {number}", BookContentService.ProseTarget(chapter.Id)));
                            return null;
                    }
                }

                case AddNote:
                    if ((a.Text ?? a.Instructions) is not string note) return "no note text.";
                    actions.Add(Make(Payload(a.Type, text: note), "Note a decision", BookContentService.NoteTarget));
                    return null;

                case UpdateNote or ArchiveNote:
                {
                    var notes = state.Notes ?? [];
                    var reference = a.Name ?? a.Chapter?.ToString();
                    var number = reference is null ? null : BookContentRender.Number(reference.TrimStart('N', 'n', '[').TrimEnd(']'));
                    if (number is null) return "no note number (\"name\": \"N2\").";
                    var target = notes.FirstOrDefault(n => n.Number == number);
                    if (target is null) return $"there is no note N{number}.";
                    if (a.Type == ArchiveNote)
                    {
                        actions.Add(Make(Payload(a.Type, name: $"N{number}"), $"Archive note N{number}", BookContentService.NoteIdTarget(target.Id)));
                        return null;
                    }
                    if ((a.Text ?? a.Instructions) is not string text) return "no new note text.";
                    if (text.Trim() == target.Text.Trim()) return $"note N{number} already says that.";
                    actions.Add(Make(Payload(a.Type, name: $"N{number}", text: text), $"Update note N{number}", BookContentService.NoteIdTarget(target.Id)));
                    return null;
                }

                case SetPartRequests:
                {
                    if (OnOff(a.Text ?? a.Instructions) is not bool on) return "\"text\" must be \"on\" or \"off\".";
                    actions.Add(Make(Payload(a.Type, text: on ? "on" : "off"),
                        on ? "Let the chat read the parts of the book it needs before answering" : "Stop the chat asking for parts of the book",
                        BookContentService.PartRequestsTarget));
                    return null;
                }
            }
            return $"unknown action \"{a.Type}\".";
        }

        (Dictionary<string, string> Known, List<string> Unknown) Fields(RawChatAction a)
        {
            var allowed = FieldsOf(a.Type);
            var known = new Dictionary<string, string>();
            var unknown = new List<string>();
            foreach (var (field, value) in a.Changes)
            {
                var canonical = allowed.FirstOrDefault(f => f.Equals(field, StringComparison.OrdinalIgnoreCase));
                if (canonical is null) unknown.Add(field);
                else known[canonical] = value;
            }
            return (known, unknown);
        }
    }

    /// <summary>
    /// Checks the "chapters" field (how many chapters a section is planned for), when given: a number from
    /// <paramref name="min"/> to <see cref="MaxSectionChapters"/>, rewritten as plain digits. Null when it is fine.
    /// </summary>
    private static string? PlannedError(Dictionary<string, string> changes, int min)
    {
        if (!changes.TryGetValue(BookContentRender.PlannedChaptersField, out var value)) return null;
        if (BookContentRender.Number(value) is not int n || n < min || n > MaxSectionChapters)
            return $"\"chapters\" must be a number from {min} to {MaxSectionChapters}.";
        changes[BookContentRender.PlannedChaptersField] = n.ToString();
        return null;
    }

    private static string BreakDownLabel(int section, IReadOnlyDictionary<string, string> changes) =>
        changes.TryGetValue(BookContentRender.PlannedChaptersField, out var n)
            ? $"Break Section {section} down into {n} chapters"
            : $"Break Section {section} down into chapters";

    /// <summary>"on"/"off" and the usual synonyms; null when it is neither.</summary>
    public static bool? OnOff(string? text) => text?.Trim().ToLowerInvariant() switch
    {
        "on" or "true" or "yes" or "enable" or "enabled" or "1" => true,
        "off" or "false" or "no" or "disable" or "disabled" or "0" => false,
        _ => null,
    };

    private static ChatActionPayload Payload(
        string type, int? chapterId = null, int? number = null, string? name = null, Dictionary<string, string>? changes = null,
        string? instructions = null, string? originalText = null, string? replacementText = null, string? text = null) =>
        new(type, chapterId, number, name, changes ?? [], instructions, originalText, replacementText, text);
}
