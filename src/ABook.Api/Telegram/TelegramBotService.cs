using System.Threading.Channels;

namespace ABook.Api.Telegram;

/// <summary>
/// Starts the bot: learns who it is (getMe), registers its commands, then receives updates — by webhook at
/// <c>https://{ABOOK_HOSTNAME}/api/telegram/webhook</c> when <c>ABOOK_HOSTNAME</c> is set, otherwise by long polling.
/// Updates from either source go through one queue and are handled one at a time, in order.
/// </summary>
public sealed class TelegramBotService(
    TelegramOptions options,
    TelegramApi api,
    TelegramBotState state,
    TelegramUpdateHandler handler,
    ILogger<TelegramBotService> logger) : BackgroundService
{
    public static readonly string[] AllowedUpdates =
        ["message", "channel_post", "callback_query", "my_chat_member", "stopped_message_generation"];

    private readonly Channel<TgUpdate> _queue = Channel.CreateBounded<TgUpdate>(
        new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });

    /// <summary>Called by the webhook endpoint.</summary>
    public bool Enqueue(TgUpdate update) => _queue.Writer.TryWrite(update);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled)
        {
            logger.LogInformation("Telegram bot is off ({Key} is not set).", TelegramOptions.TokenKey);
            return;
        }
        try
        {
            await ConnectAsync(stoppingToken);
            var handling = HandleQueueAsync(stoppingToken);
            if (!options.UseWebhook) await PollAsync(stoppingToken);
            await handling;
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>getMe (retried until Telegram answers), commands, and the webhook (set or removed).</summary>
    private async Task ConnectAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (true)
        {
            try
            {
                var me = await api.GetMeAsync(ct);
                await api.SetMyCommandsAsync(TelegramUpdateHandler.Commands, ct);
                if (options.UseWebhook)
                {
                    await api.SetWebhookAsync(options.WebhookUrl!, options.WebhookSecret, AllowedUpdates, ct);
                    logger.LogInformation("Telegram bot @{Username} receives updates by webhook at {Url}.", me.Username, options.WebhookUrl);
                }
                else
                {
                    await api.DeleteWebhookAsync(ct);
                    logger.LogInformation("Telegram bot @{Username} receives updates by long polling (set {Key} to use a webhook).",
                        me.Username, TelegramOptions.HostnameKey);
                }
                // The bot never opens topics itself; with topic mode on, Telegram starts a new topic for every message
                // sent outside one, which makes the private chat (book management) awkward.
                if (me.HasTopicsEnabled == true)
                    logger.LogInformation("Telegram: topic mode in private chats is on — every message sent outside a topic starts a new one. " +
                                          "The bot doesn't need it (books are managed in the private chat; chats follow them through one-time codes); turn it off in @BotFather if that gets in the way.");
                state.SetReady(me);
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                state.Problem = ex is TelegramApiException { Code: 401 or 404 }
                    ? "Telegram rejected the bot token — check TELEGRAM_BOT_TOKEN."
                    : $"Telegram is not reachable: {ex.Message}";
                logger.LogWarning("Telegram bot could not start ({Problem}); retrying in {Seconds}s.", state.Problem, delay.TotalSeconds);
                await Task.Delay(delay, ct);
                delay = TimeSpan.FromSeconds(Math.Min(300, delay.TotalSeconds * 2));
            }
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        long offset = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var updates = await api.GetUpdatesAsync(offset, 50, AllowedUpdates, ct);
                foreach (var update in updates)
                {
                    offset = Math.Max(offset, update.UpdateId + 1);
                    await _queue.Writer.WriteAsync(update, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (TelegramApiException ex) when (ex.Code == 409)
            {
                // Another instance polls with the same token, or a webhook was set elsewhere.
                logger.LogWarning("Telegram: getUpdates conflict — {Description}. Retrying in 30s.", ex.Description);
                await Task.Delay(TimeSpan.FromSeconds(30), ct);
            }
            catch (Exception ex)
            {
                logger.LogWarning("Telegram: polling failed — {Message}. Retrying in 5s.", ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
    }

    private async Task HandleQueueAsync(CancellationToken ct)
    {
        await foreach (var update in _queue.Reader.ReadAllAsync(ct))
            await handler.HandleAsync(update, ct);
    }
}
