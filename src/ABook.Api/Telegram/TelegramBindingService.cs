using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.EntityFrameworkCore;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram;

/// <summary>
/// Making and ending bindings. A binding starts from a one-time code the owner creates in the web UI and hands to
/// the bot in the chat to bind; whoever can post there then uses the book at the binding's level.
/// </summary>
public sealed partial class TelegramBindingService(
    ITelegramRepository tg,
    IBookRepository books,
    IBookNotifier notifier,
    TelegramApi api,
    TelegramBotState bot,
    TelegramDispatcher dispatcher,
    ILogger<TelegramBindingService> logger)
{
    public static readonly TimeSpan CodeLifetime = TimeSpan.FromMinutes(15);

    public sealed record BindCode(string Code, DateTime ExpiresAt);

    public async Task<BindCode> CreateCodeAsync(int bookId, TelegramAccessLevel level, int? userId)
    {
        await tg.DeleteStaleBindCodesAsync(DateTime.UtcNow.AddDays(-1));
        // Start parameters allow A-Z, a-z, 0-9, _ and - (up to 64 characters).
        var code = Convert.ToBase64String(RandomNumberGenerator.GetBytes(18)).Replace('+', '-').Replace('/', '_');
        var expires = DateTime.UtcNow + CodeLifetime;
        await tg.AddBindCodeAsync(new TelegramBindCode
        {
            CodeHash = HashCode(code), BookId = bookId, Level = level, CreatedByUserId = userId, ExpiresAt = expires,
        });
        return new BindCode(code, expires);
    }

    public static string HashCode(string code) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code))).ToLowerInvariant();

    public static bool LooksLikeCode(string? text) => text is not null && CodePattern().IsMatch(text);

    [GeneratedRegex("^[A-Za-z0-9_-]{16,64}$")]
    private static partial Regex CodePattern();

    /// <summary>
    /// Binds the chat (and topic) <paramref name="message"/> was sent in. Returns null on success — the new binding's
    /// worker greets the chat — or the HTML reply explaining why not.
    /// </summary>
    public async Task<string?> BindAsync(TgMessage message, string code)
    {
        var found = LooksLikeCode(code) ? await tg.FindBindCodeAsync(HashCode(code)) : null;
        if (found is null) return "❌ This code isn't valid. The book's owner can make a new one in abook: Book Settings → Telegram.";
        if (found.UsedAt is not null) return "❌ This code was already used. Ask the book's owner for a new one.";
        if (found.ExpiresAt < DateTime.UtcNow) return "❌ This code has expired. Ask the book's owner for a new one.";
        var book = await books.GetByIdAsync(found.BookId);
        if (book is null) return "❌ The book no longer exists.";

        var chat = message.Chat;
        var thread = message.TopicId;
        var title = chat.Display;
        if (chat.Type == "private")
        {
            title = $"Private chat with {message.From?.Display ?? chat.Display}";
            if (thread is null && bot.TopicsInPrivateChats)
            {
                // Each book gets its own topic in the private chat.
                try
                {
                    var topic = await api.CreateForumTopicAsync(chat.Id, Clip(book.Title, 120));
                    thread = topic.MessageThreadId;
                    title += $" · {topic.Name}";
                }
                catch (TelegramApiException ex)
                {
                    logger.LogWarning("Telegram: could not create a topic for book {BookId}: {Description}", book.Id, ex.Description);
                }
            }
        }
        else if (thread is not null)
            title += " · topic";

        if (await tg.FindActiveBindingAsync(chat.Id, thread) is { } existing)
        {
            var other = await books.GetByIdAsync(existing.BookId);
            return $"❌ This {(thread is null ? "chat" : "topic")} is already connected to “{H(other?.Title ?? "another book")}”. " +
                   (chat.Type == "private" && thread is null
                       ? "Send /unbind first — or the bot's admin can turn on topic mode in @BotFather so each book gets its own topic."
                       : "Send /unbind there first, or use another topic.");
        }

        if (!await tg.TryUseBindCodeAsync(found.Id)) return "❌ This code was already used. Ask the book's owner for a new one.";

        TelegramBinding binding;
        try
        {
            binding = await tg.AddBindingAsync(new TelegramBinding
            {
                BookId = book.Id,
                ChatId = chat.Id,
                ThreadId = thread,
                ChatType = chat.Type,
                ChatTitle = Clip(title, 300),
                // Channel posts have no sender to answer or control anything.
                Level = chat.Type == "channel" ? TelegramAccessLevel.Read : found.Level,
                CreatedByUserId = found.CreatedByUserId,
            });
        }
        catch (DbUpdateException)
        {
            return "❌ This chat was connected to a book a moment ago. Send /unbind first.";
        }

        await RecordExistingContentAsync(binding);
        await AddBookNoteAsync(book.Id, $"🔗 Connected to Telegram: {binding.ChatTitle} ({binding.Level}).",
            Origin(message.From, binding));
        dispatcher.Attach(binding);
        logger.LogInformation("Telegram: book {BookId} bound to chat {ChatId} topic {ThreadId} ({Level}).",
            book.Id, chat.Id, thread, binding.Level);
        return null;
    }

    /// <summary>
    /// Opens one of a linked owner's books in their private chat with the bot, without a code: in a new topic when topic
    /// mode is on, otherwise in the chat itself (if it holds no book yet). An active binding there is reused. Returns
    /// the binding, or null with the reason why not.
    /// </summary>
    public async Task<(TelegramBinding? Binding, string? Error)> OpenForOwnerAsync(Book book, TgChat chat, TgUser owner, int ownerUserId)
    {
        if (chat.Type != "private") return (null, "Books are managed in the private chat with the bot.");
        var existing = (await tg.GetActiveBindingsInChatAsync(chat.Id)).FirstOrDefault(b => b.BookId == book.Id);
        if (existing is not null) return (existing, null);

        int? thread = null;
        var title = $"Private chat with {owner.Display}";
        if (bot.TopicsInPrivateChats)
        {
            try
            {
                var topic = await api.CreateForumTopicAsync(chat.Id, Clip(book.Title, 120));
                thread = topic.MessageThreadId;
                title += $" · {topic.Name}";
            }
            catch (TelegramApiException ex)
            {
                return (null, $"Telegram refused to create a topic for the book: {ex.Description}");
            }
        }
        else if (await tg.FindActiveBindingAsync(chat.Id, null) is { } busy && busy.BookId != book.Id)
        {
            var other = await books.GetByIdAsync(busy.BookId);
            return (null, $"This chat already follows “{other?.Title ?? "another book"}”. Turn on topic mode for the bot in @BotFather so each book gets its own topic, or /unbind that one first.");
        }

        TelegramBinding binding;
        try
        {
            binding = await tg.AddBindingAsync(new TelegramBinding
            {
                BookId = book.Id, ChatId = chat.Id, ThreadId = thread, ChatType = chat.Type,
                ChatTitle = Clip(title, 300), Level = TelegramAccessLevel.Control, CreatedByUserId = ownerUserId,
            });
        }
        catch (DbUpdateException)
        {
            return (null, "This chat was connected to a book a moment ago. Try again.");
        }
        await RecordExistingContentAsync(binding);
        await AddBookNoteAsync(book.Id, $"🔗 Opened in Telegram: {binding.ChatTitle}.", $"Telegram · {owner.Display} · private chat");
        dispatcher.Attach(binding);
        return (binding, null);
    }

    /// <summary>
    /// What the book already has when it is bound is recorded as sent (message 0), so only later changes reach the
    /// chat: no flood of old chapters, no replay of the chat history. /export sends the book as it stands.
    /// </summary>
    private async Task RecordExistingContentAsync(TelegramBinding binding)
    {
        async Task Add(TelegramLinkKind kind, int refId, string? hash) =>
            await tg.AddLinkAsync(new TelegramMessageLink { BindingId = binding.Id, Kind = kind, RefId = refId, ContentHash = hash });

        var book = await books.GetByIdAsync(binding.BookId);
        if (book is null) return;
        var chapters = (await books.GetChaptersAsync(binding.BookId)).ToList();
        foreach (var c in chapters.Where(c => c.Status == ChapterStatus.Done && !string.IsNullOrWhiteSpace(c.Content)))
            await Add(TelegramLinkKind.ChapterFile, c.Id, BindingWorker.ChapterHash(c));
        if (BindingWorker.IsPlanningComplete(book) && chapters.Count > 0)
            await Add(TelegramLinkKind.PlanningFile, 0, await BindingWorker.PlanningHashAsync(books, book.Id, chapters));
        if (BindingWorker.IsBookComplete(book, chapters))
            await Add(TelegramLinkKind.BookFile, 0, BindingWorker.BookHash(chapters));
        foreach (var i in (await books.GetIllustrationsAsync(binding.BookId)).Where(i => i.ImageId.HasValue))
            await Add(TelegramLinkKind.Illustration, i.Id, i.ImageId!.Value.ToString());
        foreach (var m in await books.GetChatMessagesAsync(binding.BookId, null, 200))
        {
            await Add(TelegramLinkKind.ChatMessage, m.Id, m.Role == BookChatRole.Author ? m.Status.ToString() : null);
            if (m.Proposals.Count > 0)
                await Add(TelegramLinkKind.ChatProposals, m.Id, ProposalsHash(m.Proposals.OrderBy(p => p.Ordinal).ToList()));
        }
    }

    /// <summary>Ends a binding. <paramref name="tellChat"/> posts a last notice there (best effort).</summary>
    public async Task RevokeAsync(TelegramBinding binding, string reason, bool tellChat, string? origin = null)
    {
        if (!binding.IsActive) return;
        binding.RevokedAt = DateTime.UtcNow;
        binding.RevokedReason = Clip(reason, 300);
        await tg.UpdateBindingAsync(binding);
        dispatcher.Detach(binding.Id);

        var book = await books.GetByIdAsync(binding.BookId);
        if (tellChat)
        {
            try
            {
                await api.SendMessageAsync(binding.ChatId, binding.ThreadId,
                    $"🔌 This {(binding.ThreadId is null ? "chat" : "topic")} is no longer connected to <b>{H(book?.Title ?? "the book")}</b>: {H(reason)}");
            }
            catch (Exception ex) when (ex is TelegramApiException or HttpRequestException or TaskCanceledException)
            {
                logger.LogInformation("Telegram: could not post the unbind notice to {ChatId}: {Message}", binding.ChatId, ex.Message);
            }
        }
        if (book is not null)
            await AddBookNoteAsync(book.Id, $"🔌 Disconnected from Telegram: {binding.ChatTitle} — {reason}", origin);
    }

    /// <summary>The book is about to be deleted: tell its chats (the bindings go with the book).</summary>
    public async Task NotifyBookDeletedAsync(int bookId)
    {
        foreach (var binding in await tg.GetBindingsForBookAsync(bookId))
        {
            dispatcher.Detach(binding.Id);
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await api.SendMessageAsync(binding.ChatId, binding.ThreadId,
                    "🗑 The book connected here was deleted in abook. This chat is no longer connected.", ct: cts.Token);
            }
            catch (Exception ex) when (ex is TelegramApiException or HttpRequestException or TaskCanceledException) { }
        }
    }

    private async Task AddBookNoteAsync(int bookId, string text, string? origin)
    {
        await books.AddMessageAsync(new AgentMessage
        {
            BookId = bookId, AgentRole = AgentRole.Planner, MessageType = MessageType.SystemNote,
            Content = text, IsResolved = true, Origin = origin,
        });
        await notifier.NotifyMessagesUpdatedAsync(bookId);
    }
}
