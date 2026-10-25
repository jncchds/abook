using System.Text.Json;
using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Api.Telegram.Cards;

/// <summary>
/// What a card shows and where its wizard stands. Kept with the card (<see cref="TelegramCard.StateJson"/>); the card
/// message is drawn from it again after every change.
/// </summary>
public sealed class CardState
{
    /// <summary>newbook, books, book, bible, chars, char, threads, thread, chapters, chapter, clars, clar.</summary>
    public string Kind { get; set; } = "";
    /// <summary>The character / thread / chapter / clarification shown (null: a new one being added).</summary>
    public int? ItemId { get; set; }
    /// <summary>main, versions, version, archived, presets, full.</summary>
    public string View { get; set; } = "main";
    /// <summary>The field the next reply to the card fills.</summary>
    public string? Awaiting { get; set; }
    /// <summary>Fields changed and not yet saved (field key → new value).</summary>
    public Dictionary<string, string> Draft { get; set; } = new();
    /// <summary>The item's fields when editing began — Save warns when someone changed it since.</summary>
    public string? BaseHash { get; set; }
    public int Page { get; set; }
    public int? VersionId { get; set; }
    /// <summary>The field shown in full (View "full").</summary>
    public string? Focus { get; set; }
    /// <summary>A one-line status under the card: saved, why something was refused…</summary>
    public string? Note { get; set; }
    /// <summary>
    /// Set when someone other than the owner opened the card with /book in a chat that follows the book: the binding
    /// whose access they use (checked again on every press). Null for the owner's own cards.
    /// </summary>
    public int? BindingId { get; set; }

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    public string Serialize() => JsonSerializer.Serialize(this, Json);
    public static CardState Parse(string json) => JsonSerializer.Deserialize<CardState>(json, Json) ?? new CardState();

    /// <summary>Leaves the current item for another: drops the view, draft and reply being waited for.</summary>
    public void GoTo(string kind, int? itemId = null)
    {
        Kind = kind;
        ItemId = itemId;
        View = "main";
        Awaiting = null;
        Draft.Clear();
        BaseHash = null;
        Page = 0;
        VersionId = null;
        Focus = null;
    }
}

/// <summary>How a field is filled: by replying with text, a number, or picking one of a few values.</summary>
public enum FieldKind { Text, LongText, Number, Choice }

/// <summary>One editable field of a card.</summary>
public sealed record CardField(string Key, string Label, FieldKind Kind = FieldKind.Text, IReadOnlyList<string>? Choices = null, bool Required = false)
{
    public bool IsLong => Kind == FieldKind.LongText;
}

/// <summary>The fields of each kind of card, in the order shown. Keys are those of <see cref="BookContentRender"/>.</summary>
public static class CardFields
{
    public static readonly string[] Genres = ["Fantasy", "Sci-Fi", "Mystery", "Thriller", "Romance", "Horror", "Literary", "Historical", "Adventure"];
    public static readonly string[] Languages = ["English", "Russian", "Spanish", "German", "French", "Italian", "Portuguese"];
    public static readonly string[] Modes = [nameof(BookMode.Autonomous), nameof(BookMode.Interactive)];
    public static readonly string[] OnOff = ["on", "off"];

    public static readonly IReadOnlyList<CardField> NewBook =
    [
        new("title", "Title", Required: true),
        new("premise", "Premise", FieldKind.LongText, Required: true),
        new("genre", "Genre", FieldKind.Choice, Genres),
        new("language", "Language", FieldKind.Choice, Languages),
        new("mode", "Mode", FieldKind.Choice, Modes),
        new("chapters", "Chapters", FieldKind.Number),
    ];

    public static readonly IReadOnlyList<CardField> Book =
    [
        new("title", "Title", Required: true),
        new("premise", "Premise", FieldKind.LongText, Required: true),
        new("genre", "Genre", FieldKind.Choice, Genres),
        new("language", "Language", FieldKind.Choice, Languages),
        new("chapters", "Target chapters", FieldKind.Number),
        new("mode", "Mode", FieldKind.Choice, Modes),
        new("illustrations", "Illustrations", FieldKind.Choice, OnOff),
    ];

    public static readonly IReadOnlyList<CardField> Bible =
    [
        new("settingDescription", "Setting", FieldKind.LongText),
        new("timePeriod", "Time period"),
        new("themes", "Themes", FieldKind.LongText),
        new("toneAndStyle", "Tone and style", FieldKind.LongText),
        new("worldRules", "World rules", FieldKind.LongText),
        new("notes", "Notes", FieldKind.LongText),
    ];

    public static readonly IReadOnlyList<CardField> Character =
    [
        new("name", "Name", Required: true),
        new("role", "Role", FieldKind.Choice, Enum.GetNames<CharacterRole>()),
        new("physicalDescription", "Appearance", FieldKind.LongText),
        new("personality", "Personality", FieldKind.LongText),
        new("backstory", "Backstory", FieldKind.LongText),
        new("goalMotivation", "Goal", FieldKind.LongText),
        new("arc", "Arc", FieldKind.LongText),
        new("firstAppearanceChapterNumber", "First chapter", FieldKind.Number),
        new("notes", "Notes", FieldKind.LongText),
    ];

    public static readonly IReadOnlyList<CardField> Thread =
    [
        new("name", "Name", Required: true),
        new("description", "Description", FieldKind.LongText),
        new("type", "Type", FieldKind.Choice, Enum.GetNames<PlotThreadType>()),
        new("status", "Status", FieldKind.Choice, Enum.GetNames<PlotThreadStatus>()),
        new("introducedChapterNumber", "Introduced in", FieldKind.Number),
        new("resolvedChapterNumber", "Resolved in", FieldKind.Number),
    ];

    public static readonly IReadOnlyList<CardField> Chapter =
    [
        new("title", "Title", Required: true),
        new("outline", "Outline", FieldKind.LongText),
        new("povCharacter", "POV character"),
        new("charactersInvolved", "Characters (comma-separated)"),
        new("plotThreads", "Plot threads (comma-separated)"),
        new("foreshadowingNotes", "Foreshadowing", FieldKind.LongText),
        new("payoffNotes", "Payoffs", FieldKind.LongText),
    ];

    public static IReadOnlyList<CardField> For(string kind) => kind switch
    {
        "newbook" => NewBook,
        "book" => Book,
        "bible" => Bible,
        "char" => Character,
        "thread" => Thread,
        "chapter" => Chapter,
        _ => [],
    };

    public static Dictionary<string, string> Of(Book b) => new()
    {
        ["title"] = b.Title,
        ["premise"] = b.Premise,
        ["genre"] = b.Genre,
        ["language"] = b.Language,
        ["chapters"] = b.TargetChapterCount > 0 ? b.TargetChapterCount.ToString() : "",
        ["mode"] = b.Mode.ToString(),
        ["illustrations"] = b.IllustrationsEnabled ? "on" : "off",
    };

    /// <summary>
    /// A reply checked against its field: a number must be a whole number in range, a choice may also be typed
    /// (matched case-insensitively, or kept as typed for genre and language). Returns the value to store or an error.
    /// </summary>
    public static (string? Value, string? Error) Validate(CardField field, string text)
    {
        text = text.Trim();
        switch (field.Kind)
        {
            case FieldKind.Number:
                if (text.Length == 0 || text == "-") return ("", null);
                return int.TryParse(text, out var n) && n is >= 0 and <= 500
                    ? (n.ToString(), null)
                    : (null, $"{field.Label} must be a whole number.");
            case FieldKind.Choice:
                var match = field.Choices!.FirstOrDefault(c => c.Equals(text, StringComparison.OrdinalIgnoreCase));
                if (match is not null) return (match, null);
                // Genre and language may be anything; the others are fixed lists.
                if (field.Key is "genre" or "language" && text.Length is > 0 and <= 100) return (text, null);
                return (null, $"{field.Label} is one of: {string.Join(", ", field.Choices!)}.");
            default:
                if (field.Required && text.Length == 0) return (null, $"{field.Label} can't be empty.");
                return (text, null);
        }
    }
}
