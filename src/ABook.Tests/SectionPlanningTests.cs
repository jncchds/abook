using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

public class SectionPlanningTests
{
    [Fact]
    public void SectionOutlines_ReadThePlannedChapterCount_DefaultOne_Clamped()
    {
        var sections = PlannerAgent.ParseSectionOutlines(3, """
            [{"number":1,"title":"A","outline":"a","chapters":2},
             {"number":2,"title":"B","outline":"b"},
             {"number":3,"title":"C","outline":"c","chapters":40},
             {"number":4,"title":"D","outline":"d","chapters":"3"}]
            """);

        Assert.Equal([2, 1, 6, 3], sections.Select(s => s.PlannedChapters));
        Assert.Equal([1, 2, 3, 4], sections.Select(s => s.Number));
        Assert.All(sections, s => Assert.Equal(3, s.BookId));
        Assert.Equal("a", sections[0].Outline);
    }

    [Fact]
    public void SectionContext_OnlyForASectionWithSeveralChapters()
    {
        var section = new Section { Id = 7, Number = 2, Title = "The Flight", Outline = "They escape the city." };
        var one = new Chapter { Id = 1, Number = 3, Title = "Gate", Outline = "At the gate." };
        var two = new Chapter { Id = 2, Number = 4, Title = "Road", Outline = "On the road." };

        Assert.Equal("", PromptTemplates.SectionContext(section, [one], one));

        var text = PromptTemplates.SectionContext(section, [one, two], two);
        Assert.Contains("Section 2: The Flight", text);
        Assert.Contains("part 2 of 2", text);
        Assert.Contains("They escape the city.", text);
        Assert.Contains("→ Chapter 4: Road", text);
        Assert.Contains("- Chapter 3: Gate", text);
    }

    [Fact]
    public void BreakdownPrompt_MarksTheSection_AndAsksForTheCount()
    {
        var book = new Book { Title = "T", Genre = "G", Premise = "P", Language = "English" };
        var a = new Section { Id = 1, Number = 1, Title = "One", Outline = "first" };
        var b = new Section { Id = 2, Number = 2, Title = "Two", Outline = "second" };

        var fixedCount = PromptTemplates.SectionBreakdownUser(book, null, [], [a, b], b, 3, "");
        Assert.Contains("→ Section 2: Two", fixedCount);
        Assert.Contains("  Section 1: One", fixedCount);
        Assert.Contains("exactly 3 chapters", fixedCount);

        Assert.Contains("2 to 4", PromptTemplates.SectionBreakdownUser(book, null, [], [a, b], b, null, ""));
    }
}
