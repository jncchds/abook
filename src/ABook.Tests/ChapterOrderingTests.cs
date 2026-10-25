using ABook.Core.Models;

namespace ABook.Tests;

public class ChapterOrderingTests
{
    /// <summary>Chapter <paramref name="id"/> in its own section (id + 100), the default one chapter per section.</summary>
    private static Chapter Ch(int id, int number, bool archived = false, Section? section = null)
    {
        section ??= new Section { Id = id + 100, Number = number, IsArchived = archived, Title = $"s{id}" };
        return new Chapter { Id = id, Number = number, SortOrder = id, IsArchived = archived, Title = $"c{id}", Section = section };
    }

    private static Dictionary<int, int> Before(IEnumerable<Chapter> chapters) =>
        ChapterOrdering.ActiveSectionNumbers(chapters.Select(c => c.Section!).Distinct());

    private static string Numbers(IEnumerable<Chapter> ordered) =>
        string.Join(" ", ordered.Select(c => c.IsArchived ? $"[{c.Id}]" : $"{c.Id}:{c.Number}"));

    [Fact]
    public void Apply_NumbersActiveChaptersOnly_ArchivedKeepPlaceAndOldNumber()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2, archived: true), Ch(3, 3), Ch(4, 4) };
        var r = ChapterOrdering.Apply(list, Before(list));

        Assert.Equal("1:1 [2] 3:2 4:3", Numbers(r.Chapters));
        Assert.Equal(2, list[1].Number);                 // "was Ch. 2"
        Assert.Equal([1, 2, 3, 4], r.Chapters.Select(c => c.SortOrder));
        Assert.True(list[1].Section!.IsArchived);        // its section has no active chapter
        Assert.Equal([1, 2, 3, 4], r.Sections.Select(s => s.SortOrder));
        Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 3 }, r.SectionNumberMap);
    }

    [Fact]
    public void Insert_NewSection_ShiftsLaterNumbers()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2), Ch(3, 3) };
        var before = Before(list);
        var added = new Chapter { Title = "new", Section = new Section() };
        var r = ChapterOrdering.Apply(ChapterOrdering.MoveAfter(list, added, list[0]), before);

        Assert.Equal(["c1", "new", "c2", "c3"], r.Chapters.Select(c => c.Title));
        Assert.Equal(2, added.Number);
        Assert.Equal(2, added.Section!.Number);
        Assert.Equal(new Dictionary<int, int> { [2] = 3, [3] = 4 }, r.SectionNumberMap);
    }

    [Fact]
    public void ChaptersOfASectionStayTogether_InTheOrderOfTheSections()
    {
        var big = new Section { Id = 200, Number = 2 };
        var a = Ch(1, 1); var b1 = Ch(2, 2, section: big); var c = Ch(3, 4); var b2 = Ch(4, 3, section: big);
        // b2 was put after c by mistake: it is pulled back next to b1, and sections keep the order of their first chapter.
        var r = ChapterOrdering.Apply([a, b1, c, b2], Before([a, b1, c, b2]));

        Assert.Equal("1:1 2:2 4:3 3:4", Numbers(r.Chapters));
        Assert.Equal([1, 2, 3], r.Sections.Select(s => s.Number));
        Assert.Same(big, r.Sections[1]);
    }

    [Fact]
    public void MoveSection_MovesAllItsChapters()
    {
        var big = new Section { Id = 200, Number = 1 };
        var a1 = Ch(1, 1, section: big); var a2 = Ch(2, 2, section: big); var b = Ch(3, 3);
        b.Section!.Number = 2;
        var list = new List<Chapter> { a1, a2, b };
        var before = Before(list);
        var r = ChapterOrdering.Apply(ChapterOrdering.MoveSectionAfter(list, big, b.Section), before);

        Assert.Equal("3:1 1:2 2:3", Numbers(r.Chapters));
        Assert.Equal(new Dictionary<int, int> { [2] = 1, [1] = 2 }, r.SectionNumberMap);
    }

    [Fact]
    public void ChapterNumbersCountChapters_SectionNumbersCountSections()
    {
        var big = new Section { Id = 200, Number = 1 };
        var list = new List<Chapter> { Ch(1, 1, section: big), Ch(2, 2, section: big), Ch(3, 3) };
        var r = ChapterOrdering.Apply(list, Before(list));

        Assert.Equal("1:1 2:2 3:3", Numbers(r.Chapters));
        Assert.Equal(1, big.Number);
        Assert.Equal(2, list[2].Section!.Number);
        Assert.False(big.IsArchived);

        list[0].IsArchived = true;   // one of two: the section stays
        ChapterOrdering.Apply(list, Before(list));
        Assert.False(big.IsArchived);
        list[1].IsArchived = true;   // both: archived
        var r2 = ChapterOrdering.Apply(list, Before(list));
        Assert.True(big.IsArchived);
        Assert.Equal(1, list[2].Section!.Number);
        Assert.Equal(new Dictionary<int, int> { [2] = 1 }, r2.SectionNumberMap);   // 1 → next section, also number 1
    }

    [Fact]
    public void Archiving_MapsItsNumberToTheNextSection_OrThePreviousWhenLast()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2), Ch(3, 3) };
        var before = Before(list);
        list[2].IsArchived = true;
        var r = ChapterOrdering.Apply(list, before);
        Assert.Equal(new Dictionary<int, int> { [3] = 2 }, r.SectionNumberMap);
    }

    [Fact]
    public void Restore_BringsTheChapterBackInPlace()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2, archived: true), Ch(3, 2) };
        var before = Before(list);
        list[1].IsArchived = false;
        var r = ChapterOrdering.Apply(list, before);
        Assert.Equal("1:1 2:2 3:3", Numbers(r.Chapters));
        Assert.False(list[1].Section!.IsArchived);
        Assert.Equal(new Dictionary<int, int> { [2] = 3 }, r.SectionNumberMap);
    }

    [Fact]
    public void Arrange_KeepsArchivedChaptersAfterTheChapterTheyFollowed()
    {
        var a = Ch(1, 1); var x = Ch(2, 1, archived: true); var b = Ch(3, 2); var y = Ch(4, 2, archived: true);
        var c = Ch(5, 3);
        var previous = new List<Chapter> { a, x, b, y, c };
        var added = new Chapter { Title = "new" };

        // Re-plan: b dropped, a new chapter between a and c.
        var result = ChapterOrdering.Arrange(previous, [a, added, c]);

        // x followed a; y followed b, which is gone, so it now follows a too (the nearest earlier chapter kept).
        Assert.Equal(["c1", "c2", "c4", "new", "c5"], result.Select(r => r.Title));
    }

    [Fact]
    public void Arrange_ArchivedAtTheStartStayFirst()
    {
        var x = Ch(1, 1, archived: true); var a = Ch(2, 1); var b = Ch(3, 2);
        var result = ChapterOrdering.Arrange([x, a, b], [b, a]);
        Assert.Equal(["c1", "c3", "c2"], result.Select(r => r.Title));
    }

    [Fact]
    public void Remap_LeavesUnmappedAndNullAlone()
    {
        var map = new Dictionary<int, int> { [3] = 4 };
        Assert.Equal(4, ChapterOrdering.Remap(3, map));
        Assert.Equal(5, ChapterOrdering.Remap(5, map));
        Assert.Null(ChapterOrdering.Remap(null, map));
    }
}
