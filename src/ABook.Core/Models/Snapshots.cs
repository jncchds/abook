namespace ABook.Core.Models;

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
    public bool HumanAssisted { get; set; }
    public string Reason { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
}
