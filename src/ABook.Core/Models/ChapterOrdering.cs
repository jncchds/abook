namespace ABook.Core.Models;

/// <summary>
/// Keeps a book's chapter order and numbers consistent. A chapter's place is <see cref="Chapter.SortOrder"/> (archived
/// chapters included, so they keep their place); its <see cref="Chapter.Number"/> counts only active chapters.
/// Pure — the repository loads the rows, calls these and saves.
/// </summary>
public static class ChapterOrdering
{
    /// <summary>The chapters in book order (place, then id for rows that share one).</summary>
    public static List<Chapter> InOrder(IEnumerable<Chapter> chapters) =>
        chapters.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();

    /// <summary>The numbers of the active chapters (by id) — taken before a change, for <see cref="Apply"/>.</summary>
    public static Dictionary<int, int> ActiveNumbers(IEnumerable<Chapter> chapters) =>
        chapters.Where(c => !c.IsArchived && c.Id != 0)
                .GroupBy(c => c.Id).ToDictionary(g => g.Key, g => g.First().Number);

    /// <summary>
    /// Gives <paramref name="ordered"/> places 1..N and numbers its active chapters 1..K in that order. Archived chapters
    /// keep their last number. Returns how the numbers in <paramref name="before"/> (<see cref="ActiveNumbers"/> taken
    /// before the change) moved, for chapter numbers used elsewhere — a plot thread's "introduced in", a character's
    /// first appearance: old → new, unchanged numbers left out. A chapter archived by the change maps to the next active
    /// chapter after it (or the one before it when it was last).
    /// </summary>
    public static IReadOnlyDictionary<int, int> Apply(IReadOnlyList<Chapter> ordered, IReadOnlyDictionary<int, int> before)
    {
        var number = 0;
        for (var i = 0; i < ordered.Count; i++)
        {
            ordered[i].SortOrder = i + 1;
            if (!ordered[i].IsArchived) ordered[i].Number = ++number;
        }

        var map = new Dictionary<int, int>();
        for (var i = 0; i < ordered.Count; i++)
        {
            var c = ordered[i];
            if (c.Id == 0 || !before.TryGetValue(c.Id, out var old) || map.ContainsKey(old)) continue;
            var now = !c.IsArchived ? c.Number
                : ordered.Skip(i + 1).FirstOrDefault(x => !x.IsArchived)?.Number
                  ?? ordered.Take(i).LastOrDefault(x => !x.IsArchived)?.Number;
            if (now is int n && n != old) map[old] = n;
        }
        return map;
    }

    /// <summary>Moves <paramref name="chapter"/> to just after <paramref name="after"/> (null = to the start).</summary>
    public static List<Chapter> MoveAfter(IEnumerable<Chapter> ordered, Chapter chapter, Chapter? after)
    {
        var list = ordered.Where(c => !ReferenceEquals(c, chapter) && (c.Id == 0 || c.Id != chapter.Id)).ToList();
        var index = after is null ? 0 : list.FindIndex(c => ReferenceEquals(c, after) || (c.Id != 0 && c.Id == after.Id)) + 1;
        if (index < 0) index = list.Count;   // the anchor is gone: at the end
        list.Insert(index, chapter);
        return list;
    }

    /// <summary>
    /// The book's order after a re-plan gives the active chapters a new order: active chapters follow
    /// <paramref name="activeInOrder"/>, and each archived chapter stays right after the active chapter it followed
    /// before (the nearest earlier one that is still there), or at the start.
    /// </summary>
    public static List<Chapter> Arrange(IReadOnlyList<Chapter> previousOrder, IReadOnlyList<Chapter> activeInOrder)
    {
        var kept = activeInOrder.ToHashSet();
        var anchored = new Dictionary<Chapter, List<Chapter>>();
        var atStart = new List<Chapter>();
        Chapter? anchor = null;
        foreach (var c in previousOrder)
        {
            if (!c.IsArchived) { if (kept.Contains(c)) anchor = c; continue; }
            if (anchor is null) atStart.Add(c);
            else (anchored.TryGetValue(anchor, out var l) ? l : anchored[anchor] = []).Add(c);
        }
        var result = new List<Chapter>(atStart);
        foreach (var c in activeInOrder)
        {
            result.Add(c);
            if (anchored.TryGetValue(c, out var after)) result.AddRange(after);
        }
        return result;
    }

    /// <summary>Applies a number map to a nullable chapter number.</summary>
    public static int? Remap(int? number, IReadOnlyDictionary<int, int> map) =>
        number is int n && map.TryGetValue(n, out var to) ? to : number;
}
