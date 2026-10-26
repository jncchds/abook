using System.Text.Json;
using ABook.Core.Models;

namespace ABook.Agents;

/// <summary>
/// Renders one piece of book content as a flat JSON object of text fields — the form proposals are
/// previewed, diffed and undone in. Field names match the action catalogue the book chat uses; numbers
/// are written as text ("" for none) and lists as comma-separated text.
/// </summary>
public static class BookContentRender
{
    public static readonly string[] BibleFields =
        ["settingDescription", "timePeriod", "themes", "toneAndStyle", "worldRules", "notes"];

    /// <summary>Character fields the chat may change; the name identifies the card and is not one of them.</summary>
    public static readonly string[] CharacterFields =
        ["role", "physicalDescription", "personality", "backstory", "goalMotivation", "arc", "firstAppearanceChapterNumber", "notes"];

    public static readonly string[] ThreadFields =
        ["description", "type", "status", "introducedChapterNumber", "resolvedChapterNumber"];

    public static readonly string[] ChapterFields =
        ["title", "outline", "povCharacter", "charactersInvolved", "plotThreads", "foreshadowingNotes", "payoffNotes"];

    private static readonly JsonSerializerOptions Options = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public static Dictionary<string, string> Fields(StoryBible b) => new()
    {
        ["settingDescription"] = b.SettingDescription,
        ["timePeriod"] = b.TimePeriod,
        ["themes"] = b.Themes,
        ["toneAndStyle"] = b.ToneAndStyle,
        ["worldRules"] = b.WorldRules,
        ["notes"] = b.Notes,
    };

    public static Dictionary<string, string> Fields(CharacterCard c) => new()
    {
        ["name"] = c.Name,
        ["role"] = c.Role.ToString(),
        ["physicalDescription"] = c.PhysicalDescription,
        ["personality"] = c.Personality,
        ["backstory"] = c.Backstory,
        ["goalMotivation"] = c.GoalMotivation,
        ["arc"] = c.Arc,
        ["firstAppearanceChapterNumber"] = c.FirstAppearanceChapterNumber?.ToString() ?? "",
        ["notes"] = c.Notes,
    };

    public static Dictionary<string, string> Fields(PlotThread t) => new()
    {
        ["name"] = t.Name,
        ["description"] = t.Description,
        ["type"] = t.Type.ToString(),
        ["status"] = t.Status.ToString(),
        ["introducedChapterNumber"] = t.IntroducedChapterNumber?.ToString() ?? "",
        ["resolvedChapterNumber"] = t.ResolvedChapterNumber?.ToString() ?? "",
    };

    public static Dictionary<string, string> Fields(Chapter c) => new()
    {
        ["number"] = c.Number.ToString(),
        ["title"] = c.Title,
        ["outline"] = c.Outline,
        ["povCharacter"] = c.PovCharacter,
        ["charactersInvolved"] = string.Join(", ", AgentBase.ParseNameList(c.CharactersInvolvedJson)),
        ["plotThreads"] = string.Join(", ", AgentBase.ParseNameList(c.PlotThreadsJson)),
        ["foreshadowingNotes"] = c.ForeshadowingNotes,
        ["payoffNotes"] = c.PayoffNotes,
    };

    /// <summary>A section's plan, with the same field names as a chapter's.</summary>
    public static Dictionary<string, string> Fields(Section s) => new()
    {
        ["number"] = s.Number.ToString(),
        ["title"] = s.Title,
        ["outline"] = s.Outline,
        ["povCharacter"] = s.PovCharacter,
        ["charactersInvolved"] = string.Join(", ", AgentBase.ParseNameList(s.CharactersInvolvedJson)),
        ["plotThreads"] = string.Join(", ", AgentBase.ParseNameList(s.PlotThreadsJson)),
        ["foreshadowingNotes"] = s.ForeshadowingNotes,
        ["payoffNotes"] = s.PayoffNotes,
    };

    public static string Render(Section? s) => s is null ? "" : Json(Fields(s));

    /// <summary>How many chapters a section is planned for (<see cref="Section.PlannedChapters"/>), as the chat names it.</summary>
    public const string PlannedChaptersField = "chapters";

    /// <summary>The fields the chat's section actions may set: a chapter's plan fields plus the planned chapter count.</summary>
    public static readonly string[] SectionPlanFields = [.. ChapterFields, PlannedChaptersField];

    /// <summary>A section's plan with its planned chapter count — what the chat's section target renders as.</summary>
    public static Dictionary<string, string> PlanFields(Section s)
    {
        var fields = Fields(s);
        fields[PlannedChaptersField] = s.PlannedChapters.ToString();
        return fields;
    }

    public static string RenderPlan(Section? s) => s is null ? "" : Json(PlanFields(s));

    public static string Json(IReadOnlyDictionary<string, string> fields) => JsonSerializer.Serialize(fields, Options);

    public static string Render(StoryBible? b) => b is null ? "" : Json(Fields(b));
    public static string Render(CharacterCard? c) => c is null ? "" : Json(Fields(c));
    public static string Render(PlotThread? t) => t is null ? "" : Json(Fields(t));
    public static string Render(Chapter? c) => c is null ? "" : Json(Fields(c));

    /// <summary>Reads a rendering back into its fields; empty for "" (the thing did not exist).</summary>
    public static Dictionary<string, string> Parse(string? rendered)
    {
        if (string.IsNullOrWhiteSpace(rendered)) return [];
        return JsonSerializer.Deserialize<Dictionary<string, string>>(rendered) ?? [];
    }

    /// <summary>The fields after <paramref name="changes"/> are laid over <paramref name="current"/>.</summary>
    public static Dictionary<string, string> Merge(IReadOnlyDictionary<string, string> current, IReadOnlyDictionary<string, string> changes)
    {
        var merged = new Dictionary<string, string>(current);
        foreach (var (k, v) in changes) merged[k] = v;
        return merged;
    }

    /// <summary>A comma-separated list as the JSON array the chapter columns store.</summary>
    public static string NameListJson(string commaSeparated) =>
        JsonSerializer.Serialize(commaSeparated.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public static int? Number(string? text) =>
        int.TryParse(text?.Trim(), out var n) && n > 0 ? n : null;
}
