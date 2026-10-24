using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABook.Api.Telegram;

// The parts of the Bot API objects the bot reads. Deserialized with snake_case names (TelegramApi.Json),
// so property names map directly: MessageThreadId ↔ message_thread_id.

public sealed class TgUpdate
{
    public long UpdateId { get; set; }
    public TgMessage? Message { get; set; }
    public TgMessage? ChannelPost { get; set; }
    public TgCallbackQuery? CallbackQuery { get; set; }
    public TgChatMemberUpdated? MyChatMember { get; set; }
    public TgStoppedGeneration? StoppedMessageGeneration { get; set; }
}

public sealed class TgMessage
{
    public int MessageId { get; set; }
    public int? MessageThreadId { get; set; }
    public bool? IsTopicMessage { get; set; }
    public TgUser? From { get; set; }
    public TgChat? SenderChat { get; set; }
    public TgChat Chat { get; set; } = new();
    /// <summary>0 for an InaccessibleMessage (a callback on a message older than 48 hours).</summary>
    public long Date { get; set; }
    public string? Text { get; set; }
    public string? Caption { get; set; }
    public TgMessage? ReplyToMessage { get; set; }
    public long? MigrateToChatId { get; set; }
    public TgForumTopic? ForumTopicCreated { get; set; }

    /// <summary>The topic this message is in: forum topics only (a plain reply thread is not a topic).</summary>
    [JsonIgnore] public int? TopicId => IsTopicMessage == true ? MessageThreadId : null;
}

public sealed class TgUser
{
    public long Id { get; set; }
    public bool IsBot { get; set; }
    public string FirstName { get; set; } = string.Empty;
    public string? LastName { get; set; }
    public string? Username { get; set; }
    /// <summary>getMe only: the bot has forum topic mode in private chats (a BotFather setting).</summary>
    public bool? HasTopicsEnabled { get; set; }

    [JsonIgnore] public string Display => Username is { Length: > 0 } u ? "@" + u : $"{FirstName} {LastName}".Trim();
}

public sealed class TgChat
{
    public long Id { get; set; }
    public string Type { get; set; } = string.Empty;
    public string? Title { get; set; }
    public string? Username { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public bool? IsForum { get; set; }

    [JsonIgnore] public string Display => Title ?? $"{FirstName} {LastName}".Trim();
}

public sealed class TgForumTopic
{
    public int MessageThreadId { get; set; }
    public string Name { get; set; } = string.Empty;
}

public sealed class TgCallbackQuery
{
    public string Id { get; set; } = string.Empty;
    public TgUser From { get; set; } = new();
    public TgMessage? Message { get; set; }
    public string? Data { get; set; }
}

public sealed class TgChatMemberUpdated
{
    public TgChat Chat { get; set; } = new();
    public TgUser From { get; set; } = new();
    public TgChatMember NewChatMember { get; set; } = new();
}

public sealed class TgChatMember
{
    public string Status { get; set; } = string.Empty;
    public TgUser User { get; set; } = new();
}

public sealed class TgStoppedGeneration
{
    public TgChat Chat { get; set; } = new();
    public int? MessageThreadId { get; set; }
    public long DraftId { get; set; }
}

/// <summary>One inline keyboard button; exactly one of <see cref="CallbackData"/> / <see cref="Url"/>.</summary>
public sealed record TgButton(string Text, string? CallbackData = null, string? Url = null, string? Style = null);

/// <summary>A failed Bot API call: Telegram's error code and description.</summary>
public sealed class TelegramApiException(int code, string description, int? retryAfter = null, long? migrateToChatId = null)
    : Exception($"Telegram API error {code}: {description}")
{
    public int Code { get; } = code;
    public string Description { get; } = description;
    public int? RetryAfter { get; } = retryAfter;
    public long? MigrateToChatId { get; } = migrateToChatId;

    /// <summary>The chat or topic is gone for good, or the bot cannot post there any more: the binding ends.</summary>
    public bool ChatGone =>
        Code == 403
        || Description.Contains("chat not found", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("thread not found", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("TOPIC_DELETED", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("TOPIC_CLOSED", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("have no rights to send", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("CHAT_WRITE_FORBIDDEN", StringComparison.OrdinalIgnoreCase);

    public bool NotModified => Description.Contains("message is not modified", StringComparison.OrdinalIgnoreCase);

    public bool MessageMissing =>
        Description.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase)
        || Description.Contains("MESSAGE_ID_INVALID", StringComparison.OrdinalIgnoreCase);
}

internal sealed class TgResponse
{
    public bool Ok { get; set; }
    public JsonElement Result { get; set; }
    public int ErrorCode { get; set; }
    public string? Description { get; set; }
    public TgResponseParameters? Parameters { get; set; }
}

internal sealed class TgResponseParameters
{
    public int? RetryAfter { get; set; }
    public long? MigrateToChatId { get; set; }
}
