using ABook.Core.Models;

namespace ABook.Tests;

public class ChapterOrderingTests
{
    private static Chapter Ch(int id, int number, bool archived = false) =>
        new() { Id = id, Number = number, SortOrder = id, IsArchived = archived, Title = $"c{id}" };

    private static string Numbers(IEnumerable<Chapter> ordered) =>
        string.Join(" ", ordered.Select(c => c.IsArchived ? $"[{c.Id}]" : $"{c.Id}:{c.Number}"));

    [Fact]
    public void Apply_NumbersActiveChaptersOnly_ArchivedKeepPlaceAndOldNumber()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2, archived: true), Ch(3, 3), Ch(4, 4) };
        var before = ChapterOrdering.ActiveNumbers(list);
        var map = ChapterOrdering.Apply(list, before);

        Assert.Equal("1:1 [2] 3:2 4:3", Numbers(list));
        Assert.Equal(2, list[1].Number);                 // "was Ch. 2"
        Assert.Equal([1, 2, 3, 4], list.Select(c => c.SortOrder));
        Assert.Equal(new Dictionary<int, int> { [3] = 2, [4] = 3 }, map);
    }

    [Fact]
    public void Insert_ShiftsLaterNumbers()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2), Ch(3, 3) };
        var before = ChapterOrdering.ActiveNumbers(list);
        var added = new Chapter { Title = "new" };
        var ordered = ChapterOrdering.MoveAfter(list, added, list[0]);
        var map = ChapterOrdering.Apply(ordered, before);

        Assert.Equal(["c1", "new", "c2", "c3"], ordered.Select(c => c.Title));
        Assert.Equal(2, added.Number);
        Assert.Equal(new Dictionary<int, int> { [2] = 3, [3] = 4 }, map);
    }

    [Fact]
    public void Move_ToStart_AndAfter()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2), Ch(3, 3) };
        var before = ChapterOrdering.ActiveNumbers(list);
        var ordered = ChapterOrdering.MoveAfter(list, list[2], null);
        var map = ChapterOrdering.Apply(ordered, before);
        Assert.Equal("3:1 1:2 2:3", Numbers(ordered));
        Assert.Equal(new Dictionary<int, int> { [3] = 1, [1] = 2, [2] = 3 }, map);

        ordered = ChapterOrdering.MoveAfter(ordered, ordered[0], ordered[2]);
        ChapterOrdering.Apply(ordered, ChapterOrdering.ActiveNumbers(ordered));
        Assert.Equal("1:1 2:2 3:3", Numbers(ordered));
    }

    [Fact]
    public void Archiving_MapsItsNumberToTheNextChapter_OrThePreviousWhenLast()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2), Ch(3, 3) };
        var before = ChapterOrdering.ActiveNumbers(list);
        list[1].IsArchived = true;
        var map = ChapterOrdering.Apply(list, before);
        Assert.Equal(new Dictionary<int, int> { [3] = 2 }, map);   // 2 → next chapter, now number 2 too

        list = [Ch(1, 1), Ch(2, 2), Ch(3, 3)];
        before = ChapterOrdering.ActiveNumbers(list);
        list[2].IsArchived = true;
        map = ChapterOrdering.Apply(list, before);
        Assert.Equal(new Dictionary<int, int> { [3] = 2 }, map);   // last: maps to the one before
    }

    [Fact]
    public void Restore_BringsTheChapterBackInPlace()
    {
        var list = new List<Chapter> { Ch(1, 1), Ch(2, 2, archived: true), Ch(3, 2) };
        var before = ChapterOrdering.ActiveNumbers(list);
        list[1].IsArchived = false;
        var map = ChapterOrdering.Apply(list, before);
        Assert.Equal("1:1 2:2 3:3", Numbers(list));
        Assert.Equal(new Dictionary<int, int> { [2] = 3 }, map);
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
