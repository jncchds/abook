using System.Text.Json;
using ABook.Core.Models;

namespace ABook.Agents;

/// <summary>A conflict with the book's canon the LLM flagged in a reply.</summary>
public sealed record ChatConflict(string Description, string Canon, int? Chapter);

/// <summary>One action as the LLM wrote it, before it is checked against the book.</summary>
public sealed record RawChatAction(
    string Type, string Label, int? Chapter, string? Name, IReadOnlyList<KeyValuePair<string, string>> Changes,
    string? Instructions, string? OriginalText, string? ReplacementText, string? Text, string? Option);

/// <summary>A parsed reply: the message to the author, the conflicts it flags and the changes it proposes.</summary>
public sealed record ParsedChatReply(string Message, IReadOnlyList<ChatConflict> Conflicts, IReadOnlyList<RawChatAction> Actions);

/// <summary>A checked action, resolved against the book — what a <see cref="ChatProposal"/> stores as its payload.</summary>
/// <param name="AfterChapterId">add_chapter / move_chapter: the chapter it goes after (with <paramref name="AtStart"/> false; neither = at the end).</param>
/// <param name="UndoAfterChapterId">move_chapter, set when applied: where it was (after this chapter, or at the start) — for undo.</param>
public sealed record ChatActionPayload(
    string Type, int? ChapterId, int? ChapterNumber, string? Name, Dictionary<string, string> Changes,
    string? Instructions, string? OriginalText, string? ReplacementText, string? Text,
    int? AfterChapterId = null, bool AtStart = false, int? UndoAfterChapterId = null, bool UndoAtStart = false)
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
    IReadOnlyList<BookChatChapter> Chapters);

public sealed record BookChatChapter(int Id, int Number, string Title, string Content, string Outline = "");

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
    public const string CheckOutline = "check_outline";
    public const string CheckChapter = "check_chapter";
    public const string SummarizeChapter = "summarize_chapter";

    /// <summary>Actions only offered before the premise and Story Bible are locked.</summary>
    public static readonly string[] FoundationOnly = [UpdatePremise, UpdateStoryBible];

    public static readonly string[] All =
    [
        UpdatePremise, UpdateStoryBible, UpsertCharacter, ArchiveCharacter, UpsertPlotThread, ArchivePlotThread,
        AddChapter, UpdateChapterOutline, ArchiveChapter, MoveChapter, WriteChapter, ReviseChapter, PatchChapter, AddNote,
        CheckOutline, CheckChapter, SummarizeChapter,
    ];

    /// <summary>Actions that change chapter prose — they run the Writer, the Editor or the patcher.</summary>
    public static readonly string[] ProseActions = [WriteChapter, ReviseChapter, PatchChapter];

    /// <summary>
    /// Actions that re-run a generation step (outline check, chapter check, chapter summary). They change nothing
    /// themselves: what they find is posted as new proposals. They run after every other change of the batch, can be
    /// run again once done, and have nothing to undo.
    /// </summary>
    public static readonly string[] StepActions = [CheckOutline, CheckChapter, SummarizeChapter];

    public static bool IsStep(string type) => StepActions.Contains(type);

    /// <summary>The most actions one reply may propose; the rest are dropped.</summary>
    public const int MaxActions = 6;

    public static IReadOnlyList<string> Available(bool bibleLocked) =>
        bibleLocked ? All.Except(FoundationOnly).ToList() : All;

    /// <summary>The reply schema for the book's stage: once the bible is locked, bible actions do not exist.</summary>
    public static string ReplySchema(bool bibleLocked)
    {
        var types = JsonSerializer.Serialize(Available(bibleLocked));
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
                    "required": ["type", "label", "chapter", "name", "changes", "instructions", "originalText", "replacementText", "text", "option"],
                    "additionalProperties": false
                  }
                }
              },
              "required": ["message", "conflicts", "actions"],
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
        MoveChapter => ["after"],
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
                NullIfBlank(LenientJson.Text(a, "option"))?.ToUpperInvariant())).ToList();

            return new ParsedChatReply(message, conflicts, actions);
        }
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
        IReadOnlyList<RawChatAction> raw, BookChatState state)
    {
        var actions = new List<NormalizedAction>();
        var dropped = new List<string>();
        var chapters = state.Chapters.OrderBy(c => c.Number).ToList();
        var lastNumber = chapters.Count == 0 ? 0 : chapters[^1].Number;
        // Chapters this reply adds: number → index of the add_chapter action in `actions`.
        var added = new Dictionary<int, int>();
        var characters = state.Characters.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var threads = state.Threads.ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var (a, i) in raw.Select((a, i) => (a, i + 1)))
        {
            if (actions.Count >= MaxActions) { dropped.Add($"#{i} {a.Type}: more than {MaxActions} changes in one reply."); continue; }
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
                    var title = changes.GetValueOrDefault("title", "").Trim();
                    actions.Add(Make(Payload(a.Type, number: number, changes: changes),
                        title.Length > 0 ? $"Add Chapter {number}: {title}" : $"Add Chapter {number}", null));
                    return null;
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

    private static ChatActionPayload Payload(
        string type, int? chapterId = null, int? number = null, string? name = null, Dictionary<string, string>? changes = null,
        string? instructions = null, string? originalText = null, string? replacementText = null, string? text = null) =>
        new(type, chapterId, number, name, changes ?? [], instructions, originalText, replacementText, text);
}
