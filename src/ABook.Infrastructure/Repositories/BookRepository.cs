using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ABook.Infrastructure.Repositories;

public class BookRepository : IBookRepository
{
    private readonly AppDbContext _db;

    public BookRepository(AppDbContext db) => _db = db;

    public async Task<IEnumerable<Book>> GetAllAsync(int? userId = null)
    {
        var query = _db.Books.AsQueryable();
        if (userId.HasValue)
            query = query.Where(b => b.UserId == userId || b.UserId == null);
        return await query.OrderByDescending(b => b.UpdatedAt).ToListAsync();
    }

    public async Task<Book?> GetByIdAsync(int id) =>
        await _db.Books.FindAsync(id);

    public async Task<Book?> GetByIdWithDetailsAsync(int id) =>
        await _db.Books
            .Include(b => b.Chapters.OrderBy(c => c.Number))
            .Include(b => b.LlmConfigurations)
            .FirstOrDefaultAsync(b => b.Id == id);

    public async Task<Book> AddAsync(Book book)
    {
        book.CreatedAt = book.UpdatedAt = DateTime.UtcNow;
        _db.Books.Add(book);
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
        return await query.OrderBy(c => c.Number).ToListAsync();
    }

    public async Task<Chapter?> GetChapterAsync(int bookId, int chapterId) =>
        await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId);

    public async Task<Chapter> AddChapterAsync(Chapter chapter)
    {
        chapter.CreatedAt = chapter.UpdatedAt = DateTime.UtcNow;
        _db.Chapters.Add(chapter);
        await _db.SaveChangesAsync();
        return chapter;
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
        await _db.SaveChangesAsync();
    }

    public async Task ArchiveChapterAsync(int bookId, int chapterId)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId);
        if (chapter is not null)
        {
            chapter.IsArchived = true;
            chapter.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task RestoreChapterAsync(int bookId, int chapterId)
    {
        var chapter = await _db.Chapters.FirstOrDefaultAsync(c => c.BookId == bookId && c.Id == chapterId);
        if (chapter is not null)
        {
            chapter.IsArchived = false;
            chapter.UpdatedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync();
        }
    }

    public async Task ArchiveChaptersAsync(int bookId)
    {
        await _db.Chapters
            .Where(c => c.BookId == bookId && !c.IsArchived)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.IsArchived, true)
                .SetProperty(c => c.UpdatedAt, DateTime.UtcNow));
    }

    // ── Chapter Versions ─────────────────────────────────────────────────────

    public async Task<ChapterVersion> AddChapterVersionAsync(ChapterVersion version)
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
        return version;
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

    public async Task<ChapterVersion> ActivateChapterVersionAsync(int bookId, int chapterId, int versionId)
    {
        var version = await _db.ChapterVersions.FirstOrDefaultAsync(v => v.ChapterId == chapterId && v.Id == versionId)
            ?? throw new InvalidOperationException($"Version {versionId} not found for chapter {chapterId}.");

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
        return version;
    }

    public async Task<IEnumerable<AgentMessage>> GetMessagesAsync(int bookId, int? chapterId = null)
    {
        var query = _db.AgentMessages.Where(m => m.BookId == bookId);
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

    public async Task<LlmConfiguration?> GetLlmConfigAsync(int? bookId, int? userId = null)
    {
        if (bookId.HasValue)
        {
            var bookConfig = await _db.LlmConfigurations.FirstOrDefaultAsync(l => l.BookId == bookId);
            if (bookConfig is not null) return bookConfig;
        }
        if (userId.HasValue)
        {
            var userConfig = await _db.LlmConfigurations
                .FirstOrDefaultAsync(l => l.BookId == null && l.UserId == userId);
            if (userConfig is not null) return userConfig;
        }
        return await _db.LlmConfigurations.FirstOrDefaultAsync(l => l.BookId == null && l.UserId == null);
    }

    public async Task<LlmConfiguration> UpsertLlmConfigAsync(LlmConfiguration config)
    {
        var existing = await _db.LlmConfigurations
            .FirstOrDefaultAsync(l => l.BookId == config.BookId && l.UserId == config.UserId);

        if (existing is null)
        {
            _db.LlmConfigurations.Add(config);
        }
        else
        {
            existing.Provider = config.Provider;
            existing.ModelName = config.ModelName;
            existing.Endpoint = config.Endpoint;
            existing.ApiKey = config.ApiKey;
            existing.EmbeddingModelName = config.EmbeddingModelName;
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
            .Where(r => r.BookId == bookId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

    public async Task DeleteTokenUsageAsync(int bookId)
    {
        var records = await _db.TokenUsageRecords.Where(r => r.BookId == bookId).ToListAsync();
        _db.TokenUsageRecords.RemoveRange(records);
        await _db.SaveChangesAsync();
    }

    public async Task DeleteMessagesAsync(int bookId)
    {
        var messages = await _db.AgentMessages.Where(m => m.BookId == bookId).ToListAsync();
        _db.AgentMessages.RemoveRange(messages);
        await _db.SaveChangesAsync();
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

    public async Task UpdateCharacterCardAsync(CharacterCard card)
    {
        card.UpdatedAt = DateTime.UtcNow;
        _db.CharacterCards.Update(card);
        await _db.SaveChangesAsync();
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

    public async Task DeleteCharacterCardsAsync(int bookId)
    {
        var cards = await _db.CharacterCards.Where(c => c.BookId == bookId).ToListAsync();
        _db.CharacterCards.RemoveRange(cards);
        await _db.SaveChangesAsync();
    }

    // ── Plot Threads ──────────────────────────────────────────────────────────

    public async Task<IEnumerable<PlotThread>> GetPlotThreadsAsync(int bookId) =>
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

    public async Task UpdatePlotThreadAsync(PlotThread thread)
    {
        thread.UpdatedAt = DateTime.UtcNow;
        _db.PlotThreads.Update(thread);
        await _db.SaveChangesAsync();
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

    public async Task DeletePlotThreadsAsync(int bookId)
    {
        var threads = await _db.PlotThreads.Where(p => p.BookId == bookId).ToListAsync();
        _db.PlotThreads.RemoveRange(threads);
        await _db.SaveChangesAsync();
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

    // ── LLM Presets ───────────────────────────────────────────────────────────

    public async Task<IEnumerable<LlmPreset>> GetPresetsAsync(int? userId) =>
        await _db.LlmPresets
            .Where(p => p.UserId == null || p.UserId == userId)
            .OrderBy(p => p.Name)
            .ToListAsync();

    public async Task<LlmPreset?> GetPresetAsync(int id) =>
        await _db.LlmPresets.FindAsync(id);

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

    public async Task<IEnumerable<StoryBibleSnapshot>> GetStoryBibleSnapshotsAsync(int bookId) =>
        await _db.StoryBibleSnapshots.Where(s => s.BookId == bookId).OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<StoryBibleSnapshot?> GetStoryBibleSnapshotAsync(int bookId, int snapshotId) =>
        await _db.StoryBibleSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId);

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

    public async Task AddBookSnapshotAsync(BookSnapshot snapshot)
    {
        snapshot.CreatedAt = DateTime.UtcNow;
        _db.BookSnapshots.Add(snapshot);
        await _db.SaveChangesAsync();
    }

    public async Task<IEnumerable<BookSnapshot>> GetBookSnapshotsAsync(int bookId) =>
        await _db.BookSnapshots.Where(s => s.BookId == bookId).OrderByDescending(s => s.CreatedAt).ToListAsync();

    public async Task<BookSnapshot?> GetBookSnapshotAsync(int bookId, int snapshotId) =>
        await _db.BookSnapshots.FirstOrDefaultAsync(s => s.BookId == bookId && s.Id == snapshotId);
}
