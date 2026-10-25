using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Infrastructure.Rewind;

/// <summary>
/// Works out what a rewind changes, from the newest change-log revision of each row at the moment and the rows as they
/// are now (both as the change log's JSON). Pure: no database, so it is tested on its own.
/// </summary>
public static class RewindPlanner
{
    /// <summary>The book's own columns a rewind restores; its settings (prompts, LLM, mode, language, art direction) stay.</summary>
    public static readonly IReadOnlySet<string> BookFields = new HashSet<string>
    {
        nameof(Book.Title), nameof(Book.Premise), nameof(Book.Genre), nameof(Book.TargetChapterCount), nameof(Book.Status),
        nameof(Book.StoryBibleLockedAt), nameof(Book.ChatMemory), nameof(Book.ChatMemoryThroughMessageId),
        nameof(Book.ClarificationsStatus), nameof(Book.StoryBibleStatus), nameof(Book.CharactersStatus),
        nameof(Book.PlotThreadsStatus), nameof(Book.ChaptersStatus),
    };

    /// <summary>Never compared nor restored: identity and timestamps.</summary>
    private static readonly HashSet<string> Fixed = ["Id", "BookId", "CreatedAt", "UpdatedAt"];

    public sealed record RowKey(string Type, int Id);

    /// <summary>One planned change, with the JSON of the state to restore (null when archiving).</summary>
    public sealed record PlannedChange(RowKey Key, RewindChangeKind Kind, string Label, IReadOnlyList<string> Fields, string? TargetJson, string? CurrentJson);

    /// <param name="atMoment">Per row: the newest revision at or before the moment (rows without one did not exist yet).</param>
    /// <param name="current">Per row that exists now: its JSON.</param>
    public static IReadOnlyList<PlannedChange> Plan(
        IReadOnlyDictionary<RowKey, ContentRevision> atMoment, IReadOnlyDictionary<RowKey, string> current)
    {
        var changes = new List<PlannedChange>();
        foreach (var key in atMoment.Keys.Union(current.Keys).OrderBy(k => Order(k.Type)).ThenBy(k => k.Id))
        {
            var then = atMoment.TryGetValue(key, out var rev) && rev.Op != ContentRevisionOp.Delete ? rev.DataJson : null;
            var now = current.GetValueOrDefault(key);

            if (then is not null && now is not null)
            {
                var fields = Differences(key.Type, then, now);
                if (fields.Count == 1 && fields[0] == "IsArchived")
                    // Only archived or not: say so plainly ("archived" / "brought back"), not "restored (archived)".
                    changes.Add(new PlannedChange(key, IsArchivedIn(then) ? RewindChangeKind.Archive : RewindChangeKind.Recreate,
                        Label(key, then), [], then, now));
                else if (fields.Count > 0)
                    changes.Add(new PlannedChange(key, RewindChangeKind.Restore, Label(key, then), fields.Select(FieldWords).Distinct().ToList(), then, now));
            }
            else if (then is not null)
            {
                if (key.Type != nameof(Book))
                    changes.Add(new PlannedChange(key, RewindChangeKind.Recreate, Label(key, then), [], then, null));
            }
            else if (now is not null && NeedsArchive(key.Type, now))
                changes.Add(new PlannedChange(key, RewindChangeKind.Archive, Label(key, now), [], null, now));
        }
        return changes;
    }

    /// <summary>The columns that differ, among those a rewind restores.</summary>
    public static IReadOnlyList<string> Differences(string type, string thenJson, string nowJson)
    {
        using var then = JsonDocument.Parse(thenJson);
        using var now = JsonDocument.Parse(nowJson);
        var diff = new List<string>();
        foreach (var p in then.RootElement.EnumerateObject())
        {
            if (!IsRestored(type, p.Name)) continue;
            var nowValue = now.RootElement.TryGetProperty(p.Name, out var v) ? v.GetRawText() : "null";
            if (p.Value.GetRawText() != nowValue) diff.Add(p.Name);
        }
        return diff;
    }

    /// <summary>Whether a rewind writes this column back.</summary>
    public static bool IsRestored(string type, string field) =>
        type == nameof(Book) ? BookFields.Contains(field)
        // Pointing the summary at the right version is worked out when the chapter is restored.
        : !Fixed.Contains(field) && !(type == nameof(Chapter) && field == nameof(Chapter.SummaryVersionId));

    private static bool IsArchivedIn(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("IsArchived", out var a) && a.ValueKind == JsonValueKind.True;
    }

    /// <summary>A row created after the moment: archived, unless it already is (or is a proposal no longer open).</summary>
    private static bool NeedsArchive(string type, string nowJson)
    {
        using var doc = JsonDocument.Parse(nowJson);
        var root = doc.RootElement;
        return type switch
        {
            nameof(Book) => false,
            nameof(StoryBible) => true,
            nameof(ChatProposal) => root.TryGetProperty(nameof(ChatProposal.Status), out var s)
                && (ChatProposalStatus)s.GetInt32() is not (ChatProposalStatus.Rejected or ChatProposalStatus.Superseded),
            _ => !(root.TryGetProperty("IsArchived", out var a) && a.ValueKind == JsonValueKind.True),
        };
    }

    // Book first (its phase statuses), then content (sections before the chapters that point at them), proposals last.
    private static int Order(string type) => type switch
    {
        nameof(Book) => 0, nameof(StoryBible) => 1, nameof(CharacterCard) => 2, nameof(PlotThread) => 3,
        nameof(Section) => 4, nameof(Chapter) => 5, nameof(Clarification) => 6, _ => 7,
    };

    public static string Label(RowKey key, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        string S(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
        return key.Type switch
        {
            nameof(Book) => "The book (premise, title, planning progress, chat memory)",
            nameof(StoryBible) => "Story Bible",
            nameof(Chapter) => $"Chapter {(r.TryGetProperty("Number", out var n) ? n.GetRawText() : "?")}" +
                               (S("Title") is { Length: > 0 } t ? $": {t}" : ""),
            nameof(Section) => $"Section {(r.TryGetProperty("Number", out var sn) ? sn.GetRawText() : "?")}" +
                               (S("Title") is { Length: > 0 } st ? $": {st}" : ""),
            nameof(CharacterCard) => $"Character {S("Name")}",
            nameof(PlotThread) => $"Plot thread {S("Name")}",
            nameof(Clarification) => $"Clarification “{Short(S("Question"))}”",
            nameof(ChatProposal) => $"Book-chat suggestion “{Short(S("Label"))}”",
            _ => key.Type,
        };
    }

    private static string Short(string text) => text.Length <= 60 ? text : text[..57] + "…";

    /// <summary>"Content" → "text", "IsArchived" → "archived", "PhysicalDescription" → "physical description".</summary>
    public static string FieldWords(string field) => field switch
    {
        "Content" => "text",
        "IsArchived" => "archived",
        "StoryBibleLockedAt" => "bible lock",
        "ChatMemory" or "ChatMemoryThroughMessageId" => "chat memory",
        _ => string.Concat(field.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString())),
    };
}
