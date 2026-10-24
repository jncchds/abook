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
        typeof(Book), typeof(Chapter), typeof(StoryBible), typeof(CharacterCard), typeof(PlotThread),
        typeof(Clarification), typeof(ChatProposal),
    };

    /// <summary>Changes on their own (timestamps) are not a new state.</summary>
    private static readonly HashSet<string> IgnoredInHash = ["UpdatedAt"];

    public static bool IsLogged(Type type) => LoggedTypes.Contains(type);

    public static int BookIdOf(object entity) => entity switch
    {
        Book b => b.Id,
        Chapter c => c.BookId,
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
            values[p.Name] = original ? entry.Property(p.Name).OriginalValue : entry.Property(p.Name).CurrentValue;
        return JsonSerializer.Serialize(values);
    }

    /// <summary>The same JSON for a row read without tracking (the baseline).</summary>
    public static string Serialize(IEntityType type, object entity)
    {
        var values = new SortedDictionary<string, object?>(StringComparer.Ordinal);
        foreach (var p in type.GetProperties())
            values[p.Name] = p.PropertyInfo?.GetValue(entity) ?? p.FieldInfo?.GetValue(entity);
        return JsonSerializer.Serialize(values);
    }

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
        return bookIds.Count;
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
