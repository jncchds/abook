namespace ABook.Core.Models;

/// <summary>
/// Who made an edit, as versions and snapshots record it. The author's own edits — in the web UI or a Telegram card —
/// are <b>manual</b>: a manual edit on top of a manual version amends that version instead of adding another, so a run
/// of small fixes leaves one version; any other change (an agent, the book chat, a restore) starts a new one. The
/// change log (<see cref="ContentRevision"/>) still keeps every save for a rewind.
/// </summary>
public static class EditSources
{
    /// <summary>A version (CreatedBy) the author saved in the web UI.</summary>
    public const string User = "user";
    /// <summary>A version (CreatedBy) the author saved in a Telegram card.</summary>
    public const string Telegram = "telegram";
    /// <summary>
    /// A book or Story Bible snapshot (Reason) holding the state the author saved in the web UI. Older snapshots with
    /// "user-update" / "telegram" hold the state <em>before</em> such an edit and are not manual in this sense.
    /// </summary>
    public const string UserEdit = "user-edit";
    /// <summary>A snapshot holding the state the author saved in a Telegram card.</summary>
    public const string TelegramEdit = "telegram-edit";

    public static bool IsManual(string? source) => source is User or Telegram or UserEdit or TelegramEdit;
}

public class StoryBibleSnapshot
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string SettingDescription { get; set; } = string.Empty;
    public string TimePeriod { get; set; } = string.Empty;
    public string Themes { get; set; } = string.Empty;
    public string ToneAndStyle { get; set; } = string.Empty;
    public string WorldRules { get; set; } = string.Empty;
    public string Notes { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}

public class CharactersSnapshot
{
    public int Id { get; set; }
    public int BookId { get; set; }
    /// <summary>JSON-serialised CharacterCard[].</summary>
    public string DataJson { get; set; } = "[]";
    public string Reason { get; set; } = string.Empty;
    /// <summary>"phase-reset" when cleared via phase action bar; "edit" when an individual card is updated.</summary>
    public string Source { get; set; } = "phase-reset";
    public DateTime CreatedAt { get; set; }
}

public class PlotThreadsSnapshot
{
    public int Id { get; set; }
    public int BookId { get; set; }
    /// <summary>JSON-serialised PlotThread[].</summary>
    public string DataJson { get; set; } = "[]";
    public string Reason { get; set; } = string.Empty;
    /// <summary>"phase-reset" when cleared via phase action bar; "edit" when an individual thread is updated.</summary>
    public string Source { get; set; } = "phase-reset";
    public DateTime CreatedAt { get; set; }
}

public class BookSnapshot
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Premise { get; set; } = string.Empty;
    public string Genre { get; set; } = string.Empty;
    public int TargetChapterCount { get; set; }
    public string Language { get; set; } = string.Empty;
    /// <summary>Legacy (up to v0.3.1): the book's human-assisted flag. Newer snapshots record <see cref="Mode"/>.</summary>
    public bool HumanAssisted { get; set; }
    public BookMode Mode { get; set; } = BookMode.Autonomous;
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
