using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABook.Api.Telegram;

/// <summary>
/// A thin Bot API client: JSON calls, multipart file uploads, and a retry on 429 (flood control) that waits
/// the <c>retry_after</c> Telegram asks for. Methods the bot needs are wrapped below; anything else goes through
/// <see cref="CallAsync{T}"/>. Payloads are anonymous objects with Bot API (snake_case) names; nulls are left out.
/// </summary>
public sealed class TelegramApi
{
    public const string HttpClientName = "telegram";
    private const int MaxRetries = 3;
    private static readonly TimeSpan MaxRetryWait = TimeSpan.FromSeconds(60);

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    private readonly IHttpClientFactory _httpFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramApi> _logger;

    public TelegramApi(IHttpClientFactory httpFactory, TelegramOptions options, ILogger<TelegramApi> logger)
    {
        _httpFactory = httpFactory;
        _options = options;
        _logger = logger;
    }

    private string MethodUrl(string method) => $"{_options.ApiUrl}/bot{_options.BotToken}/{method}";

    /// <summary>Calls a Bot API method with a JSON body and returns its result.</summary>
    public Task<T> CallAsync<T>(string method, object? payload, CancellationToken ct = default, TimeSpan? timeout = null) =>
        SendWithRetryAsync<T>(method, () =>
        {
            var body = JsonSerializer.Serialize(payload ?? new { }, Json);
            return new StringContent(body, Encoding.UTF8, "application/json");
        }, ct, timeout);

    /// <summary>Calls a Bot API method with a multipart body: text fields plus one file.</summary>
    public Task<T> CallMultipartAsync<T>(
        string method, IReadOnlyDictionary<string, object?> fields, string fileField, string fileName, byte[] data, string contentType,
        CancellationToken ct = default) =>
        SendWithRetryAsync<T>(method, () =>
        {
            var form = new MultipartFormDataContent();
            foreach (var (name, value) in fields)
            {
                if (value is null) continue;
                var text = value switch
                {
                    string s => s,
                    bool b => b ? "true" : "false",
                    int or long => Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture)!,
                    _ => JsonSerializer.Serialize(value, Json),
                };
                form.Add(new StringContent(text, Encoding.UTF8), name);
            }
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType.Split(';')[0]);
            form.Add(file, fileField, fileName);
            return form;
        }, ct, TimeSpan.FromMinutes(5));

    private async Task<T> SendWithRetryAsync<T>(string method, Func<HttpContent> content, CancellationToken ct, TimeSpan? timeout)
    {
        for (var attempt = 0; ; attempt++)
        {
            var http = _httpFactory.CreateClient(HttpClientName);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout ?? TimeSpan.FromSeconds(60));
            using var response = await http.PostAsync(MethodUrl(method), content(), cts.Token);
            var json = await response.Content.ReadAsStringAsync(cts.Token);

            TgResponse? parsed;
            try { parsed = JsonSerializer.Deserialize<TgResponse>(json, Json); }
            catch (JsonException) { parsed = null; }
            if (parsed is null)
                throw new TelegramApiException((int)response.StatusCode, $"Unreadable response from {method}.");

            if (parsed.Ok)
                return parsed.Result.Deserialize<T>(Json)!;

            var retryAfter = parsed.Parameters?.RetryAfter;
            if (parsed.ErrorCode == 429 && attempt < MaxRetries)
            {
                var wait = TimeSpan.FromSeconds(Math.Max(1, retryAfter ?? 3));
                if (wait > MaxRetryWait) wait = MaxRetryWait;
                _logger.LogWarning("Telegram flood control on {Method}: waiting {Seconds}s.", method, wait.TotalSeconds);
                await Task.Delay(wait, ct);
                continue;
            }
            throw new TelegramApiException(parsed.ErrorCode, parsed.Description ?? "Unknown error",
                retryAfter, parsed.Parameters?.MigrateToChatId);
        }
    }

    // ── Bot ───────────────────────────────────────────────────────────────────

    public Task<TgUser> GetMeAsync(CancellationToken ct = default) => CallAsync<TgUser>("getMe", null, ct);

    public Task<bool> SetWebhookAsync(string url, string secret, IReadOnlyList<string> allowedUpdates, CancellationToken ct = default) =>
        CallAsync<bool>("setWebhook", new { url, secret_token = secret, allowed_updates = allowedUpdates }, ct);

    public Task<bool> DeleteWebhookAsync(CancellationToken ct = default) =>
        CallAsync<bool>("deleteWebhook", new { drop_pending_updates = false }, ct);

    public Task<List<TgUpdate>> GetUpdatesAsync(long offset, int timeoutSeconds, IReadOnlyList<string> allowedUpdates, CancellationToken ct) =>
        CallAsync<List<TgUpdate>>("getUpdates",
            new { offset, timeout = timeoutSeconds, allowed_updates = allowedUpdates }, ct,
            TimeSpan.FromSeconds(timeoutSeconds + 15));

    public Task<bool> SetMyCommandsAsync(IEnumerable<(string Command, string Description)> commands, CancellationToken ct = default) =>
        CallAsync<bool>("setMyCommands",
            new { commands = commands.Select(c => new { command = c.Command, description = c.Description }).ToList() }, ct);

    // ── Messages ──────────────────────────────────────────────────────────────

    private static object? Keyboard(IReadOnlyList<IReadOnlyList<TgButton>>? rows) =>
        rows is null ? null : new { inline_keyboard = rows };

    private static object? ReplyTo(int? messageId) =>
        messageId is int id and > 0 ? new { message_id = id, allow_sending_without_reply = true } : null;

    /// <summary>Sends an HTML message. <paramref name="threadId"/> is the forum topic (null = none).</summary>
    public Task<TgMessage> SendMessageAsync(
        long chatId, int? threadId, string html, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null,
        int? replyTo = null, bool silent = false, CancellationToken ct = default) =>
        CallAsync<TgMessage>("sendMessage", new
        {
            chat_id = chatId,
            message_thread_id = threadId,
            text = html,
            parse_mode = "HTML",
            link_preview_options = new { is_disabled = true },
            reply_markup = Keyboard(keyboard),
            reply_parameters = ReplyTo(replyTo),
            disable_notification = silent ? true : (bool?)null,
        }, ct);

    /// <summary>
    /// Sends a rich message (Bot API 10.1) written in rich HTML — which, unlike plain messages, can hold buttons inside
    /// the text (<c>&lt;tg-button type="callback_data" style="link"&gt;</c>).
    /// </summary>
    public Task<TgMessage> SendRichMessageAsync(
        long chatId, int? threadId, string richHtml, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null,
        int? replyTo = null, bool silent = false, CancellationToken ct = default) =>
        CallAsync<TgMessage>("sendRichMessage", new
        {
            chat_id = chatId,
            message_thread_id = threadId,
            rich_message = new { html = richHtml },
            reply_markup = Keyboard(keyboard),
            reply_parameters = ReplyTo(replyTo),
            disable_notification = silent ? true : (bool?)null,
        }, ct);

    /// <summary>Replaces a rich message's content and keyboard.</summary>
    public Task<JsonElement> EditRichMessageAsync(
        long chatId, int messageId, string richHtml, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null, CancellationToken ct = default) =>
        CallAsync<JsonElement>("editMessageText", new
        {
            chat_id = chatId,
            message_id = messageId,
            rich_message = new { html = richHtml },
            reply_markup = Keyboard(keyboard ?? []),
        }, ct);

    /// <summary>Replaces a message's text and keyboard (an empty keyboard removes it).</summary>
    public Task<JsonElement> EditMessageTextAsync(
        long chatId, int messageId, string html, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null, CancellationToken ct = default) =>
        CallAsync<JsonElement>("editMessageText", new
        {
            chat_id = chatId,
            message_id = messageId,
            text = html,
            parse_mode = "HTML",
            link_preview_options = new { is_disabled = true },
            reply_markup = Keyboard(keyboard ?? []),
        }, ct);

    public Task<JsonElement> EditMessageKeyboardAsync(
        long chatId, int messageId, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard, CancellationToken ct = default) =>
        CallAsync<JsonElement>("editMessageReplyMarkup", new
        {
            chat_id = chatId,
            message_id = messageId,
            reply_markup = Keyboard(keyboard ?? []),
        }, ct);

    /// <summary>Sends a file with an HTML caption (at most 1024 characters).</summary>
    public Task<TgMessage> SendDocumentAsync(
        long chatId, int? threadId, string fileName, byte[] data, string contentType, string captionHtml,
        int? replyTo = null, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null, CancellationToken ct = default) =>
        CallMultipartAsync<TgMessage>("sendDocument", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_thread_id"] = threadId,
            ["caption"] = captionHtml,
            ["parse_mode"] = "HTML",
            ["reply_parameters"] = ReplyTo(replyTo),
            ["reply_markup"] = Keyboard(keyboard),
        }, "document", fileName, data, contentType, ct);

    public Task<TgMessage> SendPhotoAsync(
        long chatId, int? threadId, string fileName, byte[] data, string contentType, string captionHtml,
        CancellationToken ct = default) =>
        CallMultipartAsync<TgMessage>("sendPhoto", new Dictionary<string, object?>
        {
            ["chat_id"] = chatId,
            ["message_thread_id"] = threadId,
            ["caption"] = captionHtml,
            ["parse_mode"] = "HTML",
        }, "photo", fileName, data, contentType, ct);

    /// <summary>
    /// Streams a temporary preview in a private chat (it disappears after ~30 s unless sent again).
    /// With <paramref name="canStop"/> the user gets a stop button, reported as <c>stopped_message_generation</c>.
    /// </summary>
    public Task<bool> SendMessageDraftAsync(
        long chatId, int? threadId, long draftId, string text, bool canStop, CancellationToken ct = default) =>
        CallAsync<bool>("sendMessageDraft", new
        {
            chat_id = chatId,
            message_thread_id = threadId,
            draft_id = draftId,
            text,
            can_stop = canStop ? true : (bool?)null,
        }, ct);

    public Task<bool> PinChatMessageAsync(long chatId, int messageId, CancellationToken ct = default) =>
        CallAsync<bool>("pinChatMessage", new { chat_id = chatId, message_id = messageId, disable_notification = true }, ct);

    public Task<bool> AnswerCallbackQueryAsync(string id, string? text = null, bool showAlert = false, CancellationToken ct = default) =>
        CallAsync<bool>("answerCallbackQuery", new { callback_query_id = id, text, show_alert = showAlert ? true : (bool?)null }, ct);

    /// <summary>A quick emoji reaction to acknowledge a message (e.g. 👀 while the book chat replies).</summary>
    public Task<bool> SetReactionAsync(long chatId, int messageId, string emoji, CancellationToken ct = default) =>
        CallAsync<bool>("setMessageReaction", new
        {
            chat_id = chatId,
            message_id = messageId,
            reaction = new[] { new { type = "emoji", emoji } },
        }, ct);

    // ── Chats ─────────────────────────────────────────────────────────────────

    public Task<TgForumTopic> CreateForumTopicAsync(long chatId, string name, CancellationToken ct = default) =>
        CallAsync<TgForumTopic>("createForumTopic", new { chat_id = chatId, name }, ct);

    public Task<TgChatMember> GetChatMemberAsync(long chatId, long userId, CancellationToken ct = default) =>
        CallAsync<TgChatMember>("getChatMember", new { chat_id = chatId, user_id = userId }, ct);
}
