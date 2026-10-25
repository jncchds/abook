using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

public class ChapterSummaryTests
{
    [Fact]
    public void Parse_KeepsSummaryAndFacts_DropsKnownCharactersAndUnknownThreads()
    {
        const string raw = """
            {"summary":"Mara steals the map.","facts":["Mara has green eyes", " "],
             "newCharacters":[{"name":"Mara","role":"Protagonist","description":"x"},
                              {"name":"Tobin","role":"minor","description":"A ferryman."}],
             "threadChanges":[{"name":"the map","status":"resolved","note":"Found."},
                              {"name":"Invented","status":"Active","note":"?"}]}
            """;

        var result = ChapterSummarizer.Parse(raw, ["mara"], ["The Map"]);

        Assert.Equal("Mara steals the map.\nEstablished facts:\n- Mara has green eyes", result.Summary);
        var tobin = Assert.Single(result.NewCharacters);
        Assert.Equal(("Tobin", CharacterRole.Minor), (tobin.Name, tobin.Role));
        var thread = Assert.Single(result.ThreadChanges);
        Assert.Equal(("The Map", PlotThreadStatus.Resolved), (thread.Name, thread.Status));
    }

    [Fact]
    public void Parse_WithoutSummary_Throws()
    {
        Assert.Throws<FormatException>(() =>
            ChapterSummarizer.Parse("""{"summary":"","facts":[],"newCharacters":[],"threadChanges":[]}""", [], []));
    }

    [Fact]
    public void Gist_PrefersTheSummaryOnOneLine_OverTheOutline()
    {
        Assert.Equal("Planned.", PromptTemplates.Gist(new Chapter { Outline = "Planned." }));
        Assert.Equal("Happened. Established facts: - A",
            PromptTemplates.Gist(new Chapter { Outline = "Planned.", Summary = "Happened.\nEstablished facts:\n- A" }));
    }

    [Fact]
    public void ChapterCount_ZeroIsOpenEnded()
    {
        Assert.Equal("open-ended", PromptTemplates.ChapterCount(new Book { TargetChapterCount = 0 }));
        Assert.Equal("12", PromptTemplates.ChapterCount(new Book { TargetChapterCount = 12 }));
    }

    [Fact]
    public void WriterSystem_NeverStatesAChapterTotal()
    {
        // The target counts sections, and a section may have several chapters: no "4 of 9".
        var chapter = new Chapter { Number = 4, Title = "Ash" };
        Assert.Contains("Current chapter: 4 — \"Ash\"",
            PromptTemplates.WriterSystem("base", new Book { TargetChapterCount = 0 }, chapter, "", ""));
        Assert.Contains("Current chapter: 4 — \"Ash\"",
            PromptTemplates.WriterSystem("base", new Book { TargetChapterCount = 9 }, chapter, "", ""));
    }
}
