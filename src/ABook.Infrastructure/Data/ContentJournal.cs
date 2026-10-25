using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ABook.Core.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata;

namespace ABook.Infrastructure.Data;

/// <summary>
/// The book's change log (<see cref="ContentRevision"/>): which rows are logged, how a row is written to and read from
/// JSON, and the baseline taken for rows that existed before the log began.
/// </summary>
public static class ContentJournal
{
    /// <summary>The content rows whose every state is logged — what a rewind restores.</summary>
    public static readonly IReadOnlySet<Type> LoggedTypes = new HashSet<Type>
    {
        typeof(Book), typeof(Chapter), typeof(Section), typeof(StoryBible), typeof(CharacterCard), typeof(PlotThread),
        typeof(Clarification), typeof(ChatProposal),
    };

    /// <summary>Changes on their own (timestamps) are not a new state.</summary>
    private static readonly HashSet<string> IgnoredInHash = ["UpdatedAt"];

    public static bool IsLogged(Type type) => LoggedTypes.Contains(type);

    public static int BookIdOf(object entity) => entity switch
    {
        Book b => b.Id,
        Chapter c => c.BookId,
        Section s => s.BookId,
        StoryBible s => s.BookId,
        CharacterCard c => c.BookId,
        PlotThread t => t.BookId,
        Clarification c => c.BookId,
        ChatProposal p => p.BookId,
        _ => throw new ArgumentException($"{entity.GetType().Name} is not a logged content type."),
    };

    /// <summary>The row's scalar columns (current values, or the original ones for a delete) as a JSON object.</summary>
    public static string Serialize(EntityEntry entry, bool original = false)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in entry.Metadata.GetProperties())
            values[p.Name] = AsStored(original ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue);
        return JsonSerializer.Serialize(values);
    }

    /// <summary>The same JSON for a row read without tracking (the baseline).</summary>
    public static string Serialize(IEntityType type, object entity)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in type.GetProperties())
            values[p.Name] = AsStored(p.PropertyInfo?.GetValue(entity) ?? p.FieldInfo?.GetValue(entity));
        return JsonSerializer.Serialize(values);
    }

    /// <summary>
    /// A value as the database keeps it: times to the microsecond (PostgreSQL's precision). A time saved from memory
    /// has 100 ns ticks, and would otherwise never equal the same time read back.
    /// </summary>
    public static object? AsStored(object? value) => value switch
    {
        DateTime d => new DateTime(d.Ticks - d.Ticks % 10, d.Kind),
        DateTimeOffset o => new DateTimeOffset(o.Ticks - o.Ticks % 10, o.Offset),
        _ => value,
    };

    public static string Hash(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var sb = new StringBuilder();
        foreach (var prop in doc.RootElement.EnumerateObject())
            if (!IgnoredInHash.Contains(prop.Name))
                sb.Append(prop.Name).Append('=').Append(prop.Value.GetRawText()).Append('\u001f');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())))[..32];
    }

    /// <summary>The value of one column in a revision, converted to the property's type.</summary>
    public static object? ReadValue(JsonElement data, IProperty property)
    {
        if (!data.TryGetProperty(property.Name, out var element) || element.ValueKind == JsonValueKind.Null) return null;
        return element.Deserialize(property.ClrType);
    }

    /// <summary>
    /// Writes a baseline revision for every logged row of every book that has none yet — the state each book had when
    /// the change log began, which is as far back as a rewind can go. Runs at startup; cheap once every book has one.
    /// </summary>
    public static async Task<int> WriteBaselinesAsync(AppDbContext db, CancellationToken ct = default)
    {
        var bookIds = await db.Books
            .Where(b => !db.ContentRevisions.Any(r => r.BookId == b.Id))
            .Select(b => b.Id)
            .ToListAsync(ct);
        var now = DateTime.UtcNow;
        foreach (var bookId in bookIds)
        {
            var rows = new List<object>();
            rows.AddRange(await db.Books.AsNoTracking().Where(b => b.Id == bookId).ToListAsync(ct));
            rows.AddRange(await db.Chapters.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            rows.AddRange(await db.Sections.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            rows.AddRange(await db.StoryBibles.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            rows.AddRange(await db.CharacterCards.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            rows.AddRange(await db.PlotThreads.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            rows.AddRange(await db.Clarifications.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            rows.AddRange(await db.ChatProposals.AsNoTracking().Where(x => x.BookId == bookId).ToListAsync(ct));
            foreach (var row in rows)
            {
                var type = db.Model.FindEntityType(row.GetType())!;
                var json = Serialize(type, row);
                db.ContentRevisions.Add(new ContentRevision
                {
                    BookId = bookId, EntityType = row.GetType().Name, EntityId = IdOf(type, row),
                    Op = ContentRevisionOp.Insert, DataJson = json, DataHash = Hash(json), IsBaseline = true, At = now,
                });
            }
            await db.SaveChangesWithoutLogAsync(ct);
            db.ChangeTracker.Clear();
        }
        return bookIds.Count + await WriteSectionBaselinesAsync(db, ct);
    }

    /// <summary>
    /// Sections made by the migration that introduced them (one per chapter) have no revision. Without one, a rewind
    /// to any earlier moment would think they did not exist yet and archive them, so each gets a baseline dated at its
    /// book's first revision: as far as the log is concerned, the book always had them.
    /// </summary>
    private static async Task<int> WriteSectionBaselinesAsync(AppDbContext db, CancellationToken ct)
    {
        var sections = await db.Sections.AsNoTracking()
            .Where(s => !db.ContentRevisions.Any(r => r.EntityType == nameof(Section) && r.EntityId == s.Id))
            .ToListAsync(ct);
        if (sections.Count == 0) return 0;
        var bookIds = sections.Select(s => s.BookId).Distinct().ToList();
        var firstAt = await db.ContentRevisions.Where(r => bookIds.Contains(r.BookId))
            .GroupBy(r => r.BookId).Select(g => new { g.Key, At = g.Min(r => r.At) })
            .ToDictionaryAsync(x => x.Key, x => x.At, ct);
        var type = db.Model.FindEntityType(typeof(Section))!;
        foreach (var s in sections)
        {
            var json = Serialize(type, s);
            db.ContentRevisions.Add(new ContentRevision
            {
                BookId = s.BookId, EntityType = nameof(Section), EntityId = s.Id, Op = ContentRevisionOp.Insert,
                DataJson = json, DataHash = Hash(json), IsBaseline = true,
                At = firstAt.TryGetValue(s.BookId, out var at) ? at : DateTime.UtcNow,
            });
        }
        await db.SaveChangesWithoutLogAsync(ct);
        db.ChangeTracker.Clear();
        return sections.Count;
    }

    private static int IdOf(IEntityType type, object entity) =>
        Convert.ToInt32(type.FindPrimaryKey()!.Properties[0].PropertyInfo!.GetValue(entity));

    /// <summary>Hash of the newest revision per row, for rows saved through this process (skips a lookup per save).</summary>
    internal static readonly ConcurrentDictionary<(string Type, int Id), string> LastHashes = new();
}

public partial class AppDbContext
{
    private bool _savingLog;

    private sealed record PendingRevision(EntityEntry Entry, ContentRevisionOp Op, string? DeletedJson, int BookId);

    /// <summary>
    /// A section with a single active chapter has that chapter's plan: whatever saves a chapter's plan (an edit, a
    /// version, the planner, a rewind) saves it on the section too. True when a section was changed.
    /// </summary>
    private async Task<bool> MirrorSingleChapterPlansAsync(CancellationToken ct)
    {
        var changed = false;
        var chapters = ChangeTracker.Entries<Chapter>()
            .Where(e => e.State is EntityState.Added or EntityState.Modified && !e.Entity.IsArchived
                && (e.Entity.Section is not null || e.Entity.SectionId is not null)
                && (e.State == EntityState.Added || e.Property(nameof(Chapter.SectionId)).IsModified
                    || e.Property(nameof(Chapter.IsArchived)).IsModified
                    || Section.PlanFields.Any(f => e.Property(f).IsModified)))
            .Select(e => e.Entity).ToList();
        foreach (var chapter in chapters)
        {
            var section = chapter.Section ?? await Sections.FindAsync([chapter.SectionId!.Value], ct);
            if (section is null) continue;
            // The other chapters of the section that are active, as they will be after this save.
            var tracked = ChangeTracker.Entries<Chapter>()
                .Where(e => e.State != EntityState.Deleted && !ReferenceEquals(e.Entity, chapter)
                    && (ReferenceEquals(e.Entity.Section, section) || (section.Id != 0 && e.Entity.SectionId == section.Id)))
                .Select(e => e.Entity).ToList();
            var trackedIds = tracked.Where(c => c.Id != 0).Select(c => c.Id).ToList();
            var othersActive = tracked.Any(c => !c.IsArchived)
                || (section.Id != 0 && await Chapters.AnyAsync(c => c.SectionId == section.Id && !c.IsArchived
                    && c.Id != chapter.Id && !trackedIds.Contains(c.Id), ct));
            if (othersActive) continue;
            if (section.CopyPlanFrom(chapter))
            {
                section.UpdatedAt = DateTime.UtcNow;
                changed = true;
            }
        }
        return changed;
    }

    /// <summary>Saves without adding change-log entries (the log's own writes, the baseline).</summary>
    public async Task<int> SaveChangesWithoutLogAsync(CancellationToken ct = default)
    {
        _savingLog = true;
        try { return await base.SaveChangesAsync(true, ct); }
        finally { _savingLog = false; }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess) =>
        SaveChangesAsync(acceptAllChangesOnSuccess).GetAwaiter().GetResult();

    /// <summary>
    /// Saves, then records the new state of every logged content row the save touched — in the same transaction, so
    /// a change and its log entry are kept or lost together.
    /// </summary>
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken ct = default)
    {
        if (_savingLog) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        ChangeTracker.DetectChanges();
        if (await MirrorSingleChapterPlansAsync(ct)) ChangeTracker.DetectChanges();
        var deletedBooks = ChangeTracker.Entries<Book>().Where(e => e.State == EntityState.Deleted)
            .Select(e => e.Entity.Id).ToHashSet();
        var pending = new List<PendingRevision>();
        foreach (var entry in ChangeTracker.Entries())
        {
            if (!ContentJournal.IsLogged(entry.Metadata.ClrType)) continue;
            if (entry.State is not (EntityState.Added or EntityState.Modified or EntityState.Deleted)) continue;
            // A book being deleted takes its log with it.
            var bookId = entry.State == EntityState.Added && entry.Entity is Book ? 0 : ContentJournal.BookIdOf(entry.Entity);
            if (deletedBooks.Contains(bookId)) continue;
            pending.Add(new PendingRevision(entry,
                entry.State switch
                {
                    EntityState.Added => ContentRevisionOp.Insert,
                    EntityState.Deleted => ContentRevisionOp.Delete,
                    _ => ContentRevisionOp.Update,
                },
                entry.State == EntityState.Deleted ? ContentJournal.Serialize(entry, original: true) : null,
                bookId));
        }
        if (pending.Count == 0) return await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);

        var ownTransaction = Database.CurrentTransaction is null ? await Database.BeginTransactionAsync(ct) : null;
        var cached = new List<((string, int) Key, string Hash)>();
        try
        {
            var result = await base.SaveChangesAsync(acceptAllChangesOnSuccess, ct);
            var now = DateTime.UtcNow;
            var revisions = new List<ContentRevision>();
            foreach (var p in pending)
            {
                var entity = p.Entry.Entity;
                var json = p.DeletedJson ?? ContentJournal.Serialize(p.Entry);
                var hash = ContentJournal.Hash(json);
                var type = p.Entry.Metadata.ClrType.Name;
                var id = Convert.ToInt32(p.Entry.Metadata.FindPrimaryKey()!.Properties[0].PropertyInfo!.GetValue(entity));
                var key = (type, id);
                if (p.Op == ContentRevisionOp.Update)
                {
                    // Only trust the cache when this save owns its transaction (a caller's could still roll back).
                    var last = ownTransaction is not null && ContentJournal.LastHashes.TryGetValue(key, out var h)
                        ? h
                        : await ContentRevisions.Where(r => r.EntityType == type && r.EntityId == id)
                            .OrderByDescending(r => r.Id).Select(r => r.DataHash).FirstOrDefaultAsync(ct);
                    if (last == hash) continue;
                }
                revisions.Add(new ContentRevision
                {
                    BookId = entity is Book book ? book.Id : p.BookId,
                    EntityType = type, EntityId = id, Op = p.Op, DataJson = json, DataHash = hash, At = now,
                });
                cached.Add((key, hash));
            }
            if (revisions.Count > 0)
            {
                ContentRevisions.AddRange(revisions);
                await SaveChangesWithoutLogAsync(ct);
                // The log is write-only here: keep long-running contexts (agent runs) from holding every revision.
                foreach (var r in revisions) Entry(r).State = EntityState.Detached;
            }
            if (ownTransaction is not null) await ownTransaction.CommitAsync(ct);
            foreach (var (key, hash) in cached) ContentJournal.LastHashes[key] = hash;
            return result;
        }
        catch
        {
            if (ownTransaction is not null) await ownTransaction.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (ownTransaction is not null) await ownTransaction.DisposeAsync();
        }
    }
}
