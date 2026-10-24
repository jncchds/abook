using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using ABook.Infrastructure.VectorStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using static ABook.Infrastructure.Rewind.RewindPlanner;

namespace ABook.Infrastructure.Rewind;

/// <summary>
/// Rewinds a book with its change log (<see cref="ContentRevision"/>): the state of every logged row at the moment is
/// its newest revision at or before it. Restoring writes that state as a new one — chapters get a new version (the
/// versions in between stay), the Story Bible a new snapshot, characters and threads a new version, rows created
/// since are archived — so every part's history stays complete. Book-chat messages after the moment are hidden.
/// </summary>
public sealed class BookRewinder(AppDbContext db) : IBookRewinder
{
    public const string CreatedBy = "rewind";

    private static readonly string[] ChapterVersionFields =
    [
        nameof(Chapter.Title), nameof(Chapter.Outline), nameof(Chapter.Content), nameof(Chapter.Status),
        nameof(Chapter.PovCharacter), nameof(Chapter.CharactersInvolvedJson), nameof(Chapter.PlotThreadsJson),
        nameof(Chapter.ForeshadowingNotes), nameof(Chapter.PayoffNotes),
    ];

    public async Task<DateTime?> EarliestAsync(int bookId, CancellationToken ct = default) =>
        await db.ContentRevisions.Where(r => r.BookId == bookId).MinAsync(r => (DateTime?)r.At, ct);

    // ── Points ────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RewindPoint>> PointsAsync(int bookId, DateTime? before = null, int limit = 30, CancellationToken ct = default)
    {
        var earliest = await EarliestAsync(bookId, ct);
        if (earliest is null) return [];
        var upTo = before ?? DateTime.MaxValue;
        var points = new List<RewindPoint>();

        var messages = await db.BookChatMessages.AsNoTracking()
            .Where(m => m.BookId == bookId && m.HiddenAt == null && m.Role != BookChatRole.Event && m.CreatedAt >= earliest && m.CreatedAt < upTo)
            .OrderByDescending(m => m.Id).Take(limit)
            .Select(m => new { m.Id, m.Role, m.Content, m.CreatedAt, Proposals = m.Proposals.Select(p => p.Id).ToList() })
            .ToListAsync(ct);
        foreach (var m in messages)
        {
            if (m.Role == BookChatRole.Author)
            {
                points.Add(new RewindPoint(m.CreatedAt.AddTicks(-1), "chat-before", $"Before your message “{Short(m.Content, 80)}”"));
                continue;
            }
            // "After the reply" includes the suggestions saved with it, but nothing applied later.
            var at = m.CreatedAt;
            if (m.Proposals.Count > 0)
                at = await db.ContentRevisions
                    .Where(r => r.EntityType == nameof(ChatProposal) && m.Proposals.Contains(r.EntityId) && r.Op == ContentRevisionOp.Insert)
                    .MaxAsync(r => (DateTime?)r.At, ct) ?? at;
            points.Add(new RewindPoint(at, "chat-after", $"After the reply “{Short(m.Content, 80)}”"));
        }

        var versions = await db.ChapterVersions.AsNoTracking()
            .Where(v => v.BookId == bookId && v.CreatedAt >= earliest && v.CreatedAt < upTo)
            .OrderByDescending(v => v.Id).Take(limit)
            .Join(db.Chapters, v => v.ChapterId, c => c.Id, (v, c) => new { v.ChapterId, v.VersionNumber, v.CreatedBy, v.CreatedAt, c.Number })
            .ToListAsync(ct);
        foreach (var v in versions)
        {
            // The chapter row is updated with the version, a moment after it: take that moment, so the point includes it.
            var synced = await db.ContentRevisions
                .Where(r => r.EntityType == nameof(Chapter) && r.EntityId == v.ChapterId && r.At >= v.CreatedAt && r.At <= v.CreatedAt.AddSeconds(10))
                .MinAsync(r => (DateTime?)r.At, ct);
            points.Add(new RewindPoint(synced ?? v.CreatedAt, "chapter-version",
                $"After Chapter {v.Number} v{v.VersionNumber}{(v.CreatedBy.Length > 0 ? $" ({v.CreatedBy})" : "")}"));
        }

        var runs = await db.AgentRuns.AsNoTracking()
            .Where(r => r.BookId == bookId && r.UpdatedAt >= earliest && r.CreatedAt < upTo)
            .OrderByDescending(r => r.CreatedAt).Take(limit)
            .ToListAsync(ct);
        foreach (var r in runs.Where(r => r.RunType != "rewind"))
        {
            var what = RunWords(r.RunType);
            if (r.CreatedAt >= earliest) points.Add(new RewindPoint(r.CreatedAt.AddTicks(-1), "run-before", $"Before {what} started"));
            if (r.Status is not (AgentRunPersistStatus.Running or AgentRunPersistStatus.WaitingForInput) && r.UpdatedAt < upTo)
                points.Add(new RewindPoint(r.UpdatedAt, "run-after", $"After {what} {StatusWords(r.Status)}"));
        }

        return points.Where(p => p.At >= earliest && p.At < upTo)
            .OrderByDescending(p => p.At)
            .DistinctBy(p => (p.At, p.Kind))
            .Take(limit)
            .ToList();
    }

    private static string Short(string text, int max)
    {
        text = text.ReplaceLineEndings(" ").Trim();
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }

    private static string RunWords(string runType) => runType switch
    {
        "workflow" => "the workflow",
        "plan" => "planning",
        "write" => "writing",
        "edit" => "editing",
        "continuity" => "a continuity check",
        "illustrate-plan" => "illustration planning",
        "illustrate" => "illustrating",
        "chat" => "a book-chat reply",
        "chat-apply" => "applying changes",
        "chat-undo" => "an undo",
        _ => runType,
    };

    private static string StatusWords(AgentRunPersistStatus s) => s switch
    {
        AgentRunPersistStatus.Completed => "finished",
        AgentRunPersistStatus.Failed => "failed",
        AgentRunPersistStatus.Cancelled => "was stopped",
        _ => "ended",
    };

    // ── Preview ───────────────────────────────────────────────────────────────

    public async Task<RewindPlan> PreviewAsync(int bookId, DateTime at, CancellationToken ct = default) =>
        (await PlanAsync(bookId, at, ct)).Plan;

    private sealed record Planned(RewindPlan Plan, IReadOnlyList<PlannedChange> Changes);

    private async Task<Planned> PlanAsync(int bookId, DateTime at, CancellationToken ct)
    {
        var earliest = await EarliestAsync(bookId, ct) ?? throw new RewindTooEarlyException(DateTime.UtcNow);
        if (at < earliest) throw new RewindTooEarlyException(earliest);

        // The newest revision of each row at the moment.
        var latestIds = await db.ContentRevisions
            .Where(r => r.BookId == bookId && r.At <= at)
            .GroupBy(r => new { r.EntityType, r.EntityId })
            .Select(g => g.Max(r => r.Id))
            .ToListAsync(ct);
        var atMoment = (await db.ContentRevisions.AsNoTracking().Where(r => latestIds.Contains(r.Id)).ToListAsync(ct))
            .ToDictionary(r => new RowKey(r.EntityType, r.EntityId));

        var current = new Dictionary<RowKey, string>();
        async Task AddRows<T>(IQueryable<T> rows) where T : class
        {
            var type = db.Model.FindEntityType(typeof(T))!;
            foreach (var row in await rows.AsNoTracking().ToListAsync(ct))
            {
                var id = Convert.ToInt32(type.FindPrimaryKey()!.Properties[0].PropertyInfo!.GetValue(row));
                current[new RowKey(typeof(T).Name, id)] = ContentJournal.Serialize(type, row);
            }
        }
        await AddRows(db.Books.Where(b => b.Id == bookId));
        await AddRows(db.Chapters.Where(x => x.BookId == bookId));
        await AddRows(db.StoryBibles.Where(x => x.BookId == bookId));
        await AddRows(db.CharacterCards.Where(x => x.BookId == bookId));
        await AddRows(db.PlotThreads.Where(x => x.BookId == bookId));
        await AddRows(db.Clarifications.Where(x => x.BookId == bookId));
        await AddRows(db.ChatProposals.Where(x => x.BookId == bookId));

        var changes = RewindPlanner.Plan(atMoment, current);
        var hidden = await db.BookChatMessages.CountAsync(m => m.BookId == bookId && m.HiddenAt == null && m.CreatedAt > at, ct);
        var plan = new RewindPlan(bookId, at,
            changes.Select(c => new RewindChange(c.Key.Type, c.Key.Id, c.Kind, c.Label, c.Fields)).ToList(),
            hidden, await FingerprintAsync(bookId, ct));
        return new Planned(plan, changes);
    }

    private async Task<string> FingerprintAsync(int bookId, CancellationToken ct)
    {
        var lastRevision = await db.ContentRevisions.Where(r => r.BookId == bookId).MaxAsync(r => (long?)r.Id, ct) ?? 0;
        var lastMessage = await db.BookChatMessages.Where(m => m.BookId == bookId).MaxAsync(m => (int?)m.Id, ct) ?? 0;
        return $"{lastRevision}-{lastMessage}";
    }

    // ── Rewind ────────────────────────────────────────────────────────────────

    public async Task<RewindResult> RewindAsync(int bookId, DateTime at, string? fingerprint, string? origin, CancellationToken ct = default)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var planned = await PlanAsync(bookId, at, ct);
        if (fingerprint is not null && fingerprint != planned.Plan.Fingerprint) throw new RewindStaleException();

        var changedChapters = new List<int>();
        var toIndex = new List<(int, int)>();
        var now = DateTime.UtcNow;

        foreach (var change in planned.Changes)
        {
            ct.ThrowIfCancellationRequested();
            switch (change.Key.Type)
            {
                case nameof(Book): await RestoreBookAsync(change, origin, ct); break;
                case nameof(Chapter): await RestoreChapterAsync(change, now, toIndex, ct); changedChapters.Add(change.Key.Id); break;
                case nameof(StoryBible): await RestoreBibleAsync(change, ct); break;
                case nameof(CharacterCard): await RestoreCardAsync<CharacterCard, CharacterCardVersion>(change, nameof(CharacterCardVersion.CharacterCardId), ct); break;
                case nameof(PlotThread): await RestoreCardAsync<PlotThread, PlotThreadVersion>(change, nameof(PlotThreadVersion.PlotThreadId), ct); break;
                case nameof(Clarification): await RestoreRowAsync<Clarification>(change, ct); break;
                case nameof(ChatProposal): await RestoreProposalAsync(change, ct); break;
            }
        }

        var hidden = await db.BookChatMessages
            .Where(m => m.BookId == bookId && m.HiddenAt == null && m.CreatedAt > at).ToListAsync(ct);
        foreach (var m in hidden) m.HiddenAt = now;
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        db.ChangeTracker.Clear();

        return new RewindResult(planned.Plan, changedChapters, toIndex, hidden.Select(m => m.Id).ToList());
    }

    /// <summary>The tracked row, or for a recreate a new one carrying its old id.</summary>
    private async Task<EntityEntry> LoadOrCreateAsync<T>(PlannedChange change, CancellationToken ct) where T : class, new()
    {
        var entity = await db.Set<T>().FindAsync([change.Key.Id], ct);
        if (entity is not null) return db.Entry(entity);
        entity = new T();
        var entry = db.Entry(entity);
        using var doc = JsonDocument.Parse(change.TargetJson!);
        foreach (var p in entry.Metadata.GetProperties())
            entry.Property(p.Name).CurrentValue = ContentJournal.ReadValue(doc.RootElement, p) ?? entry.Property(p.Name).CurrentValue;
        db.Set<T>().Add(entity);
        return entry;
    }

    /// <summary>Writes back the restored columns of <paramref name="change"/>'s target state.</summary>
    private static void Apply(EntityEntry entry, PlannedChange change)
    {
        using var doc = JsonDocument.Parse(change.TargetJson!);
        foreach (var p in entry.Metadata.GetProperties())
            if (IsRestored(change.Key.Type, p.Name) && doc.RootElement.TryGetProperty(p.Name, out _))
                entry.Property(p.Name).CurrentValue = ContentJournal.ReadValue(doc.RootElement, p);
        if (entry.Metadata.FindProperty("UpdatedAt") is not null) entry.Property("UpdatedAt").CurrentValue = DateTime.UtcNow;
    }

    private async Task RestoreBookAsync(PlannedChange change, string? origin, CancellationToken ct)
    {
        var book = await db.Books.FindAsync([change.Key.Id], ct);
        if (book is null) return;
        // The book's own history (Book Settings) keeps how it was before the rewind.
        db.BookSnapshots.Add(new BookSnapshot
        {
            BookId = book.Id, Title = book.Title, Premise = book.Premise, Genre = book.Genre,
            TargetChapterCount = book.TargetChapterCount, Language = book.Language, Mode = book.Mode,
            Reason = origin is null ? "rewind" : $"rewind ({origin})", CreatedAt = DateTime.UtcNow,
        });
        Apply(db.Entry(book), change);
    }

    private async Task RestoreChapterAsync(PlannedChange change, DateTime now, List<(int, int)> toIndex, CancellationToken ct)
    {
        if (change.Kind == RewindChangeKind.Archive)
        {
            if (await db.Chapters.FindAsync([change.Key.Id], ct) is { } archived)
            {
                archived.IsArchived = true;
                archived.UpdatedAt = now;
            }
            return;
        }

        var entry = await LoadOrCreateAsync<Chapter>(change, ct);
        var chapter = (Chapter)entry.Entity;
        Apply(entry, change);
        await db.SaveChangesAsync(ct);

        using var doc = JsonDocument.Parse(change.TargetJson!);
        var target = doc.RootElement;
        var textChanged = change.Kind == RewindChangeKind.Recreate
            || ChapterVersionFields.Any(f => change.Fields.Contains(FieldWords(f)));
        if (!textChanged) return;

        // A new active version holding the state of the moment; the versions in between stay in the history.
        var versionNumber = (await db.ChapterVersions.Where(v => v.ChapterId == chapter.Id).MaxAsync(v => (int?)v.VersionNumber, ct) ?? 0) + 1;
        await db.ChapterVersions.Where(v => v.ChapterId == chapter.Id).ExecuteUpdateAsync(s => s.SetProperty(v => v.IsActive, false), ct);
        var version = new ChapterVersion
        {
            ChapterId = chapter.Id, BookId = chapter.BookId, VersionNumber = versionNumber,
            Title = chapter.Title, Outline = chapter.Outline, Content = chapter.Content, Status = chapter.Status,
            PovCharacter = chapter.PovCharacter, CharactersInvolvedJson = chapter.CharactersInvolvedJson,
            PlotThreadsJson = chapter.PlotThreadsJson, ForeshadowingNotes = chapter.ForeshadowingNotes,
            PayoffNotes = chapter.PayoffNotes, CreatedBy = CreatedBy, IsActive = true, CreatedAt = now,
        };
        db.ChapterVersions.Add(version);
        await db.SaveChangesAsync(ct);

        // The same text was embedded before: copy its chunks instead of embedding it again.
        var source = string.IsNullOrEmpty(chapter.Content) ? null : await db.ChapterVersions.AsNoTracking()
            .Where(v => v.ChapterId == chapter.Id && v.Id != version.Id && v.HasEmbeddings && v.Content == chapter.Content)
            .OrderByDescending(v => v.Id).FirstOrDefaultAsync(ct);
        if (source is not null)
        {
            var chunks = await db.ChapterEmbeddings.AsNoTracking().Where(e => e.ChapterVersionId == source.Id).ToListAsync(ct);
            foreach (var c in chunks)
                db.ChapterEmbeddings.Add(new ChapterEmbedding
                {
                    BookId = c.BookId, ChapterId = c.ChapterId, ChapterNumber = chapter.Number, ChunkIndex = c.ChunkIndex,
                    ChapterVersionId = version.Id, Text = c.Text, Embedding = c.Embedding,
                });
            version.HasEmbeddings = chunks.Count > 0;
        }
        if (!version.HasEmbeddings && !string.IsNullOrEmpty(chapter.Content)) toIndex.Add((chapter.Id, version.Id));

        // The summary stays valid when it was made from this very text.
        var summaryVersionId = target.TryGetProperty(nameof(Chapter.SummaryVersionId), out var s) && s.ValueKind == JsonValueKind.Number
            ? s.GetInt32() : (int?)null;
        if (summaryVersionId is int sid
            && await db.ChapterVersions.AnyAsync(v => v.Id == sid && v.Content == chapter.Content, ct))
            chapter.SummaryVersionId = version.Id;
        else
            chapter.SummaryVersionId = summaryVersionId;
        await db.SaveChangesAsync(ct);
    }

    private async Task RestoreBibleAsync(PlannedChange change, CancellationToken ct)
    {
        var existing = await db.StoryBibles.FindAsync([change.Key.Id], ct);
        if (existing is not null)
            db.StoryBibleSnapshots.Add(BibleSnapshot(existing, change.Kind == RewindChangeKind.Archive ? "rewind (removed)" : "rewind (before)"));
        if (change.Kind == RewindChangeKind.Archive)
        {
            // The book had no Story Bible yet: the snapshot above keeps the one it has now.
            if (existing is not null) db.StoryBibles.Remove(existing);
            await db.SaveChangesAsync(ct);
            return;
        }
        var entry = await LoadOrCreateAsync<StoryBible>(change, ct);
        Apply(entry, change);
        await db.SaveChangesAsync(ct);
        db.StoryBibleSnapshots.Add(BibleSnapshot((StoryBible)entry.Entity, "rewind"));
        await db.SaveChangesAsync(ct);
    }

    private static StoryBibleSnapshot BibleSnapshot(StoryBible b, string reason) => new()
    {
        BookId = b.BookId, SettingDescription = b.SettingDescription, TimePeriod = b.TimePeriod, Themes = b.Themes,
        ToneAndStyle = b.ToneAndStyle, WorldRules = b.WorldRules, Notes = b.Notes, Reason = reason, CreatedAt = DateTime.UtcNow,
    };

    /// <summary>A character or plot thread: restored (or archived) and, when restored, a new version in its history.</summary>
    private async Task RestoreCardAsync<TCard, TVersion>(PlannedChange change, string cardIdProperty, CancellationToken ct)
        where TCard : class, new() where TVersion : class, new()
    {
        var entry = change.Kind == RewindChangeKind.Archive
            ? db.Entry((await db.Set<TCard>().FindAsync([change.Key.Id], ct))!)
            : await LoadOrCreateAsync<TCard>(change, ct);
        if (change.Kind == RewindChangeKind.Archive)
        {
            entry.Property("IsArchived").CurrentValue = true;
            entry.Property("UpdatedAt").CurrentValue = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return;
        }
        Apply(entry, change);
        await db.SaveChangesAsync(ct);

        var cardId = change.Key.Id;
        var number = await db.Set<TVersion>().Where(v => EF.Property<int>(v, cardIdProperty) == cardId)
            .MaxAsync(v => (int?)EF.Property<int>(v, "VersionNumber"), ct) ?? 0;
        var version = new TVersion();
        var versionEntry = db.Entry(version);
        foreach (var p in versionEntry.Metadata.GetProperties())
            if (p.Name != "Id" && entry.Metadata.FindProperty(p.Name) is { } source)
                versionEntry.Property(p.Name).CurrentValue = entry.Property(source.Name).CurrentValue;
        versionEntry.Property(cardIdProperty).CurrentValue = cardId;
        versionEntry.Property("VersionNumber").CurrentValue = number + 1;
        versionEntry.Property("CreatedBy").CurrentValue = CreatedBy;
        versionEntry.Property("CreatedAt").CurrentValue = DateTime.UtcNow;
        db.Set<TVersion>().Add(version);
        await db.SaveChangesAsync(ct);
    }

    private async Task RestoreRowAsync<T>(PlannedChange change, CancellationToken ct) where T : class, new()
    {
        if (change.Kind == RewindChangeKind.Archive)
        {
            if (await db.Set<T>().FindAsync([change.Key.Id], ct) is { } row)
            {
                var e = db.Entry(row);
                e.Property("IsArchived").CurrentValue = true;
                e.Property("UpdatedAt").CurrentValue = DateTime.UtcNow;
            }
        }
        else Apply(await LoadOrCreateAsync<T>(change, ct), change);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>A suggestion back to its state (Pending again when applied since); one made since is rejected.</summary>
    private async Task RestoreProposalAsync(PlannedChange change, CancellationToken ct)
    {
        if (change.Kind == RewindChangeKind.Archive)
        {
            if (await db.ChatProposals.FindAsync([change.Key.Id], ct) is { } p)
            {
                p.Status = ChatProposalStatus.Rejected;
                p.ResultNote = "Rewound — made after the moment the book was rewound to.";
                p.UpdatedAt = DateTime.UtcNow;
            }
        }
        else Apply(await LoadOrCreateAsync<ChatProposal>(change, ct), change);
        await db.SaveChangesAsync(ct);
    }
}
