using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>What everyone who can post in a bound Telegram chat or topic may do with the book.</summary>
public enum TelegramAccessLevel
{
    /// <summary>Gets the status card, notices and files.</summary>
    Read,
    /// <summary>Read, plus answering the agents' questions, /status, /export, /note and the book chat.</summary>
    Participate,
    /// <summary>Participate, plus starting, continuing and stopping runs and applying book-chat changes.</summary>
    Control,
}

/// <summary>What a <see cref="TelegramMessageLink"/> points at.</summary>
public enum TelegramLinkKind
{
    /// <summary>An agent question (<see cref="AgentMessage"/> of type Question); RefId = message id.</summary>
    Question,
    /// <summary>A planner clarification asked in the chat; RefId = clarification id.</summary>
    Clarification,
    /// <summary>A chapter file; RefId = chapter id. One link per version sent.</summary>
    ChapterFile,
    /// <summary>The planning (metadata) export; RefId = 0.</summary>
    PlanningFile,
    /// <summary>The whole-book export sent when the book was complete; RefId = 0.</summary>
    BookFile,
    /// <summary>A rendered illustration sent as a photo; RefId = illustration id.</summary>
    Illustration,
    /// <summary>A book-chat message mirrored to the thread; RefId = chat message id.</summary>
    ChatMessage,
    /// <summary>The suggested changes of a book-chat reply, with Apply/Reject buttons; RefId = chat message id.</summary>
    ChatProposals,
}

/// <summary>
/// A Telegram chat — or one topic of it — tied to a book. Whoever can post there gets <see cref="Level"/>
/// on the book; there are no abook accounts on the Telegram side. Created only from a one-time
/// <see cref="TelegramBindCode"/> the book's owner made in the web UI.
/// </summary>
public class TelegramBinding
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public long ChatId { get; set; }
    /// <summary>The forum topic (in a group or a private chat with topics); null for the whole chat or a forum's General topic.</summary>
    public int? ThreadId { get; set; }
    /// <summary>Telegram's chat type: <c>private</c>, <c>group</c>, <c>supergroup</c> or <c>channel</c>.</summary>
    public string ChatType { get; set; } = string.Empty;
    /// <summary>The chat (and topic) name, for the web UI.</summary>
    public string ChatTitle { get; set; } = string.Empty;
    public TelegramAccessLevel Level { get; set; } = TelegramAccessLevel.Participate;
    /// <summary>Control actions need Telegram admin rights in the chat (checked with getChatMember).</summary>
    public bool ControlAdminsOnly { get; set; }
    /// <summary>Format of chapter and book files: <c>epub</c>, <c>fb2</c> or <c>html</c>.</summary>
    public string FileFormat { get; set; } = "epub";
    /// <summary>Private chats only: show the text being written as a live draft.</summary>
    public bool LiveDrafts { get; set; } = true;
    /// <summary>The pinned status card, edited in place.</summary>
    public int? StatusMessageId { get; set; }
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    /// <summary>Set when the binding ends: revoked by the owner, /unbind, bot removed, topic gone.</summary>
    public DateTime? RevokedAt { get; set; }
    /// <summary>Why the binding ended.</summary>
    public string? RevokedReason { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
    [JsonIgnore] public bool IsActive => RevokedAt is null;
    [JsonIgnore] public bool IsPrivate => ChatType == "private";
    [JsonIgnore] public bool IsChannel => ChatType == "channel";
}

/// <summary>
/// A one-time code the owner hands to the bot (<c>/start CODE</c>, <c>?startgroup=CODE</c> or <c>/bind CODE</c>)
/// to bind the chat it is used in. Only its hash is stored.
/// </summary>
public class TelegramBindCode
{
    public int Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public int BookId { get; set; }
    public TelegramAccessLevel Level { get; set; } = TelegramAccessLevel.Participate;
    public int? CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
}

/// <summary>
/// A card: one bot message that is a small editor or wizard (new book, book settings, Story Bible, a character…).
/// The bot edits it in place as the state changes; replying to it fills the field it asks for. The state is kept
/// here (by chat and message id) so a reply always finds its card, also after a restart.
/// </summary>
public class TelegramCard
{
    public int Id { get; set; }
    public long ChatId { get; set; }
    public int? ThreadId { get; set; }
    public int MessageId { get; set; }
    /// <summary>The book the card works on; null for cards before there is one (new book, the list of books).</summary>
    public int? BookId { get; set; }
    /// <summary>Who opened it — for cards of a linked account (new book, books), only they may use it.</summary>
    public long OpenedByTelegramUserId { get; set; }
    /// <summary>The card's state as JSON (see the bot's CardState).</summary>
    public string StateJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A one-time code linking the Telegram account that uses it (<c>/start acc_CODE</c> in a private chat) to the abook user
/// who made it. Only its hash is stored.
/// </summary>
public class TelegramAccountCode
{
    public int Id { get; set; }
    public string CodeHash { get; set; } = string.Empty;
    public int UserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public DateTime? UsedAt { get; set; }

    [JsonIgnore] public AppUser User { get; set; } = null!;
}

/// <summary>
/// Pairs something of the book with the Telegram message that shows it in a bound thread — to match replies
/// to questions, to edit keyboards, to reply to a file's previous version, and to never send a thing twice.
/// </summary>
public class TelegramMessageLink
{
    public long Id { get; set; }
    public int BindingId { get; set; }
    public TelegramLinkKind Kind { get; set; }
    public int RefId { get; set; }
    /// <summary>The Telegram message id; 0 for a baseline taken without sending (content that existed at bind time).</summary>
    public int MessageId { get; set; }
    /// <summary>Hash of what was sent (chapter text, proposal states…), to tell whether it changed since.</summary>
    public string? ContentHash { get; set; }
    /// <summary>Chapter files: 1 for the first file sent, then 2, 3…</summary>
    public int Version { get; set; }
    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public TelegramBinding Binding { get; set; } = null!;
}
