namespace ABook.Core.Models;

/// <summary>
/// Keeps a book's sections and chapters in a consistent order. A chapter's place is <see cref="Chapter.SortOrder"/>
/// (archived chapters included, so they keep their place) and a section's chapters always sit together, in the order
/// of the sections. Numbers count only what is active: <see cref="Chapter.Number"/> over active chapters,
/// <see cref="Section.Number"/> over active sections; a section is archived exactly when none of its chapters is
/// active. Pure — the repository loads the rows, calls these and saves.
/// </summary>
public static class ChapterOrdering
{
    /// <summary>The chapters in book order (place, then id for rows that share one).</summary>
    public static List<Chapter> InOrder(IEnumerable<Chapter> chapters) =>
        chapters.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToList();

    /// <summary>The numbers of the active sections (by id) — taken before a change, for <see cref="Apply"/>.</summary>
    public static Dictionary<int, int> ActiveSectionNumbers(IEnumerable<Section> sections) =>
        sections.Where(s => !s.IsArchived && s.Id != 0)
                .GroupBy(s => s.Id).ToDictionary(g => g.Key, g => g.First().Number);

    /// <summary>What <see cref="Apply"/> produced.</summary>
    /// <param name="Chapters">All chapters in their new order (each section's chapters together).</param>
    /// <param name="Sections">The sections that have chapters, in order.</param>
    /// <param name="SectionNumberMap">
    /// How section numbers used elsewhere (a plot thread's "introduced in", a character's first appearance) move:
    /// old → new, unchanged numbers left out. A section archived by the change maps to the next active section after
    /// it (or the one before it when it was last).
    /// </param>
    public sealed record Result(List<Chapter> Chapters, List<Section> Sections, IReadOnlyDictionary<int, int> SectionNumberMap);

    /// <summary>
    /// Puts the book in the order of <paramref name="flat"/> (every chapter, each with its <see cref="Chapter.Section"/>
    /// set): sections in the order their first chapter appears, each section's chapters together in their order. Then
    /// gives places and numbers to both, and archives exactly the sections with no active chapter.
    /// <paramref name="sectionNumbersBefore"/> is <see cref="ActiveSectionNumbers"/> taken before the change.
    /// </summary>
    public static Result Apply(IReadOnlyList<Chapter> flat, IReadOnlyDictionary<int, int> sectionNumbersBefore)
    {
        var sections = new List<Section>();
        var bySection = new Dictionary<Section, List<Chapter>>(ReferenceEqualityComparer.Instance);
        foreach (var c in flat)
        {
            var s = c.Section ?? throw new InvalidOperationException($"Chapter {c.Id} has no section.");
            if (!bySection.TryGetValue(s, out var list)) { bySection[s] = list = []; sections.Add(s); }
            list.Add(c);
        }

        var chapters = new List<Chapter>(flat.Count);
        int chapterNumber = 0, sectionNumber = 0;
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            s.SortOrder = i + 1;
            s.IsArchived = bySection[s].All(c => c.IsArchived);
            if (!s.IsArchived) s.Number = ++sectionNumber;
            foreach (var c in bySection[s])
            {
                chapters.Add(c);
                c.SortOrder = chapters.Count;
                if (!c.IsArchived) c.Number = ++chapterNumber;
            }
        }

        var map = new Dictionary<int, int>();
        for (var i = 0; i < sections.Count; i++)
        {
            var s = sections[i];
            if (s.Id == 0 || !sectionNumbersBefore.TryGetValue(s.Id, out var old) || map.ContainsKey(old)) continue;
            var now = !s.IsArchived ? s.Number
                : sections.Skip(i + 1).FirstOrDefault(x => !x.IsArchived)?.Number
                  ?? sections.Take(i).LastOrDefault(x => !x.IsArchived)?.Number;
            if (now is int n && n != old) map[old] = n;
        }
        return new Result(chapters, sections, map);
    }

    /// <summary>Moves <paramref name="chapter"/> to just after <paramref name="after"/> (null = to the start).</summary>
    public static List<Chapter> MoveAfter(IEnumerable<Chapter> ordered, Chapter chapter, Chapter? after)
    {
        var list = ordered.Where(c => !ReferenceEquals(c, chapter) && (c.Id == 0 || c.Id != chapter.Id)).ToList();
        var index = after is null ? 0 : list.FindIndex(c => ReferenceEquals(c, after) || (c.Id != 0 && c.Id == after.Id)) + 1;
        if (index <= 0 && after is not null) index = list.Count;   // the anchor is gone: at the end
        list.Insert(index, chapter);
        return list;
    }

    /// <summary>
    /// Moves a whole section (its chapters, in order) to just after the chapters of <paramref name="after"/>
    /// (null = to the start).
    /// </summary>
    public static List<Chapter> MoveSectionAfter(IEnumerable<Chapter> ordered, Section section, Section? after)
    {
        var all = ordered.ToList();
        var moving = all.Where(c => ReferenceEquals(c.Section, section)).ToList();
        var rest = all.Where(c => !ReferenceEquals(c.Section, section)).ToList();
        var index = after is null ? 0 : rest.FindLastIndex(c => ReferenceEquals(c.Section, after)) + 1;
        if (index <= 0 && after is not null) index = rest.Count;
        rest.InsertRange(index, moving);
        return rest;
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

    /// <summary>Applies a number map to a nullable number.</summary>
    public static int? Remap(int? number, IReadOnlyDictionary<int, int> map) =>
        number is int n && map.TryGetValue(n, out var to) ? to : number;
}
