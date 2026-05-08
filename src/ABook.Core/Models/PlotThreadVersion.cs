using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// Immutable snapshot of a <see cref="PlotThread"/> at a specific point in time.
/// A new version is created on every user edit and on initial agent generation.
/// </summary>
public class PlotThreadVersion
{
    public int Id { get; set; }
    public int PlotThreadId { get; set; }
    public int BookId { get; set; }
    public int VersionNumber { get; set; }

    // Snapshot of all PlotThread fields at this version
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PlotThreadType Type { get; set; } = PlotThreadType.MainPlot;
    public int? IntroducedChapterNumber { get; set; }
    public int? ResolvedChapterNumber { get; set; }
    public PlotThreadStatus Status { get; set; } = PlotThreadStatus.Active;

    /// <summary>"user", "agent:PlotThreads", "restore"</summary>
    public string CreatedBy { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public PlotThread PlotThread { get; set; } = null!;
}
