using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using System.Text;

namespace ABook.Infrastructure.Repositories;

/// <summary>
/// Repository for all book-related data access. Each public method creates its own short-lived
/// <see cref="AppDbContext"/> via the injected factory where needed.
/// <para>
/// <b>Known limitations:</b>
/// <list type="bullet">
/// <item><description>Token usage records are flushed per-LLM-call (one SaveChanges each). For
/// high-throughput scenarios, consider batching.</description></item>
/// </list>
/// </para>
/// </summary>
public class BookRepository : IBookRepository
{
    private readonly AppDbContext _db;

    public BookRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<Book>> GetAllAsync(int? userId = null)
    {
        if (!userId.HasValue)
            return Enumerable.Empty<Book>();
        var query = _db.Books.Where(b => b.UserId == userId || b.UserId == null);
        return await query.OrderByDescending(b => b.UpdatedAt).ToListAsync();
    }

    public async Task<Book?> GetByIdAsync(int id) =>
        await WithClarificationsAsync(await _db.Books.FindAsync(id));

    /// <summary>
    /// Every single-book load brings the active clarifications along — prompts append them to the
    /// premise. Read untracked on each load, so they are always current and never saved via the book.
    /// </summary>
    private async Task<Book?> WithClarificationsAsync(Book? book)
    {
        if (book is not null)
            book.Clarifications = await _db.Clarifications.AsNoTracking()
                .Where(c => c.BookId == book.Id && !c.IsArchived)
                .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
                .ToListAsync();
        return book;
    }

    /// <summary>
    /// Loads a book with all of its chapters (archived included), LLM config, and Story Bible.
    /// Uses AsSplitQuery to avoid the Cartesian explosion when a book has many chapters.
    /// Intended for the UI, which renders archived chapters in their own section.
    /// </summary>
    public async Task<Book?> GetByIdWithDetailsAsync(int id) =>
        await WithClarificationsAsync(await _db.Books
            .Include(b => b.StoryBible)
            .Include(b => b.Chapters.OrderBy(c => c.SortOrder))
            .Include(b => b.Sections.OrderBy(s => s.SortOrder))
            .Include(b => b.LlmConfigurations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id));

    /// <summary>
    /// Same as <see cref="GetByIdWithDetailsAsync"/> but the chapter collection excludes archived
    /// chapters. Use this everywhere the loaded book feeds generation or export — archived content
    /// exists only to be displayed in the archived sections and must never reach a model or an
    /// exported manuscript.
    /// </summary>
    public async Task<Book?> GetByIdWithActiveDetailsAsync(int id) =>
        await WithClarificationsAsync(await _db.Books
            .Include(b => b.StoryBible)
            .Include(b => b.Chapters.Where(c => !c.IsArchived).OrderBy(c => c.SortOrder))
            .Include(b => b.Sections.Where(s => !s.IsArchived).OrderBy(s => s.SortOrder))
            .Include(b => b.LlmConfigurations)
            .AsSplitQuery()
            .FirstOrDefaultAsync(b => b.Id == id));

    public async Task<IReadOnlyList<int>> GetAncestryBookIdsAsync(int bookId, CancellationToken ct = default)
    {
        // Single recursive CTE instead of one query per ancestor.
        // LIMIT 50 acts as both a cycle guard and a depth cap.
        var ids = await _db.Database
            .SqlQuery<int>($"""
                WITH RECURSIVE ancestry AS (
                    SELECT "Id", "BaseBookId", 0 AS depth
                    FROM "Books"
                    WHERE "Id" = {bookId}
                    UNION ALL
                    SELECT b."Id", b."BaseBookId", a.depth + 1
                    FROM "Books" b
                    JOIN ancestry a ON b."Id" = a."BaseBookId"
                    WHERE a."BaseBookId" IS NOT NULL AND a.depth < 49
                )
                SELECT "Id" FROM ancestry ORDER BY depth
                """)
            .ToListAsync(ct);
        return ids;
    }

    public async Task<IReadOnlyList<AncestorBookContext>> GetAncestorPlanningContextAsync(int bookId, CancellationToken ct = default)
    {
        var chain = await GetAncestryBookIdsAsync(bookId, ct);
        var ancestorIds = chain.Skip(1).ToArray();
        if (ancestorIds.Length == 0) return [];

        var books = await _db.Books
            .Where(b => ancestorIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, ct);

        var clarificationsByBook = await _db.Clarifications.AsNoTracking()
            .Where(c => ancestorIds.Contains(c.BookId) && !c.IsArchived)
            .GroupBy(c => c.BookId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToList(), ct);
        foreach (var (id, book) in books)
            book.Clarifications = clarificationsByBook.GetValueOrDefault(id) ?? [];

        var bibles = await _db.StoryBibles
            .Where(s => ancestorIds.Contains(s.BookId))
            .ToDictionaryAsync(s => s.BookId, ct);

        var charactersByBook = await _db.CharacterCards
            .Where(c => ancestorIds.Contains(c.BookId) && !c.IsArchived)
            .GroupBy(c => c.BookId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderBy(x => x.Name).ToList(), ct);

        var threadsByBook = await _db.PlotThreads
            .Where(p => ancestorIds.Contains(p.BookId) && !p.IsArchived)
            .GroupBy(p => p.BookId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderBy(x => x.IntroducedChapterNumber).ThenBy(x => x.Name).ToList(), ct);

        var chaptersByBook = await _db.Chapters
            .Where(c => ancestorIds.Contains(c.BookId) && !c.IsArchived)
            .GroupBy(c => c.BookId)
            .ToDictionaryAsync(g => g.Key, g => g.OrderBy(x => x.SortOrder).ToList(), ct);

        // The first id after the book itself is its direct parent.
        return ancestorIds
            .Where(books.ContainsKey)
            .Select(id => new AncestorBookContext(
                books[id],
                bibles.GetValueOrDefault(id),
                charactersByBook.GetValueOrDefault(id) ?? [],
                threadsByBook.GetValueOrDefault(id) ?? [],
                chaptersByBook.GetValueOrDefault(id) ?? [],
                IsDirectParent: id == ancestorIds[0]))
            .ToList();
    }

    public async Task<Book> AddAsync(Book book)
    {
        book.CreatedAt = book.UpdatedAt = DateTime.UtcNow;
        _db.Books.Add(book);

        // The new book gets a copy of its owner's current default preset; it is never tied to the preset.
        var preset = book.UserId is int userId ? await GetDefaultPresetAsync(userId) : null;
        if (preset is not null)
        {
            var config = new LlmConfiguration { Book = book };
            config.CopyFrom(preset);
            _db.LlmConfigurations.Add(config);
        }

        await _db.SaveChangesAsync();
        return book;
    }

    public async Task UpdateAsync(Book book)
    {
        book.UpdatedAt = DateTime.UtcNow;
        _db.Books.Update(book);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteAsync(int id)
    {
        var book = await _db.Books.FindAsync(id);
        if (book is not null)
        {
            _db.Books.Remove(book);
            await _db.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<Chapter>> GetChaptersAsync(int bookId, bool includeArchived = false)
    {
        var query = _db.Chapters.Where(c => c.BookId == bookId);
        if (!includeArchived) query = query.Where(c => !c.IsArchived);
        return await query.OrderBy(c => c.SortOrder).ThenBy(c => c.Id).ToListAsync();
    }

    public async Task<Chapter?> GetChapterAsync(int bookId, int chapterId) =>
        await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId);

    public async Task<Chapter> AddChapterAsync(Chapter chapter)
    {
        var book = await LoadStructureAsync(chapter.BookId);
        if (chapter.SectionId is int sectionId)
        {
            // At the end of its section.
            chapter.Section = FindSection(book, sectionId);
            var last = book.Ordered.LastOrDefault(c => ReferenceEquals(c.Section, chapter.Section));
            return await SaveNewChapterAsync(chapter, ChapterOrdering.MoveAfter(book.Ordered, chapter, last), book);
        }
        NewSectionFor(chapter);   // a new section at the end
        book.Ordered.Add(chapter);
        return await SaveNewChapterAsync(chapter, book.Ordered, book);
    }

    public async Task<Chapter> InsertChapterAsync(Chapter chapter, int? afterChapterId, bool sameSection = false)
    {
        var book = await LoadStructureAsync(chapter.BookId);
        var after = FindAnchor(book.Ordered, afterChapterId);
        if (chapter.SectionId is int sectionId)
        {
            chapter.Section = FindSection(book, sectionId);
            if (after is not null && !ReferenceEquals(after.Section, chapter.Section))
                throw new InvalidOperationException($"Chapter {after.Id} is not in section {sectionId}.");
            // after == null: first in the section.
            var anchor = after ?? book.Ordered.TakeWhile(c => !ReferenceEquals(c.Section, chapter.Section)).LastOrDefault();
            return await SaveNewChapterAsync(chapter, ChapterOrdering.MoveAfter(book.Ordered, chapter, anchor), book);
        }
        if (sameSection && after is not null)
        {
            chapter.Section = after.Section;
            return await SaveNewChapterAsync(chapter, ChapterOrdering.MoveAfter(book.Ordered, chapter, after), book);
        }
        // A new section of its own, after the anchor's whole section (or first).
        var section = NewSectionFor(chapter);
        var ordered = ChapterOrdering.MoveAfter(book.Ordered, chapter, null);
        ordered = ChapterOrdering.MoveSectionAfter(ordered, section, after?.Section);
        return await SaveNewChapterAsync(chapter, ordered, book);
    }

    private async Task<Chapter> SaveNewChapterAsync(Chapter chapter, List<Chapter> ordered, BookStructure book)
    {
        chapter.CreatedAt = chapter.UpdatedAt = DateTime.UtcNow;
        _db.Chapters.Add(chapter);
        ApplyStructure(ordered, book);
        await _db.SaveChangesAsync();
        return chapter;
    }

    /// <summary>A new section holding <paramref name="chapter"/>, with its plan (tracked, not saved).</summary>
    private Section NewSectionFor(Chapter chapter)
    {
        var section = new Section { BookId = chapter.BookId, CreatedAt = DateTime.UtcNow, UpdatedAt = DateTime.UtcNow };
        section.CopyPlanFrom(chapter);
        _db.Sections.Add(section);
        chapter.Section = section;
        return section;
    }

    public async Task MoveChapterAsync(int bookId, int chapterId, int? afterChapterId)
    {
        var book = await LoadStructureAsync(bookId);
        var chapter = book.Ordered.FirstOrDefault(c => c.Id == chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        if (afterChapterId == chapterId) return;
        var after = FindAnchor(book.Ordered, afterChapterId);
        var alone = book.Ordered.Count(c => ReferenceEquals(c.Section, chapter.Section)) == 1;
        List<Chapter> ordered;
        if (alone)
            // A chapter alone in its section moves with it: the section goes after the anchor's section.
            ordered = ChapterOrdering.MoveSectionAfter(book.Ordered, chapter.Section!,
                after is null || ReferenceEquals(after.Section, chapter.Section) ? null : after.Section);
        else if (after is null)
        {
            // First in its own section.
            var before = book.Ordered.TakeWhile(c => !ReferenceEquals(c.Section, chapter.Section)).LastOrDefault();
            ordered = ChapterOrdering.MoveAfter(book.Ordered, chapter, before);
        }
        else
        {
            chapter.Section = after.Section;   // joins the anchor's section
            ordered = ChapterOrdering.MoveAfter(book.Ordered, chapter, after);
        }
        ApplyStructure(ordered, book);
        await _db.SaveChangesAsync();
    }

    public async Task MoveChapterToSectionAsync(int bookId, int chapterId, int sectionId, int? afterChapterId)
    {
        var book = await LoadStructureAsync(bookId);
        var chapter = book.Ordered.FirstOrDefault(c => c.Id == chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found.");
        var section = FindSection(book, sectionId);
        var after = FindAnchor(book.Ordered, afterChapterId);
        if (after is not null && !ReferenceEquals(after.Section, section))
            throw new InvalidOperationException($"Chapter {after.Id} is not in section {sectionId}.");
        if (after?.Id == chapterId) return;
        chapter.Section = section;
        var anchor = after ?? book.Ordered.TakeWhile(c => !ReferenceEquals(c.Section, section)).LastOrDefault(c => c.Id != chapterId);
        ApplyStructure(ChapterOrdering.MoveAfter(book.Ordered, chapter, anchor), book);   // an emptied section is removed
        await _db.SaveChangesAsync();
    }

    public async Task MoveSectionAsync(int bookId, int sectionId, int? afterSectionId)
    {
        var book = await LoadStructureAsync(bookId);
        var section = FindSection(book, sectionId);
        if (afterSectionId == sectionId) return;
        var after = afterSectionId is int a ? FindSection(book, a) : null;
        ApplyStructure(ChapterOrdering.MoveSectionAfter(book.Ordered, section, after), book);
        await _db.SaveChangesAsync();
    }

    private static Chapter? FindAnchor(List<Chapter> ordered, int? afterChapterId) =>
        afterChapterId is int id
            ? ordered.FirstOrDefault(c => c.Id == id) ?? throw new InvalidOperationException($"Chapter {id} not found.")
            : null;

    private static Section FindSection(BookStructure book, int sectionId) =>
        book.Sections.FirstOrDefault(s => s.Id == sectionId)
        ?? throw new InvalidOperationException($"Section {sectionId} not found.");

    /// <summary>A book's sections and chapters, tracked, with the active sections' numbers before a change.</summary>
    private sealed record BookStructure(List<Chapter> Ordered, List<Section> Sections, Dictionary<int, int> SectionNumbersBefore);

    /// <summary>
    /// Loads the book's chapters (archived included, in book order) and sections, tracked, so a change can be applied
    /// and saved. A chapter without a section (made before sections existed) gets one of its own.
    /// </summary>
    private async Task<BookStructure> LoadStructureAsync(int bookId)
    {
        var sections = await _db.Sections.Where(s => s.BookId == bookId).ToListAsync();
        var chapters = ChapterOrdering.InOrder(await _db.Chapters.Where(c => c.BookId == bookId).ToListAsync());
        var before = ChapterOrdering.ActiveSectionNumbers(sections);
        foreach (var c in chapters.Where(c => c.Section is null))
            sections.Add(NewSectionFor(c));
        return new BookStructure(chapters, sections, before);
    }

    /// <summary>
    /// Writes places and numbers for <paramref name="ordered"/> (sections and chapters), removes sections left without
    /// chapters, and moves the section numbers that plot threads and characters point at along with them. Tracked
    /// changes only (the change log must see them); the caller saves.
    /// </summary>
    private void ApplyStructure(IReadOnlyList<Chapter> ordered, BookStructure book)
    {
        var now = DateTime.UtcNow;
        var chapterPlaces = ordered.Where(c => c.Id != 0).ToDictionary(c => c.Id, c => (c.SortOrder, c.Number, c.SectionId));
        var sectionPlaces = book.Sections.Where(s => s.Id != 0).ToDictionary(s => s.Id, s => (s.SortOrder, s.Number, s.IsArchived));
        var result = ChapterOrdering.Apply(ordered, book.SectionNumbersBefore);
        foreach (var c in result.Chapters)
            if (c.Id != 0 && chapterPlaces[c.Id] != (c.SortOrder, c.Number, c.Section?.Id == 0 ? null : c.Section?.Id)) c.UpdatedAt = now;
        foreach (var s in result.Sections)
            if (s.Id != 0 && sectionPlaces[s.Id] != (s.SortOrder, s.Number, s.IsArchived)) s.UpdatedAt = now;
        foreach (var empty in book.Sections.Where(s => !result.Sections.Contains(s)))
            _db.Sections.Remove(empty);

        var map = result.SectionNumberMap;
        if (map.Count == 0 || ordered.Count == 0) return;
        var bookId = ordered[0].BookId;
        foreach (var t in _db.PlotThreads.Where(t => t.BookId == bookId).ToList())
        {
            var introduced = ChapterOrdering.Remap(t.IntroducedChapterNumber, map);
            var resolved = ChapterOrdering.Remap(t.ResolvedChapterNumber, map);
            if (introduced == t.IntroducedChapterNumber && resolved == t.ResolvedChapterNumber) continue;
            t.IntroducedChapterNumber = introduced;
            t.ResolvedChapterNumber = resolved;
            t.UpdatedAt = now;
        }
        foreach (var card in _db.CharacterCards.Where(c => c.BookId == bookId).ToList())
        {
            var first = ChapterOrdering.Remap(card.FirstAppearanceChapterNumber, map);
            if (first == card.FirstAppearanceChapterNumber) continue;
            card.FirstAppearanceChapterNumber = first;
            card.UpdatedAt = now;
        }
    }

    /// <summary>Renumbers the book's sections and chapters from their places.</summary>
    public async Task RenumberChaptersAsync(int bookId)
    {
        var book = await LoadStructureAsync(bookId);
        ApplyStructure(book.Ordered, book);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Atomically syncs the book's plan to <paramref name="sections"/> (matched by section number) in one transaction:
    /// a matching section gets the new plan (and so does its chapter when it has only one — prose, status, versions
    /// and message links stay), a new number becomes a new section with one outlined chapter, and active sections
    /// absent from the plan are removed with their active chapters. Archived chapters are never touched and keep
    /// their place. Returns the active chapters of the plan's sections, in order.
    /// Concurrency is guarded at the orchestrator level by AgentRunStateService.TryStartRun.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> ReplaceSectionsAsync(int bookId, IEnumerable<Section> sections)
    {
        var incoming = sections.ToList();
        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            var book = await LoadStructureAsync(bookId);
            var active = book.Sections.Where(s => !s.IsArchived).ToList();
            var numbers = incoming.Select(s => s.Number).ToHashSet();
            var removed = active.Where(s => !numbers.Contains(s.Number)).ToList();
            foreach (var c in book.Ordered.Where(c => !c.IsArchived && removed.Contains(c.Section!)).ToList())
            {
                _db.Chapters.Remove(c);
                book.Ordered.Remove(c);
            }

            var planned = UpsertSectionOutlines(bookId, incoming, active.Except(removed).ToList(), book);
            var result = SavePlanOrder(planned, book);
            await _db.SaveChangesAsync();
            await tx.CommitAsync();
            return result;
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }

    /// <summary>
    /// Merges section outlines into the plan without removing anything (matched by number; new numbers become new
    /// sections). Used to keep the outlines salvaged from a run that failed part-way through streaming. Returns the
    /// active chapters of the merged sections.
    /// </summary>
    public async Task<IReadOnlyList<Chapter>> MergeSectionOutlinesAsync(int bookId, IEnumerable<Section> sections)
    {
        var book = await LoadStructureAsync(bookId);
        var active = book.Sections.Where(s => !s.IsArchived).ToList();
        var touched = UpsertSectionOutlines(bookId, sections, active, book);
        // Sections already there keep their numbers; a salvaged new one goes where its number puts it.
        var all = active.Select(s => (s, s.Number)).Concat(touched.Where(t => !active.Contains(t.s)).Select(t => (t.s, t.Number)))
            .Select((x, i) => (x.s, x.Number, i)).OrderBy(x => x.Number).ThenBy(x => x.i).Select(x => (x.s, x.Number)).ToList();
        SavePlanOrder(all, book);
        await _db.SaveChangesAsync();
        var touchedSections = touched.Select(t => t.s).ToHashSet();
        return book.Ordered.Where(c => !c.IsArchived && touchedSections.Contains(c.Section!)).ToList();
    }

    /// <summary>
    /// Orders the book by the plan: <paramref name="planned"/> sections by their plan number, each with its active
    /// chapters; archived chapters stay next to the chapter they followed. Returns the active chapters in order.
    /// </summary>
    private List<Chapter> SavePlanOrder(List<(Section s, int Number)> planned, BookStructure book)
    {
        var activeChapters = planned.OrderBy(p => p.Number)
            .SelectMany(p => book.Ordered.Where(c => !c.IsArchived && ReferenceEquals(c.Section, p.s)))
            .ToList();
        ApplyStructure(ChapterOrdering.Arrange(book.Ordered, activeChapters), book);
        return activeChapters;
    }

    /// <summary>
    /// Copies each incoming section's plan onto the active section with its number (and onto that section's chapter
    /// when it has only one), or adds a new section with one outlined chapter. Prose, status and archive state of
    /// existing chapters are preserved. Returns the affected sections with their plan numbers.
    /// </summary>
    private List<(Section s, int Number)> UpsertSectionOutlines(int bookId, IEnumerable<Section> incoming, List<Section> existing, BookStructure book)
    {
        var now = DateTime.UtcNow;
        var byNumber = existing.GroupBy(s => s.Number).ToDictionary(g => g.Key, g => g.First());
        var touched = new List<(Section, int)>();
        foreach (var plan in incoming)
        {
            var asChapter = new Chapter();
            asChapter.CopyPlanFrom(plan);
            if (byNumber.TryGetValue(plan.Number, out var row))
            {
                row.CopyPlanFrom(asChapter);
                row.PlannedChapters = Math.Max(1, plan.PlannedChapters);
                row.UpdatedAt = now;
                var chapters = book.Ordered.Where(c => !c.IsArchived && ReferenceEquals(c.Section, row)).ToList();
                if (chapters.Count == 1)
                {
                    chapters[0].CopyPlanFrom(plan);
                    chapters[0].UpdatedAt = now;
                }
                touched.Add((row, plan.Number));
            }
            else
            {
                var chapter = new Chapter { BookId = bookId, Status = ChapterStatus.Outlined, CreatedAt = now, UpdatedAt = now };
                chapter.CopyPlanFrom(plan);
                var section = NewSectionFor(chapter);
                section.PlannedChapters = Math.Max(1, plan.PlannedChapters);
                _db.Chapters.Add(chapter);
                book.Ordered.Add(chapter);
                byNumber[plan.Number] = section;
                touched.Add((section, plan.Number));
            }
        }
        return touched;
    }

    public async Task UpdateChapterAsync(Chapter chapter)
    {
        chapter.UpdatedAt = DateTime.UtcNow;
        _db.Chapters.Update(chapter);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteChaptersAsync(int bookId)
    {
        var chapters = await _db.Chapters.Where(c => c.BookId == bookId).ToListAsync();
        _db.Chapters.RemoveRange(chapters);
        _db.Sections.RemoveRange(await _db.Sections.Where(s => s.BookId == bookId).ToListAsync());
        await _db.SaveChangesAsync();
    }

    // Archiving keeps the chapter's place (and its last number, shown as "was Ch. N"); the chapters after it move up.
    // Archiving a section's last active chapter archives the section (and the chapter comes back with it).
    public async Task ArchiveChapterAsync(int bookId, int chapterId)
    {
        var book = await LoadStructureAsync(bookId);
        var chapter = book.Ordered.FirstOrDefault(c => c.Id == chapterId);
        if (chapter is null || chapter.IsArchived) return;
        chapter.IsArchived = true;
        chapter.ArchivedWithSection = !book.Ordered.Any(c => ReferenceEquals(c.Section, chapter.Section) && !c.IsArchived);
        chapter.UpdatedAt = DateTime.UtcNow;
        ApplyStructure(book.Ordered, book);
        await _db.SaveChangesAsync();
    }

    // Restoring brings the chapter back at its place (and its section, if it was archived); the chapters after it move down.
    public async Task RestoreChapterAsync(int bookId, int chapterId)
    {
        var book = await LoadStructureAsync(bookId);
        var chapter = book.Ordered.FirstOrDefault(c => c.Id == chapterId);
        if (chapter is null || !chapter.IsArchived) return;
        chapter.IsArchived = false;
        chapter.ArchivedWithSection = false;
        chapter.UpdatedAt = DateTime.UtcNow;
        ApplyStructure(book.Ordered, book);
        await _db.SaveChangesAsync();
    }

    public async Task ArchiveSectionAsync(int bookId, int sectionId)
    {
        var book = await LoadStructureAsync(bookId);
        var section = FindSection(book, sectionId);
        var now = DateTime.UtcNow;
        foreach (var c in book.Ordered.Where(c => ReferenceEquals(c.Section, section) && !c.IsArchived))
        {
            c.IsArchived = true;
            c.ArchivedWithSection = true;
            c.UpdatedAt = now;
        }
        ApplyStructure(book.Ordered, book);
        await _db.SaveChangesAsync();
    }

    public async Task RestoreSectionAsync(int bookId, int sectionId)
    {
        var book = await LoadStructureAsync(bookId);
        var section = FindSection(book, sectionId);
        var chapters = book.Ordered.Where(c => ReferenceEquals(c.Section, section)).ToList();
        // The chapters archived with it; failing that (archived before sections existed), all of them.
        var back = chapters.Where(c => c.IsArchived && c.ArchivedWithSection).ToList();
        if (back.Count == 0) back = chapters.Where(c => c.IsArchived).ToList();
        var now = DateTime.UtcNow;
        foreach (var c in back)
        {
            c.IsArchived = false;
            c.ArchivedWithSection = false;
            c.UpdatedAt = now;
        }
        ApplyStructure(book.Ordered, book);
        await _db.SaveChangesAsync();
    }

    public async Task ArchiveChaptersAsync(int bookId)
    {
        // Tracked, not ExecuteUpdate: the change log (ContentRevision) only sees tracked saves.
        var book = await LoadStructureAsync(bookId);
        var now = DateTime.UtcNow;
        foreach (var c in book.Ordered.Where(c => !c.IsArchived))
        {
            c.IsArchived = true;
            c.ArchivedWithSection = true;
            c.UpdatedAt = now;
        }
        ApplyStructure(book.Ordered, book);
        await _db.SaveChangesAsync();
    }

    // ── Sections ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Section>> GetSectionsAsync(int bookId, bool includeArchived = false)
    {
        var query = _db.Sections.Where(s => s.BookId == bookId);
        if (!includeArchived) query = query.Where(s => !s.IsArchived);
        return await query.OrderBy(s => s.SortOrder).ThenBy(s => s.Id).ToListAsync();
    }

    public async Task<Section?> GetSectionAsync(int bookId, int sectionId) =>
        await _db.Sections.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == sectionId);

    public async Task UpdateSectionAsync(Section section)
    {
        section.UpdatedAt = DateTime.UtcNow;
        _db.Sections.Update(section);
        await _db.SaveChangesAsync();
    }

    // ── Chapter Versions ─────────────────────────────────────────────────────

    public async Task<ChapterVersion> AddChapterVersionAsync(ChapterVersion version)
    {
        var active = await _db.ChapterVersions.FirstOrDefaultAsync(v => v.ChapterId == version.ChapterId && v.IsActive);
        // The same prose as the active version (an outline edit): its embeddings and summary move to the new version
        // instead of being made again.
        ChapterVersion? sameProse = null;
        // A manual edit on top of a manual active version amends it (see EditSources).
        if (active is not null && EditSources.IsManual(version.CreatedBy) && EditSources.IsManual(active.CreatedBy))
        {
            if (active.Content != version.Content) active.HasEmbeddings = false;
            active.Title = version.Title;
            active.Outline = version.Outline;
            active.Content = version.Content;
            active.Status = version.Status;
            active.PovCharacter = version.PovCharacter;
            active.CharactersInvolvedJson = version.CharactersInvolvedJson;
            active.PlotThreadsJson = version.PlotThreadsJson;
            active.ForeshadowingNotes = version.ForeshadowingNotes;
            active.PayoffNotes = version.PayoffNotes;
            active.CreatedBy = version.CreatedBy;
            active.CreatedAt = DateTime.UtcNow;
            version = active;
        }
        else
        {
            if (active is not null && active.Content == version.Content && active.HasEmbeddings)
            {
                sameProse = active;
                version.HasEmbeddings = true;
                active.HasEmbeddings = false;   // activated again later, it is indexed again
            }
            await AddNewChapterVersionAsync(version);
        }

        // Sync denormalized fields back to Chapter
        var chapter = await _db.Chapters.FindAsync(version.ChapterId);
        if (chapter is not null)
        {
            chapter.Title = version.Title;
            chapter.Outline = version.Outline;
            chapter.Content = version.Content;
            chapter.Status = version.Status;
            chapter.PovCharacter = version.PovCharacter;
            chapter.CharactersInvolvedJson = version.CharactersInvolvedJson;
            chapter.PlotThreadsJson = version.PlotThreadsJson;
            chapter.ForeshadowingNotes = version.ForeshadowingNotes;
            chapter.PayoffNotes = version.PayoffNotes;
            chapter.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync();
        if (sameProse is not null)
        {
            await _db.ChapterEmbeddings
                .Where(e => e.ChapterVersionId == sameProse.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(e => e.ChapterVersionId, version.Id));
            if (chapter is not null && chapter.SummaryVersionId == sameProse.Id)
            {
                chapter.SummaryVersionId = version.Id;
                await _db.SaveChangesAsync();
            }
        }
        return version;
    }

    private async Task AddNewChapterVersionAsync(ChapterVersion version)
    {
        // Deactivate all existing versions for this chapter
        await _db.ChapterVersions
            .Where(v => v.ChapterId == version.ChapterId)
            .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));

        version.CreatedAt = DateTime.UtcNow;
        version.IsActive = true;

        // Auto-assign version number
        var maxVersion = await _db.ChapterVersions
            .Where(v => v.ChapterId == version.ChapterId)
            .MaxAsync(v => (int?)v.VersionNumber) ?? 0;
        version.VersionNumber = maxVersion + 1;

        _db.ChapterVersions.Add(version);
    }

    public async Task UpdateChapterVersionAsync(ChapterVersion version)
    {
        _db.ChapterVersions.Update(version);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<ChapterVersion>> GetChapterVersionsAsync(int chapterId) =>
        await _db.ChapterVersions
            .Where(v => v.ChapterId == chapterId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();

    public async Task<ChapterVersion?> GetChapterVersionAsync(int chapterId, int versionId) =>
        await _db.ChapterVersions.FirstOrDefaultAsync(v => v.ChapterId == chapterId && v.Id == versionId);

    public async Task<IReadOnlyDictionary<int, int>> GetActiveChapterVersionIdsAsync(int bookId) =>
        await _db.ChapterVersions
            .Where(v => v.BookId == bookId && v.IsActive)
            .GroupBy(v => v.ChapterId)
            .Select(g => new { ChapterId = g.Key, Id = g.Max(v => v.Id) })
            .ToDictionaryAsync(x => x.ChapterId, x => x.Id);

    public async Task<ChapterVersion> ActivateChapterVersionAsync(int bookId, int chapterId, int versionId)
    {
        var version = await _db.ChapterVersions.FirstOrDefaultAsync(v => v.ChapterId == chapterId && v.Id == versionId)
            ?? throw new InvalidOperationException($"Version {versionId} not found for chapter {chapterId}.");

        await using var tx = await _db.Database.BeginTransactionAsync();
        try
        {
            // Deactivate all versions
            await _db.ChapterVersions
                .Where(v => v.ChapterId == chapterId)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false));

            // Activate the chosen one
            version.IsActive = true;
            _db.ChapterVersions.Update(version);

            // Sync denormalized fields back to Chapter
            var chapter = await _db.Chapters.FindAsync(chapterId);
            if (chapter is not null)
            {
                chapter.Title = version.Title;
                chapter.Outline = version.Outline;
                chapter.Content = version.Content;
                chapter.Status = version.Status;
                chapter.PovCharacter = version.PovCharacter;
                chapter.CharactersInvolvedJson = version.CharactersInvolvedJson;
                chapter.PlotThreadsJson = version.PlotThreadsJson;
                chapter.ForeshadowingNotes = version.ForeshadowingNotes;
                chapter.PayoffNotes = version.PayoffNotes;
                chapter.UpdatedAt = DateTime.UtcNow;
            }

            await _db.SaveChangesAsync();
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }

        return version;
    }

    public async Task<IEnumerable<AgentMessage>> GetMessagesAsync(int bookId, int? chapterId = null)
    {
        var query = _db.AgentMessages.Where(m => m.BookId == bookId && !m.IsDeleted);
        if (chapterId.HasValue)
            query = query.Where(m => m.ChapterId == chapterId);
        return await query.OrderBy(m => m.CreatedAt).ToListAsync();
    }

    public async Task<AgentMessage> AddMessageAsync(AgentMessage message)
    {
        message.CreatedAt = DateTime.UtcNow;
        _db.AgentMessages.Add(message);
        await _db.SaveChangesAsync();
        return message;
    }

    public async Task<AgentMessage?> FindMessageByIdAsync(int messageId) =>
        await _db.AgentMessages.FindAsync(messageId);

    public async Task UpdateMessageAsync(AgentMessage message)
    {
        _db.AgentMessages.Update(message);
        await _db.SaveChangesAsync();
    }

    public async Task<LlmConfiguration?> GetLlmConfigAsync(int bookId) =>
        await _db.LlmConfigurations.FirstOrDefaultAsync(l => l.BookId == bookId);

    public async Task<LlmConfiguration> UpsertLlmConfigAsync(LlmConfiguration config)
    {
        var existing = await _db.LlmConfigurations.FirstOrDefaultAsync(l => l.BookId == config.BookId);

        if (existing is null)
        {
            _db.LlmConfigurations.Add(config);
        }
        else
        {
            existing.CopyFrom(config);
            config = existing;
        }

        await _db.SaveChangesAsync();
        return config;
    }

    public async Task<TokenUsageRecord> AddTokenUsageAsync(TokenUsageRecord record)
    {
        record.CreatedAt = DateTime.UtcNow;
        _db.TokenUsageRecords.Add(record);
        await _db.SaveChangesAsync();
        return record;
    }

    public async Task AddWorkflowStepAsync(int bookId, string step, string? endpoint = null, string? modelName = null, CancellationToken ct = default)
    {
        _db.TokenUsageRecords.Add(new TokenUsageRecord
        {
            BookId = bookId,
            AgentRole = AgentRole.WorkflowStep,
            PromptTokens = 0,
            CompletionTokens = 0,
            StepLabel = step,
            Endpoint = endpoint,
            ModelName = modelName,
            CreatedAt = DateTime.UtcNow,
        });
        await _db.SaveChangesAsync(ct);
    }

    public async Task<IEnumerable<TokenUsageRecord>> GetTokenUsageAsync(int bookId) =>
        await _db.TokenUsageRecords
            .Where(r => r.BookId == bookId && !r.IsDeleted)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

    public async Task DeleteTokenUsageAsync(int bookId)
    {
        await _db.TokenUsageRecords
            .Where(r => r.BookId == bookId && !r.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.IsDeleted, true));
    }

    public async Task DeleteMessagesAsync(int bookId)
    {
        await _db.AgentMessages
            .Where(m => m.BookId == bookId && !m.IsDeleted)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.IsDeleted, true));
    }

    // ── Story Bible ───────────────────────────────────────────────────────────

    public async Task<StoryBible?> GetStoryBibleAsync(int bookId) =>
        await _db.StoryBibles.FirstOrDefaultAsync(s => s.BookId == bookId);

    public async Task<StoryBible> UpsertStoryBibleAsync(StoryBible bible)
    {
        var existing = await _db.StoryBibles.FirstOrDefaultAsync(s => s.BookId == bible.BookId);
        if (existing is null)
        {
            bible.CreatedAt = bible.UpdatedAt = DateTime.UtcNow;
            _db.StoryBibles.Add(bible);
            await _db.SaveChangesAsync();
            return bible;
        }
        existing.SettingDescription = bible.SettingDescription;
        existing.TimePeriod = bible.TimePeriod;
        existing.Themes = bible.Themes;
        existing.ToneAndStyle = bible.ToneAndStyle;
        existing.WorldRules = bible.WorldRules;
        existing.Notes = bible.Notes;
        existing.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        return existing;
    }

    public async Task DeleteStoryBibleAsync(int bookId)
    {
        var bible = await _db.StoryBibles.FirstOrDefaultAsync(s => s.BookId == bookId);
        if (bible is not null)
        {
            _db.StoryBibles.Remove(bible);
            await _db.SaveChangesAsync();
        }
    }

    // ── Character Cards ───────────────────────────────────────────────────────

    public async Task<IEnumerable<CharacterCard>> GetCharacterCardsAsync(int bookId) =>
        await _db.CharacterCards.Where(c => c.BookId == bookId && !c.IsArchived).OrderBy(c => c.Name).ToListAsync();

    public async Task<IEnumerable<CharacterCard>> GetAllCharacterCardsAsync(int bookId) =>
        await _db.CharacterCards.Where(c => c.BookId == bookId).OrderBy(c => c.Name).ToListAsync();

    public async Task<CharacterCard?> GetCharacterCardAsync(int bookId, int cardId) =>
        await _db.CharacterCards.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == cardId);

    public async Task<CharacterCard> AddCharacterCardAsync(CharacterCard card)
    {
        card.CreatedAt = card.UpdatedAt = DateTime.UtcNow;
        _db.CharacterCards.Add(card);
        await _db.SaveChangesAsync();
        return card;
    }

    public async Task<IReadOnlyList<CharacterCard>> AddCharacterCardsBatchAsync(IEnumerable<CharacterCard> cards)
    {
        var now = DateTime.UtcNow;
        var list = cards.ToList();
        foreach (var card in list)
            card.CreatedAt = card.UpdatedAt = now;
        _db.CharacterCards.AddRange(list);
        await _db.SaveChangesAsync();
        return list;
    }

    public async Task UpdateCharacterCardAsync(CharacterCard card)
    {
        card.UpdatedAt = DateTime.UtcNow;
        _db.CharacterCards.Update(card);
        await _db.SaveChangesAsync();
    }

    public async Task ArchiveCharacterCardAsync(int bookId, int cardId)
    {
        var card = await _db.CharacterCards.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == cardId);
        if (card is not null)
        {
            card.IsArchived = true;
            card.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task UnarchiveCharacterCardAsync(int bookId, int cardId)
    {
        var card = await _db.CharacterCards.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == cardId);
        if (card is not null)
        {
            card.IsArchived = false;
            card.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task DeleteCharacterCardAsync(int bookId, int cardId)
    {
        var card = await _db.CharacterCards.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == cardId);
        if (card is not null)
        {
            _db.CharacterCards.Remove(card);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Deletes the book's active character cards. Archived cards are left untouched.</summary>
    public async Task DeleteCharacterCardsAsync(int bookId)
    {
        var cards = await _db.CharacterCards.Where(c => c.BookId == bookId && !c.IsArchived).ToListAsync();
        _db.CharacterCards.RemoveRange(cards);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Merges cards into the book's active set without removing anything: matches on Name
    /// (case-insensitive), overwrites the matched row's fields, and inserts unknown names.
    /// Archived cards are never matched or modified. Returns the affected rows.
    /// </summary>
    public async Task<IReadOnlyList<CharacterCard>> MergeCharacterCardsAsync(int bookId, IEnumerable<CharacterCard> cards)
    {
        var existing = await _db.CharacterCards
            .Where(c => c.BookId == bookId && !c.IsArchived)
            .ToListAsync();

        var byName = new Dictionary<string, CharacterCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in existing)
            byName.TryAdd(c.Name, c);

        var now = DateTime.UtcNow;
        var touched = new List<CharacterCard>();
        foreach (var card in cards)
        {
            if (byName.TryGetValue(card.Name, out var row))
            {
                row.Role = card.Role;
                row.PhysicalDescription = card.PhysicalDescription;
                row.Personality = card.Personality;
                row.Backstory = card.Backstory;
                row.GoalMotivation = card.GoalMotivation;
                row.Arc = card.Arc;
                row.FirstAppearanceChapterNumber = card.FirstAppearanceChapterNumber;
                row.Notes = card.Notes;
                row.UpdatedAt = now;
                touched.Add(row);
            }
            else
            {
                card.BookId = bookId;
                card.CreatedAt = card.UpdatedAt = now;
                _db.CharacterCards.Add(card);
                byName[card.Name] = card;
                touched.Add(card);
            }
        }

        await _db.SaveChangesAsync();
        return touched;
    }

    // ── Book chat ─────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<BookChatMessage>> GetChatMessagesAsync(int bookId, int? beforeId = null, int? limit = null, bool includeHidden = false)
    {
        var query = _db.BookChatMessages.Where(m => m.BookId == bookId);
        if (!includeHidden) query = query.Where(m => m.HiddenAt == null);
        if (beforeId is int before) query = query.Where(m => m.Id < before);
        query = query.OrderByDescending(m => m.Id);
        if (limit is int n) query = query.Take(n);
        var messages = await query.Include(m => m.Proposals).AsSplitQuery().ToListAsync();
        foreach (var m in messages)
            m.Proposals = m.Proposals.OrderBy(p => p.Ordinal).ToList();
        messages.Reverse();
        return messages;
    }

    public async Task<BookChatMessage?> GetChatMessageAsync(int bookId, int messageId) =>
        await _db.BookChatMessages.Include(m => m.Proposals)
            .FirstOrDefaultAsync(m => m.BookId == bookId && m.Id == messageId);

    public async Task<BookChatMessage> AddChatMessageAsync(BookChatMessage message)
    {
        var now = DateTime.UtcNow;
        message.CreatedAt = now;
        foreach (var p in message.Proposals)
        {
            p.BookId = message.BookId;
            p.CreatedAt = p.UpdatedAt = now;
        }
        _db.BookChatMessages.Add(message);
        await _db.SaveChangesAsync();
        return message;
    }

    public async Task UpdateChatMessageAsync(BookChatMessage message)
    {
        _db.BookChatMessages.Update(message);
        await _db.SaveChangesAsync();
    }

    public async Task<ChatProposal?> GetProposalAsync(int bookId, int proposalId) =>
        await _db.ChatProposals.FirstOrDefaultAsync(p => p.BookId == bookId && p.Id == proposalId);

    public async Task<IReadOnlyList<ChatProposal>> GetProposalsAsync(int bookId, IEnumerable<int> proposalIds)
    {
        var ids = proposalIds.ToArray();
        return await _db.ChatProposals
            .Where(p => p.BookId == bookId && ids.Contains(p.Id))
            .OrderBy(p => p.MessageId).ThenBy(p => p.Ordinal)
            .ToListAsync();
    }

    public async Task<IReadOnlyList<ChatProposal>> GetProposalsByStatusAsync(int bookId, ChatProposalStatus status) =>
        await _db.ChatProposals
            .Where(p => p.BookId == bookId && p.Status == status)
            .OrderBy(p => p.MessageId).ThenBy(p => p.Ordinal)
            .ToListAsync();

    public async Task<IReadOnlyList<ChatProposal>> GetOpenProposalsSinceAsync(int bookId, DateTime since) =>
        await _db.ChatProposals
            .Where(p => p.BookId == bookId && p.CreatedAt >= since
                        && (p.Status == ChatProposalStatus.Pending || p.Status == ChatProposalStatus.Failed)
                        && p.Message.HiddenAt == null)
            .OrderBy(p => p.MessageId).ThenBy(p => p.Ordinal)
            .ToListAsync();

    public async Task UpdateProposalAsync(ChatProposal proposal)
    {
        proposal.UpdatedAt = DateTime.UtcNow;
        _db.ChatProposals.Update(proposal);
        await _db.SaveChangesAsync();
    }

    public void DiscardPendingChanges() => _db.ChangeTracker.Clear();

    public async Task<int> FailInterruptedChatAsync()
    {
        var messages = await _db.BookChatMessages
            .Where(m => m.Status == BookChatMessageStatus.Pending)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, BookChatMessageStatus.Failed));
        // Tracked, not ExecuteUpdate: proposals are in the change log (ContentRevision), which only sees tracked saves.
        var interrupted = await _db.ChatProposals.Where(p => p.Status == ChatProposalStatus.Applying).ToListAsync();
        foreach (var p in interrupted)
        {
            p.Status = ChatProposalStatus.Failed;
            p.ResultNote = "Interrupted by a server restart.";
            p.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return messages + interrupted.Count;
    }

    // ── Clarifications ────────────────────────────────────────────────────────

    // Untracked: a planning run polls this while the author answers in other requests.
    public async Task<IReadOnlyList<Clarification>> GetClarificationsAsync(int bookId, bool includeArchived = false) =>
        await _db.Clarifications.AsNoTracking()
            .Where(c => c.BookId == bookId && (includeArchived || !c.IsArchived))
            .OrderBy(c => c.CreatedAt).ThenBy(c => c.Id)
            .ToListAsync();

    public async Task<Clarification?> GetClarificationAsync(int bookId, int clarificationId) =>
        await _db.Clarifications.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == clarificationId);

    // AnsweredAt records the first answer, whichever path gives it — the metadata timeline relies on it.
    public async Task<Clarification> AddClarificationAsync(Clarification clarification)
    {
        clarification.CreatedAt = clarification.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(clarification.Answer)) clarification.AnsweredAt ??= clarification.CreatedAt;
        _db.Clarifications.Add(clarification);
        await _db.SaveChangesAsync();
        return clarification;
    }

    public async Task UpdateClarificationAsync(Clarification clarification)
    {
        clarification.UpdatedAt = DateTime.UtcNow;
        if (!string.IsNullOrWhiteSpace(clarification.Answer)) clarification.AnsweredAt ??= clarification.UpdatedAt;
        _db.Clarifications.Update(clarification);
        await _db.SaveChangesAsync();
    }

    public async Task ArchiveClarificationsAsync(int bookId)
    {
        // Tracked, not ExecuteUpdate: the change log (ContentRevision) only sees tracked saves.
        var now = DateTime.UtcNow;
        foreach (var c in await _db.Clarifications.Where(c => c.BookId == bookId && !c.IsArchived).ToListAsync())
        {
            c.IsArchived = true;
            c.UpdatedAt = now;
        }
        await _db.SaveChangesAsync();
    }

    public async Task<IReadOnlyList<VisualAsset>> GetVisualAssetsAsync(int bookId) =>
        await _db.VisualAssets.Where(a => a.BookId == bookId).OrderBy(a => a.Kind).ThenBy(a => a.Name).ToListAsync();

    public async Task<VisualAsset?> GetVisualAssetAsync(int bookId, int assetId) =>
        await _db.VisualAssets.FirstOrDefaultAsync(a => a.BookId == bookId && a.Id == assetId);

    public async Task<VisualAsset> AddVisualAssetAsync(VisualAsset asset)
    {
        asset.CreatedAt = asset.UpdatedAt = DateTime.UtcNow;
        _db.VisualAssets.Add(asset);
        await _db.SaveChangesAsync();
        return asset;
    }

    public async Task UpdateVisualAssetAsync(VisualAsset asset)
    {
        asset.UpdatedAt = DateTime.UtcNow;
        _db.VisualAssets.Update(asset);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteVisualAssetAsync(int bookId, int assetId) =>
        await _db.VisualAssets.Where(a => a.BookId == bookId && a.Id == assetId).ExecuteDeleteAsync();

    // ── Illustrations ─────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<Illustration>> GetIllustrationsAsync(int bookId, int? chapterId = null, IllustrationKind? kind = null)
    {
        var query = _db.Illustrations.Where(i => i.BookId == bookId);
        if (chapterId.HasValue) query = query.Where(i => i.ChapterId == chapterId);
        if (kind.HasValue) query = query.Where(i => i.Kind == kind);
        return await query
            .OrderBy(i => i.Kind).ThenBy(i => i.ChapterId).ThenBy(i => i.ParagraphNumber).ThenBy(i => i.SortOrder).ThenBy(i => i.Id)
            .ToListAsync();
    }

    public async Task<Illustration?> GetIllustrationAsync(int bookId, int illustrationId) =>
        await _db.Illustrations.FirstOrDefaultAsync(i => i.BookId == bookId && i.Id == illustrationId);

    public async Task<Illustration> AddIllustrationAsync(Illustration illustration)
    {
        illustration.CreatedAt = illustration.UpdatedAt = DateTime.UtcNow;
        _db.Illustrations.Add(illustration);
        await _db.SaveChangesAsync();
        return illustration;
    }

    public async Task UpdateIllustrationAsync(Illustration illustration)
    {
        illustration.UpdatedAt = DateTime.UtcNow;
        _db.Illustrations.Update(illustration);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteIllustrationAsync(int bookId, int illustrationId)
    {
        var row = await _db.Illustrations.FirstOrDefaultAsync(i => i.BookId == bookId && i.Id == illustrationId);
        if (row is null) return;
        var imageId = row.ImageId;
        _db.Illustrations.Remove(row);
        await _db.SaveChangesAsync();
        if (imageId.HasValue)
            await _db.BookImages.Where(b => b.Id == imageId).ExecuteDeleteAsync();
    }

    public async Task<IReadOnlyList<Illustration>> ReplaceUnrenderedIllustrationsAsync(
        int bookId, int? chapterId, IllustrationKind kind, IEnumerable<Illustration> planned)
    {
        await using var tx = await _db.Database.BeginTransactionAsync();
        var stale = await _db.Illustrations
            .Where(i => i.BookId == bookId && i.ChapterId == chapterId && i.Kind == kind && i.ImageId == null)
            .ToListAsync();
        _db.Illustrations.RemoveRange(stale);

        var now = DateTime.UtcNow;
        var list = planned.ToList();
        foreach (var i in list)
        {
            i.BookId = bookId;
            i.ChapterId = chapterId;
            i.Kind = kind;
            i.CreatedAt = i.UpdatedAt = now;
        }
        _db.Illustrations.AddRange(list);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();
        return list;
    }

    public async Task<Illustration> SaveRenderedImageAsync(int bookId, int illustrationId, BookImage image)
    {
        var row = await _db.Illustrations.FirstOrDefaultAsync(i => i.BookId == bookId && i.Id == illustrationId)
            ?? throw new InvalidOperationException($"Illustration {illustrationId} not found.");
        await using var tx = await _db.Database.BeginTransactionAsync();
        var previousImageId = row.ImageId;
        image.BookId = bookId;
        image.CreatedAt = DateTime.UtcNow;
        _db.BookImages.Add(image);
        await _db.SaveChangesAsync();

        row.ImageId = image.Id;
        row.Status = IllustrationStatus.Rendered;
        row.Error = null;
        row.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
        if (previousImageId.HasValue)
            await _db.BookImages.Where(b => b.Id == previousImageId).ExecuteDeleteAsync();
        await tx.CommitAsync();
        // A render run saves many images through one context; don't keep their bytes tracked.
        _db.Entry(image).State = EntityState.Detached;
        return row;
    }

    public async Task<BookImage?> GetBookImageAsync(int bookId, int imageId) =>
        await _db.BookImages.AsNoTracking().FirstOrDefaultAsync(b => b.BookId == bookId && b.Id == imageId);

    public async Task<IReadOnlyList<BookImage>> GetBookImagesAsync(int bookId, IEnumerable<int> imageIds)
    {
        var ids = imageIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        return await _db.BookImages.AsNoTracking().Where(b => b.BookId == bookId && ids.Contains(b.Id)).ToListAsync();
    }

    // ── Character Card Versions ───────────────────────────────────────────────

    public async Task<CharacterCardVersion> AddCharacterVersionAsync(CharacterCardVersion version)
    {
        var latest = await _db.CharacterCardVersions
            .Where(v => v.CharacterCardId == version.CharacterCardId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync();
        // A manual edit on top of a manual version amends it (see EditSources).
        if (latest is not null && EditSources.IsManual(version.CreatedBy) && EditSources.IsManual(latest.CreatedBy))
        {
            latest.Name = version.Name;
            latest.Role = version.Role;
            latest.PhysicalDescription = version.PhysicalDescription;
            latest.Personality = version.Personality;
            latest.Backstory = version.Backstory;
            latest.GoalMotivation = version.GoalMotivation;
            latest.Arc = version.Arc;
            latest.FirstAppearanceChapterNumber = version.FirstAppearanceChapterNumber;
            latest.Notes = version.Notes;
            latest.CreatedBy = version.CreatedBy;
            latest.CreatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return latest;
        }
        version.VersionNumber = (latest?.VersionNumber ?? 0) + 1;
        version.CreatedAt = DateTime.UtcNow;
        _db.CharacterCardVersions.Add(version);
        await _db.SaveChangesAsync();
        return version;
    }

    public async Task AddCharacterVersionsBatchAsync(IEnumerable<CharacterCardVersion> versions)
    {
        var now = DateTime.UtcNow;
        foreach (var v in versions)
        {
            v.VersionNumber = 1; // agent batch-writes always produce the first version
            v.CreatedAt = now;
        }
        _db.CharacterCardVersions.AddRange(versions);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<CharacterCardVersion>> GetCharacterVersionsAsync(int bookId, int cardId) =>
        await _db.CharacterCardVersions
            .Where(v => v.BookId == bookId && v.CharacterCardId == cardId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();

    public async Task<CharacterCardVersion?> GetCharacterVersionAsync(int bookId, int cardId, int versionId) =>
        await _db.CharacterCardVersions.FirstOrDefaultAsync(v => v.BookId == bookId && v.CharacterCardId == cardId && v.Id == versionId);

    public async Task<CharacterCard> RestoreCharacterVersionAsync(int bookId, int cardId, int versionId)
    {
        var version = await GetCharacterVersionAsync(bookId, cardId, versionId)
            ?? throw new InvalidOperationException($"Character version {versionId} not found.");
        var card = await _db.CharacterCards.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == cardId)
            ?? throw new InvalidOperationException($"Character card {cardId} not found.");

        card.Name = version.Name;
        card.Role = version.Role;
        card.PhysicalDescription = version.PhysicalDescription;
        card.Personality = version.Personality;
        card.Backstory = version.Backstory;
        card.GoalMotivation = version.GoalMotivation;
        card.Arc = version.Arc;
        card.FirstAppearanceChapterNumber = version.FirstAppearanceChapterNumber;
        card.Notes = version.Notes;
        card.IsArchived = false;
        card.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Record the restore as a new version
        await AddCharacterVersionAsync(new CharacterCardVersion
        {
            CharacterCardId = cardId,
            BookId = bookId,
            Name = card.Name,
            Role = card.Role,
            PhysicalDescription = card.PhysicalDescription,
            Personality = card.Personality,
            Backstory = card.Backstory,
            GoalMotivation = card.GoalMotivation,
            Arc = card.Arc,
            FirstAppearanceChapterNumber = card.FirstAppearanceChapterNumber,
            Notes = card.Notes,
            CreatedBy = $"restore:v{version.VersionNumber}",
        });

        return card;
    }

    // ── Plot Threads ──────────────────────────────────────────────────────────

    public async Task<IEnumerable<PlotThread>> GetPlotThreadsAsync(int bookId) =>
        await _db.PlotThreads.Where(p => p.BookId == bookId && !p.IsArchived).OrderBy(p => p.IntroducedChapterNumber).ThenBy(p => p.Name).ToListAsync();

    public async Task<IEnumerable<PlotThread>> GetAllPlotThreadsAsync(int bookId) =>
        await _db.PlotThreads.Where(p => p.BookId == bookId).OrderBy(p => p.IntroducedChapterNumber).ThenBy(p => p.Name).ToListAsync();

    public async Task<PlotThread?> GetPlotThreadAsync(int bookId, int threadId) =>
        await _db.PlotThreads.FirstOrDefaultAsync(p => p.BookId == bookId && p.Id == threadId);

    public async Task<PlotThread> AddPlotThreadAsync(PlotThread thread)
    {
        thread.CreatedAt = thread.UpdatedAt = DateTime.UtcNow;
        _db.PlotThreads.Add(thread);
        await _db.SaveChangesAsync();
        return thread;
    }

    public async Task<IReadOnlyList<PlotThread>> AddPlotThreadsBatchAsync(IEnumerable<PlotThread> threads)
    {
        var now = DateTime.UtcNow;
        var list = threads.ToList();
        foreach (var t in list)
            t.CreatedAt = t.UpdatedAt = now;
        _db.PlotThreads.AddRange(list);
        await _db.SaveChangesAsync();
        return list;
    }

    public async Task UpdatePlotThreadAsync(PlotThread thread)
    {
        thread.UpdatedAt = DateTime.UtcNow;
        _db.PlotThreads.Update(thread);
        await _db.SaveChangesAsync();
    }

    public async Task ArchivePlotThreadAsync(int bookId, int threadId)
    {
        var thread = await _db.PlotThreads.FirstOrDefaultAsync(p => p.BookId == bookId && p.Id == threadId);
        if (thread is not null)
        {
            thread.IsArchived = true;
            thread.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task UnarchivePlotThreadAsync(int bookId, int threadId)
    {
        var thread = await _db.PlotThreads.FirstOrDefaultAsync(p => p.BookId == bookId && p.Id == threadId);
        if (thread is not null)
        {
            thread.IsArchived = false;
            thread.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task DeletePlotThreadAsync(int bookId, int threadId)
    {
        var thread = await _db.PlotThreads.FirstOrDefaultAsync(p => p.BookId == bookId && p.Id == threadId);
        if (thread is not null)
        {
            _db.PlotThreads.Remove(thread);
            await _db.SaveChangesAsync();
        }
    }

    /// <summary>Deletes the book's active plot threads. Archived threads are left untouched.</summary>
    public async Task DeletePlotThreadsAsync(int bookId)
    {
        var threads = await _db.PlotThreads.Where(p => p.BookId == bookId && !p.IsArchived).ToListAsync();
        _db.PlotThreads.RemoveRange(threads);
        await _db.SaveChangesAsync();
    }

    /// <summary>
    /// Merges threads into the book's active set without removing anything: matches on Name
    /// (case-insensitive), overwrites the matched row's fields, and inserts unknown names.
    /// Archived threads are never matched or modified. Returns the affected rows.
    /// </summary>
    public async Task<IReadOnlyList<PlotThread>> MergePlotThreadsAsync(int bookId, IEnumerable<PlotThread> threads)
    {
        var existing = await _db.PlotThreads
            .Where(p => p.BookId == bookId && !p.IsArchived)
            .ToListAsync();

        var byName = new Dictionary<string, PlotThread>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in existing)
            byName.TryAdd(t.Name, t);

        var now = DateTime.UtcNow;
        var touched = new List<PlotThread>();
        foreach (var thread in threads)
        {
            if (byName.TryGetValue(thread.Name, out var row))
            {
                row.Description = thread.Description;
                row.Type = thread.Type;
                row.IntroducedChapterNumber = thread.IntroducedChapterNumber;
                row.ResolvedChapterNumber = thread.ResolvedChapterNumber;
                row.Status = thread.Status;
                row.UpdatedAt = now;
                touched.Add(row);
            }
            else
            {
                thread.BookId = bookId;
                thread.CreatedAt = thread.UpdatedAt = now;
                _db.PlotThreads.Add(thread);
                byName[thread.Name] = thread;
                touched.Add(thread);
            }
        }

        await _db.SaveChangesAsync();
        return touched;
    }

    // ── Plot Thread Versions ──────────────────────────────────────────────────

    public async Task<PlotThreadVersion> AddPlotThreadVersionAsync(PlotThreadVersion version)
    {
        var latest = await _db.PlotThreadVersions
            .Where(v => v.PlotThreadId == version.PlotThreadId)
            .OrderByDescending(v => v.VersionNumber)
            .FirstOrDefaultAsync();
        // A manual edit on top of a manual version amends it (see EditSources).
        if (latest is not null && EditSources.IsManual(version.CreatedBy) && EditSources.IsManual(latest.CreatedBy))
        {
            latest.Name = version.Name;
            latest.Description = version.Description;
            latest.Type = version.Type;
            latest.IntroducedChapterNumber = version.IntroducedChapterNumber;
            latest.ResolvedChapterNumber = version.ResolvedChapterNumber;
            latest.Status = version.Status;
            latest.CreatedBy = version.CreatedBy;
            latest.CreatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
            return latest;
        }
        version.VersionNumber = (latest?.VersionNumber ?? 0) + 1;
        version.CreatedAt = DateTime.UtcNow;
        _db.PlotThreadVersions.Add(version);
        await _db.SaveChangesAsync();
        return version;
    }

    public async Task AddPlotThreadVersionsBatchAsync(IEnumerable<PlotThreadVersion> versions)
    {
        var now = DateTime.UtcNow;
        foreach (var v in versions)
        {
            v.VersionNumber = 1; // agent batch-writes always produce the first version
            v.CreatedAt = now;
        }
        _db.PlotThreadVersions.AddRange(versions);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<PlotThreadVersion>> GetPlotThreadVersionsAsync(int bookId, int threadId) =>
        await _db.PlotThreadVersions
            .Where(v => v.BookId == bookId && v.PlotThreadId == threadId)
            .OrderByDescending(v => v.VersionNumber)
            .ToListAsync();

    public async Task<PlotThreadVersion?> GetPlotThreadVersionAsync(int bookId, int threadId, int versionId) =>
        await _db.PlotThreadVersions.FirstOrDefaultAsync(v => v.BookId == bookId && v.PlotThreadId == threadId && v.Id == versionId);

    public async Task<PlotThread> RestorePlotThreadVersionAsync(int bookId, int threadId, int versionId)
    {
        var version = await GetPlotThreadVersionAsync(bookId, threadId, versionId)
            ?? throw new InvalidOperationException($"Plot thread version {versionId} not found.");
        var thread = await _db.PlotThreads.FirstOrDefaultAsync(p => p.BookId == bookId && p.Id == threadId)
            ?? throw new InvalidOperationException($"Plot thread {threadId} not found.");

        thread.Name = version.Name;
        thread.Description = version.Description;
        thread.Type = version.Type;
        thread.IntroducedChapterNumber = version.IntroducedChapterNumber;
        thread.ResolvedChapterNumber = version.ResolvedChapterNumber;
        thread.Status = version.Status;
        thread.IsArchived = false;
        thread.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();

        // Record the restore as a new version
        await AddPlotThreadVersionAsync(new PlotThreadVersion
        {
            PlotThreadId = threadId,
            BookId = bookId,
            Name = thread.Name,
            Description = thread.Description,
            Type = thread.Type,
            IntroducedChapterNumber = thread.IntroducedChapterNumber,
            ResolvedChapterNumber = thread.ResolvedChapterNumber,
            Status = thread.Status,
            CreatedBy = $"restore:v{version.VersionNumber}",
        });

        return thread;
    }

    // ── Agent Runs ────────────────────────────────────────────────────────────

    public async Task<AgentRun> CreateRunAsync(AgentRun run)
    {
        run.CreatedAt = DateTime.UtcNow;
        run.UpdatedAt = DateTime.UtcNow;
        _db.AgentRuns.Add(run);
        await _db.SaveChangesAsync();
        return run;
    }

    public async Task<AgentRun?> GetRunByIdAsync(Guid runId) =>
        await _db.AgentRuns.FindAsync(runId);

    public async Task<AgentRun?> GetActiveRunForBookAsync(int bookId) =>
        await _db.AgentRuns
            .Where(r => r.BookId == bookId &&
                (r.Status == AgentRunPersistStatus.Running ||
                 r.Status == AgentRunPersistStatus.WaitingForInput))
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task UpdateRunAsync(AgentRun run)
    {
        run.UpdatedAt = DateTime.UtcNow;
        _db.AgentRuns.Update(run);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<AgentRun>> GetRunsByStatusAsync(AgentRunPersistStatus status) =>
        await _db.AgentRuns.Where(r => r.Status == status).ToListAsync();

    public async Task<IReadOnlyList<AgentRun>> GetRunsForBookAsync(int bookId) =>
        await _db.AgentRuns.AsNoTracking().Where(r => r.BookId == bookId).OrderBy(r => r.CreatedAt).ToListAsync();

    // ── LLM Presets ───────────────────────────────────────────────────────────

    public async Task<IEnumerable<LlmPreset>> GetPresetsAsync(int? userId) =>
        await _db.LlmPresets
            .Where(p => p.UserId == null || p.UserId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

    public async Task<LlmPreset?> GetPresetAsync(int id) =>
        await _db.LlmPresets.FindAsync(id);

    public async Task<LlmPreset?> GetDefaultPresetAsync(int userId)
    {
        var presetId = await _db.Users.Where(u => u.Id == userId).Select(u => u.DefaultPresetId).FirstOrDefaultAsync();
        if (presetId is null) return null;
        // A shared preset an admin has since made private is no longer the user's to use.
        return await _db.LlmPresets.FirstOrDefaultAsync(p => p.Id == presetId && (p.UserId == null || p.UserId == userId));
    }

    public async Task<LlmPreset> CreatePresetAsync(LlmPreset preset)
    {
        preset.CreatedAt = preset.UpdatedAt = DateTime.UtcNow;
        _db.LlmPresets.Add(preset);
        await _db.SaveChangesAsync();
        return preset;
    }

    public async Task UpdatePresetAsync(LlmPreset preset)
    {
        preset.UpdatedAt = DateTime.UtcNow;
        _db.LlmPresets.Update(preset);
        await _db.SaveChangesAsync();
    }

    public async Task DeletePresetAsync(int id)
    {
        var preset = await _db.LlmPresets.FindAsync(id);
        if (preset is not null)
        {
            _db.LlmPresets.Remove(preset);
            await _db.SaveChangesAsync();
        }
    }

    // ── Snapshots ─────────────────────────────────────────────────────────────

    public async Task AddStoryBibleSnapshotAsync(StoryBibleSnapshot snapshot)
    {
        snapshot.CreatedAt = DateTime.UtcNow;
        _db.StoryBibleSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
    }

    public async Task AmendStoryBibleSnapshotAsync(int snapshotId, StoryBibleSnapshot values)
    {
        var snapshot = await _db.StoryBibleSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId && s.BookId == values.BookId)
            ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        snapshot.SettingDescription = values.SettingDescription;
        snapshot.TimePeriod = values.TimePeriod;
        snapshot.Themes = values.Themes;
        snapshot.ToneAndStyle = values.ToneAndStyle;
        snapshot.WorldRules = values.WorldRules;
        snapshot.Notes = values.Notes;
        snapshot.Reason = values.Reason;
        snapshot.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<StoryBibleSnapshot>> GetStoryBibleSnapshotsAsync(int bookId) =>
        await _db.StoryBibleSnapshots.Where(s => s.BookId == bookId).OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<StoryBibleSnapshot?> GetStoryBibleSnapshotAsync(int bookId, int snapshotId) =>
        await _db.StoryBibleSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId);

    public async Task<StoryBible> RestoreStoryBibleSnapshotAsync(int bookId, int snapshotId)
    {
        var snapshot = await _db.StoryBibleSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId)
            ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        var bible = new StoryBible
        {
            BookId = bookId,
            SettingDescription = snapshot.SettingDescription,
            TimePeriod = snapshot.TimePeriod,
            Themes = snapshot.Themes,
            ToneAndStyle = snapshot.ToneAndStyle,
            WorldRules = snapshot.WorldRules,
            Notes = snapshot.Notes,
        };
        return await UpsertStoryBibleAsync(bible);
    }

    public async Task AddCharactersSnapshotAsync(CharactersSnapshot snapshot)
    {
        snapshot.CreatedAt = DateTime.UtcNow;
        _db.CharactersSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<CharactersSnapshot>> GetCharactersSnapshotsAsync(int bookId) =>
        await _db.CharactersSnapshots.Where(s => s.BookId == bookId).OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<CharactersSnapshot?> GetCharactersSnapshotAsync(int bookId, int snapshotId) =>
        await _db.CharactersSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId);

    public async Task<IEnumerable<CharacterCard>> RestoreCharactersSnapshotAsync(int bookId, int snapshotId)
    {
        var snapshot = await _db.CharactersSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId)
            ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        var cards = System.Text.Json.JsonSerializer.Deserialize<List<CharacterCard>>(snapshot.DataJson) ?? [];
        // Delete current and re-insert
        await DeleteCharacterCardsAsync(bookId);
        foreach (var card in cards)
        {
            card.Id = 0;
            card.BookId = bookId;
            card.CreatedAt = card.UpdatedAt = DateTime.UtcNow;
            _db.CharacterCards.Add(card);
        }
        await _db.SaveChangesAsync();
        return await GetCharacterCardsAsync(bookId);
    }

    public async Task AddPlotThreadsSnapshotAsync(PlotThreadsSnapshot snapshot)
    {
        snapshot.CreatedAt = DateTime.UtcNow;
        _db.PlotThreadsSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<PlotThreadsSnapshot>> GetPlotThreadsSnapshotsAsync(int bookId) =>
        await _db.PlotThreadsSnapshots.Where(s => s.BookId == bookId).OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<PlotThreadsSnapshot?> GetPlotThreadsSnapshotAsync(int bookId, int snapshotId) =>
        await _db.PlotThreadsSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId);

    public async Task<IEnumerable<PlotThread>> RestorePlotThreadsSnapshotAsync(int bookId, int snapshotId)
    {
        var snapshot = await _db.PlotThreadsSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId)
            ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        var threads = System.Text.Json.JsonSerializer.Deserialize<List<PlotThread>>(snapshot.DataJson) ?? [];
        await DeletePlotThreadsAsync(bookId);
        foreach (var thread in threads)
        {
            thread.Id = 0;
            thread.BookId = bookId;
            thread.CreatedAt = thread.UpdatedAt = DateTime.UtcNow;
            _db.PlotThreads.Add(thread);
        }
        await _db.SaveChangesAsync();
        return await GetPlotThreadsAsync(bookId);
    }

    public async Task AddBookSnapshotAsync(BookSnapshot snapshot)
    {
        snapshot.CreatedAt = DateTime.UtcNow;
        _db.BookSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
    }

    public async Task AmendBookSnapshotAsync(int snapshotId, BookSnapshot values)
    {
        var snapshot = await _db.BookSnapshots.FirstOrDefaultAsync(s => s.Id == snapshotId && s.BookId == values.BookId)
            ?? throw new InvalidOperationException($"Snapshot {snapshotId} not found.");
        snapshot.Title = values.Title;
        snapshot.Premise = values.Premise;
        snapshot.Genre = values.Genre;
        snapshot.TargetChapterCount = values.TargetChapterCount;
        snapshot.Language = values.Language;
        snapshot.Mode = values.Mode;
        snapshot.Reason = values.Reason;
        snapshot.CreatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<BookSnapshot>> GetBookSnapshotsAsync(int bookId) =>
        await _db.BookSnapshots.Where(s => s.BookId == bookId).OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<BookSnapshot?> GetBookSnapshotAsync(int bookId, int snapshotId) =>
        await _db.BookSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId);

    // ── Public library ───────────────────────────────────────────────────────

    public async Task<(List<ABook.Core.Interfaces.PublicBookItemDto> Items, int TotalCount)> GetPublicBooksAsync(ABook.Core.Interfaces.PublicBookFilter filter)
    {
        var query = _db.Books
            .Include(b => b.User)
            .Include(b => b.Chapters)
            .AsQueryable();

        // In non-public mode restrict to a specific user's books
        if (filter.UserId.HasValue)
            query = query.Where(b => b.UserId == filter.UserId.Value || b.UserId == null);

        // Only show books that have at least one written (non-archived, non-empty) chapter
        query = query.Where(b => b.Chapters.Any(c => !c.IsArchived && c.Content != null && c.Content != ""));

        // Author filter (case-insensitive partial match against DisplayName or Username)
        if (!string.IsNullOrWhiteSpace(filter.Author))
            query = query.Where(b => b.User != null &&
                (EF.Functions.ILike(b.User.DisplayName ?? b.User.Username, $"%{filter.Author}%")));

        // Genre filter (case-insensitive; genre field is comma-separated so a partial match suffices)
        if (!string.IsNullOrWhiteSpace(filter.Genre))
            query = query.Where(b => EF.Functions.ILike(b.Genre, $"%{filter.Genre}%"));

        var totalCount = await query.CountAsync();

        // Project, apply chapter-count filter, then paginate
        var projected = query.Select(b => new
        {
            Book = b,
            WrittenCount = b.Chapters.Count(c => !c.IsArchived && c.Content != null && c.Content != ""),
            AuthorDisplay = b.User != null ? (b.User.DisplayName ?? b.User.Username) : "Unknown"
        });

        if (filter.ChapterCount.HasValue)
            projected = projected.Where(x => x.WrittenCount == filter.ChapterCount.Value);

        var totalCountFiltered = filter.ChapterCount.HasValue ? await projected.CountAsync() : totalCount;

        var items = await projected
            .OrderByDescending(x => x.Book.UpdatedAt)
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .ToListAsync();

        var dtos = items.Select(x => new ABook.Core.Interfaces.PublicBookItemDto(
            x.Book.Id, x.Book.Title, x.Book.Genre, x.Book.TargetChapterCount,
            x.WrittenCount, x.Book.Status.ToString(), x.Book.Language,
            x.AuthorDisplay, x.Book.CreatedAt, x.Book.UpdatedAt)).ToList();

        return (dtos, totalCountFiltered);
    }

    public async Task<IEnumerable<string>> GetAllGenreStringsAsync(int? userId)
    {
        var query = _db.Books.AsQueryable();
        if (userId.HasValue)
            query = query.Where(b => b.UserId == userId.Value || b.UserId == null);
        var genres = await query
            .Where(b => b.Genre != null && b.Genre != "")
            .Select(b => b.Genre)
            .ToListAsync();
        return genres;
    }
}

