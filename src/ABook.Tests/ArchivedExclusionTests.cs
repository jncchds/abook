using ABook.Api.Services;
using ABook.Core.Models;

namespace ABook.Tests;

/// <summary>
/// Archived entities are display-only history. They must never reach a model prompt nor an
/// exported manuscript — these tests pin that invariant at the export generators, which are the
/// last stage where an unfiltered <see cref="Book"/> could still leak archived prose.
/// </summary>
public class ArchivedChapterExportTests
{
    private static Book BookWithArchivedChapter() => new()
    {
        Id = 1,
        Title = "Test Book",
        Language = "English",
        Chapters =
        [
            new Chapter { Id = 1, Number = 1, Title = "Live Chapter", Content = "Active prose marker." },
            new Chapter { Id = 2, Number = 2, Title = "Dead Chapter", Content = "Archived prose marker.", IsArchived = true }
        ]
    };

    [Fact]
    public void GenerateHtml_ArchivedChapter_Excluded()
    {
        var html = BookExportService.GenerateHtml(BookWithArchivedChapter());
        Assert.Contains("Active prose marker.", html);
        Assert.DoesNotContain("Archived prose marker.", html);
        Assert.DoesNotContain("Dead Chapter", html);
    }

    [Fact]
    public void GenerateFb2_ArchivedChapter_Excluded()
    {
        var fb2 = BookExportService.GenerateFb2(BookWithArchivedChapter(), bible: null);
        Assert.Contains("Active prose marker.", fb2);
        Assert.DoesNotContain("Archived prose marker.", fb2);
    }

    [Fact]
    public void GenerateEpub_ArchivedChapter_Excluded()
    {
        var bytes = BookExportService.GenerateEpub(BookWithArchivedChapter(), bible: null);

        using var stream = new MemoryStream(bytes);
        using var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Read);
        var text = string.Concat(zip.Entries.Select(e =>
        {
            using var reader = new StreamReader(e.Open());
            return reader.ReadToEnd();
        }));

        Assert.Contains("Active prose marker.", text);
        Assert.DoesNotContain("Archived prose marker.", text);
    }

    [Fact]
    public void GenerateMetadataHtml_ArchivedChapter_Excluded()
    {
        var html = BookExportService.GenerateMetadataHtml(
            BookWithArchivedChapter(), bible: null, characters: [], plotThreads: [], messages: [], tokenUsage: []);

        Assert.Contains("Live Chapter", html);
        Assert.DoesNotContain("Dead Chapter", html);
    }
}

public class ArchivedChapterAgentGuardTests
{
    [Fact]
    public void EnsureNotArchived_ActiveChapter_ReturnsChapter()
    {
        var chapter = new Chapter { Id = 7, Number = 1, Title = "Live" };
        Assert.Same(chapter, ABook.Agents.AgentBase.EnsureNotArchived(chapter));
    }

    [Fact]
    public void EnsureNotArchived_ArchivedChapter_Throws()
    {
        var chapter = new Chapter { Id = 7, Number = 1, Title = "Dead", IsArchived = true };
        var ex = Assert.Throws<InvalidOperationException>(() => ABook.Agents.AgentBase.EnsureNotArchived(chapter));
        Assert.Contains("archived", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
