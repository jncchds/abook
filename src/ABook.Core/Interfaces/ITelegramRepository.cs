using ABook.Core.Models;

namespace ABook.Core.Interfaces;

/// <summary>Bindings of books to Telegram chats, their one-time bind codes, and the sent-message map.</summary>
public interface ITelegramRepository
{
    // ── Bindings ──────────────────────────────────────────────────────────────
    Task<TelegramBinding?> GetBindingAsync(int bindingId);
    /// <summary>The book's bindings, newest first; revoked ones only when <paramref name="includeRevoked"/>.</summary>
    Task<IReadOnlyList<TelegramBinding>> GetBindingsForBookAsync(int bookId, bool includeRevoked = false);
    /// <summary>Ids of every book with at least one active binding.</summary>
    Task<IReadOnlyList<int>> GetBoundBookIdsAsync();
    /// <summary>The active binding of exactly this chat + topic (null topic = the whole chat / General).</summary>
    Task<TelegramBinding?> FindActiveBindingAsync(long chatId, int? threadId);
    Task<IReadOnlyList<TelegramBinding>> GetActiveBindingsInChatAsync(long chatId);
    Task<TelegramBinding> AddBindingAsync(TelegramBinding binding);
    Task UpdateBindingAsync(TelegramBinding binding);
    /// <summary>A group became a supergroup: its chat id changed.</summary>
    Task<int> MoveChatAsync(long oldChatId, long newChatId);

    // ── Bind codes ────────────────────────────────────────────────────────────
    Task<TelegramBindCode> AddBindCodeAsync(TelegramBindCode code);
    Task<TelegramBindCode?> FindBindCodeAsync(string codeHash);
    /// <summary>Marks the code used; false when it was already used (another chat won the race).</summary>
    Task<bool> TryUseBindCodeAsync(int codeId);
    Task<int> DeleteStaleBindCodesAsync(DateTime olderThan);

    // ── Sent-message map ──────────────────────────────────────────────────────
    /// <summary>The newest link of this kind and item, or null.</summary>
    Task<TelegramMessageLink?> GetLatestLinkAsync(int bindingId, TelegramLinkKind kind, int refId);
    Task<IReadOnlyList<TelegramMessageLink>> GetLinksAsync(int bindingId, TelegramLinkKind kind);
    Task<TelegramMessageLink?> GetLinkByMessageAsync(int bindingId, int messageId);
    /// <summary>The last message the bot posted (or mapped) in the binding at or before <paramref name="at"/>.</summary>
    Task<TelegramMessageLink?> GetLastLinkBeforeAsync(int bindingId, DateTime at);
    Task<TelegramMessageLink> AddLinkAsync(TelegramMessageLink link);
    Task UpdateLinkAsync(TelegramMessageLink link);
}
