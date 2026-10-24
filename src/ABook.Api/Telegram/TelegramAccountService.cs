using System.Collections.Concurrent;
using System.Security.Cryptography;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram;

/// <summary>
/// Linking a Telegram account to an abook user, one each way. The user makes a one-time code in Settings and opens
/// <c>t.me/&lt;bot&gt;?start=acc_CODE</c>; the bot links whoever sends it, in a private chat. A linked account manages
/// its own books in the private chat with the bot, and has full rights on them in any connected chat.
/// </summary>
public sealed class TelegramAccountService(
    ITelegramRepository tg,
    IUserRepository users,
    TelegramBotState bot,
    ILogger<TelegramAccountService> logger)
{
    public const string StartPrefix = "acc_";
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    // Telegram user → abook user, for the access check on every update (short-lived: unlinking takes effect quickly).
    private static readonly ConcurrentDictionary<long, (int? UserId, DateTime Until)> LinkedCache = new();

    public sealed record AccountCode(string Code, string StartParameter, string? Link, DateTime ExpiresAt);

    public async Task<AccountCode> CreateCodeAsync(int userId)
    {
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var expires = DateTime.UtcNow + CodeLifetime;
        await tg.AddAccountCodeAsync(new TelegramAccountCode
        {
            CodeHash = TelegramBindingService.HashCode(code), UserId = userId, ExpiresAt = expires,
        });
        var start = StartPrefix + code;
        return new AccountCode(code, start, bot.Username is null ? null : $"https://t.me/{bot.Username}?start={start}", expires);
    }

    /// <summary>Links the sender to the code's user. Returns the reply (HTML) either way.</summary>
    public async Task<string> LinkAsync(TgMessage message, string code)
    {
        if (message.Chat.Type != "private" || message.From is null)
            return "🔒 Link your account in a private chat with the bot, not here.";
        var found = TelegramBindingService.LooksLikeCode(code) ? await tg.FindAccountCodeAsync(TelegramBindingService.HashCode(code)) : null;
        if (found is null) return "❌ This link isn't valid. Make a new one in abook: Settings → Telegram.";
        if (found.UsedAt is not null) return "❌ This link was already used. Make a new one in abook: Settings → Telegram.";
        if (found.ExpiresAt < DateTime.UtcNow) return "❌ This link has expired. Make a new one in abook: Settings → Telegram.";

        var user = await users.GetByIdAsync(found.UserId);
        if (user is null) return "❌ That abook account no longer exists.";
        var from = message.From;
        if (await users.GetByTelegramIdAsync(from.Id) is { } other && other.Id != user.Id)
            return $"❌ This Telegram account is already linked to the abook account <b>{H(other.Username)}</b>. Send /unlink first.";
        if (!await tg.TryUseAccountCodeAsync(found.Id)) return "❌ This link was already used.";

        // A user links one Telegram account: a new link replaces the old one.
        user.TelegramUserId = from.Id;
        user.TelegramUsername = Clip(from.Display, 100);
        user.TelegramLinkedAt = DateTime.UtcNow;
        await users.UpdateAsync(user);
        LinkedCache.TryRemove(from.Id, out _);
        logger.LogInformation("Telegram: abook user {UserId} linked Telegram account {TelegramId}.", user.Id, from.Id);
        return $"✅ Linked to the abook account <b>{H(user.Username)}</b>.\n\n" +
               "Here you can manage your books: /newbook to start one, /books to open one, /book in a book's topic for its settings and content. " +
               "In any chat connected to one of your books you now have full rights. /unlink to undo.";
    }

    public async Task<bool> UnlinkAsync(AppUser user)
    {
        if (user.TelegramUserId is not long telegramId) return false;
        user.TelegramUserId = null;
        user.TelegramUsername = null;
        user.TelegramLinkedAt = null;
        await users.UpdateAsync(user);
        LinkedCache.TryRemove(telegramId, out _);
        return true;
    }

    /// <summary>The abook user a Telegram account is linked to (cached for a minute).</summary>
    public async Task<AppUser?> LinkedUserAsync(long telegramUserId)
    {
        if (LinkedCache.TryGetValue(telegramUserId, out var cached) && cached.Until > DateTime.UtcNow)
            return cached.UserId is int id ? await users.GetByIdAsync(id) : null;
        var user = await users.GetByTelegramIdAsync(telegramUserId);
        LinkedCache[telegramUserId] = (user?.Id, DateTime.UtcNow.AddMinutes(1));
        return user;
    }
}
