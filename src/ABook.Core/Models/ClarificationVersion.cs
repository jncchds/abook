using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// Immutable snapshot of a <see cref="Clarification"/>'s question and answer. The repository records one whenever
/// they change, whoever changes them (web UI, Telegram, MCP, the planner, the book chat); a manual edit on top of a
/// manual version amends it (<see cref="EditSources"/>). Archiving is not a version — the row keeps that flag.
/// </summary>
public class ClarificationVersion
{
    public int Id { get; set; }
    public int ClarificationId { get; set; }
    public int BookId { get; set; }
    public int VersionNumber { get; set; }

    public string Question { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;

    /// <summary>"user", "telegram", "agent:Planner", "agent:BookChat", "restore:vN", "rewind", "initial"</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public Clarification Clarification { get; set; } = null!;
}
