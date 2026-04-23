namespace ABook.Core.Models;

public class PlotThread
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public PlotThreadType Type { get; set; } = PlotThreadType.MainPlot;
    public int? IntroducedChapterNumber { get; set; }
    public int? ResolvedChapterNumber { get; set; }
    public PlotThreadStatus Status { get; set; } = PlotThreadStatus.Active;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    public Book Book { get; set; } = null!;
}
