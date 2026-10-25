namespace ABook.Api.Telegram;

/// <summary>What the bot learned about itself at startup (getMe), shared by the parts that need it.</summary>
public sealed class TelegramBotState
{
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public string? Username { get; private set; }
    public long BotId { get; private set; }
    /// <summary>Topic mode in private chats is on (BotFather). Only reported: the bot never opens topics itself.</summary>
    public bool TopicsInPrivateChats { get; private set; }
    public bool IsReady => _ready.Task.IsCompleted;
    /// <summary>The last startup problem (bad token, Telegram unreachable…), shown in the web UI.</summary>
    public string? Problem { get; set; }

    public Task WaitReadyAsync(CancellationToken ct) => _ready.Task.WaitAsync(ct);

    public void SetReady(TgUser me)
    {
        Username = me.Username;
        BotId = me.Id;
        TopicsInPrivateChats = me.HasTopicsEnabled == true;
        Problem = null;
        _ready.TrySetResult();
    }

    public string? PrivateLink(string code) => Username is null ? null : $"https://t.me/{Username}?start={code}";
    public string? GroupLink(string code) => Username is null ? null : $"https://t.me/{Username}?startgroup={code}";
}
