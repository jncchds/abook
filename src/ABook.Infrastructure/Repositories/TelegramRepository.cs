using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ABook.Infrastructure.Repositories;

public class TelegramRepository(AppDbContext db) : ITelegramRepository
{
    public async Task<TelegramBinding?> GetBindingAsync(int bindingId) =>
        await db.TelegramBindings.FirstOrDefaultAsync(b => b.Id == bindingId);

    public async Task<IReadOnlyList<TelegramBinding>> GetBindingsForBookAsync(int bookId, bool includeRevoked = false) =>
        await db.TelegramBindings
            .Where(b => b.BookId == bookId && (includeRevoked || b.RevokedAt == null))
            .OrderByDescending(b => b.Id)
            .ToListAsync();

    public async Task<IReadOnlyList<int>> GetBoundBookIdsAsync() =>
        await db.TelegramBindings.Where(b => b.RevokedAt == null).Select(b => b.BookId).Distinct().ToListAsync();

    public async Task<TelegramBinding?> FindActiveBindingAsync(long chatId, int? threadId) =>
        await db.TelegramBindings.FirstOrDefaultAsync(b =>
            b.ChatId == chatId && b.ThreadId == threadId && b.RevokedAt == null);

    public async Task<IReadOnlyList<TelegramBinding>> GetActiveBindingsInChatAsync(long chatId) =>
        await db.TelegramBindings.Where(b => b.ChatId == chatId && b.RevokedAt == null).ToListAsync();

    public async Task<TelegramBinding> AddBindingAsync(TelegramBinding binding)
    {
        binding.CreatedAt = DateTime.UtcNow;
        db.TelegramBindings.Add(binding);
        await db.SaveChangesAsync();
        return binding;
    }

    public async Task UpdateBindingAsync(TelegramBinding binding)
    {
        db.TelegramBindings.Update(binding);
        await db.SaveChangesAsync();
    }

    public async Task<int> MoveChatAsync(long oldChatId, long newChatId) =>
        await db.TelegramBindings
            .Where(b => b.ChatId == oldChatId && b.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.ChatId, newChatId)
                .SetProperty(b => b.ChatType, "supergroup")
                // Message ids restart in the new chat: the old status card cannot be edited any more.
                .SetProperty(b => b.StatusMessageId, (int?)null));

    public async Task<TelegramBindCode> AddBindCodeAsync(TelegramBindCode code)
    {
        code.CreatedAt = DateTime.UtcNow;
        db.TelegramBindCodes.Add(code);
        await db.SaveChangesAsync();
        return code;
    }

    public async Task<TelegramBindCode?> FindBindCodeAsync(string codeHash) =>
        await db.TelegramBindCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == codeHash);

    public async Task<bool> TryUseBindCodeAsync(int codeId)
    {
        var now = DateTime.UtcNow;
        return await db.TelegramBindCodes
            .Where(c => c.Id == codeId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now)) == 1;
    }

    public async Task<int> DeleteStaleBindCodesAsync(DateTime olderThan) =>
        await db.TelegramBindCodes.Where(c => c.ExpiresAt < olderThan).ExecuteDeleteAsync();

    public async Task<TelegramCard> AddCardAsync(TelegramCard card)
    {
        card.CreatedAt = card.UpdatedAt = DateTime.UtcNow;
        db.TelegramCards.Add(card);
        await db.SaveChangesAsync();
        return card;
    }

    public async Task<TelegramCard?> GetCardAsync(int cardId) =>
        await db.TelegramCards.FirstOrDefaultAsync(c => c.Id == cardId);

    public async Task<TelegramCard?> FindCardAsync(long chatId, int messageId) =>
        await db.TelegramCards.FirstOrDefaultAsync(c => c.ChatId == chatId && c.MessageId == messageId);

    public async Task UpdateCardAsync(TelegramCard card)
    {
        card.UpdatedAt = DateTime.UtcNow;
        db.TelegramCards.Update(card);
        await db.SaveChangesAsync();
    }

    public async Task<TelegramAccountCode> AddAccountCodeAsync(TelegramAccountCode code)
    {
        code.CreatedAt = DateTime.UtcNow;
        // Old codes are no use to anyone.
        await db.TelegramAccountCodes.Where(c => c.ExpiresAt < code.CreatedAt.AddDays(-1)).ExecuteDeleteAsync();
        db.TelegramAccountCodes.Add(code);
        await db.SaveChangesAsync();
        return code;
    }

    public async Task<TelegramAccountCode?> FindAccountCodeAsync(string codeHash) =>
        await db.TelegramAccountCodes.AsNoTracking().FirstOrDefaultAsync(c => c.CodeHash == codeHash);

    public async Task<bool> TryUseAccountCodeAsync(int codeId)
    {
        var now = DateTime.UtcNow;
        return await db.TelegramAccountCodes
            .Where(c => c.Id == codeId && c.UsedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.UsedAt, now)) == 1;
    }

    public async Task<TelegramMessageLink?> GetLatestLinkAsync(int bindingId, TelegramLinkKind kind, int refId) =>
        await db.TelegramMessageLinks
            .Where(l => l.BindingId == bindingId && l.Kind == kind && l.RefId == refId)
            .OrderByDescending(l => l.Id)
            .FirstOrDefaultAsync();

    public async Task<IReadOnlyList<TelegramMessageLink>> GetLinksAsync(int bindingId, TelegramLinkKind kind) =>
        await db.TelegramMessageLinks
            .Where(l => l.BindingId == bindingId && l.Kind == kind)
            .OrderBy(l => l.Id)
            .ToListAsync();

    public async Task<TelegramMessageLink?> GetLinkByMessageAsync(int bindingId, int messageId) =>
        messageId == 0
            ? null
            : await db.TelegramMessageLinks
                .Where(l => l.BindingId == bindingId && l.MessageId == messageId)
                .OrderByDescending(l => l.Id)
                .FirstOrDefaultAsync();

    public async Task<TelegramMessageLink?> GetLastLinkBeforeAsync(int bindingId, DateTime at) =>
        await db.TelegramMessageLinks
            .Where(l => l.BindingId == bindingId && l.MessageId > 0 && l.CreatedAt <= at)
            .OrderByDescending(l => l.CreatedAt)
            .FirstOrDefaultAsync();

    public async Task<TelegramMessageLink> AddLinkAsync(TelegramMessageLink link)
    {
        link.CreatedAt = DateTime.UtcNow;
        db.TelegramMessageLinks.Add(link);
        await db.SaveChangesAsync();
        return link;
    }

    public async Task UpdateLinkAsync(TelegramMessageLink link)
    {
        db.TelegramMessageLinks.Update(link);
        await db.SaveChangesAsync();
    }
}
