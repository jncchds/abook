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

    public async Task<IEnumerable<Chapter>> GetChaptersAsync(int bookId) =>
        await _db.Chapters.Where(c => c.BookId == bookId).OrderBy(c => c.Number).ToListAsync();

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

    public async Task<IEnumerable<TokenUsageRecord>> GetTokenUsageAsync(int bookId) =>
        await _db.TokenUsageRecords
            .Where(r => r.BookId == bookId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();

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
}
