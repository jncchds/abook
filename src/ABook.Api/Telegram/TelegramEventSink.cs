using System.Collections.Concurrent;
using System.Threading.Channels;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Telegram;

/// <summary>A book event as the bot sees it.</summary>
public abstract record TgBookEvent(int BookId);
public sealed record TgStatusEvent(int BookId, AgentRole Role, string State, int? ChapterId) : TgBookEvent(BookId);
public sealed record TgProgressEvent(int BookId, string Step, bool IsComplete) : TgBookEvent(BookId);
public sealed record TgQuestionEvent(int BookId, int MessageId) : TgBookEvent(BookId);
public sealed record TgChapterEvent(int BookId, int ChapterId) : TgBookEvent(BookId);
public sealed record TgTokensEvent(int BookId, int PromptTokens, int CompletionTokens) : TgBookEvent(BookId);
public sealed record TgErrorEvent(int BookId, string Role, string Message) : TgBookEvent(BookId);
public sealed record TgIllustrationEvent(int BookId, int? IllustrationId) : TgBookEvent(BookId);
public sealed record TgChatEvent(int BookId, int? MessageId) : TgBookEvent(BookId);
public sealed record TgRewoundEvent(int BookId, DateTime At, IReadOnlyList<int> HiddenChatMessageIds, string? Origin) : TgBookEvent(BookId);

/// <summary>
/// Where <see cref="FanOutBookNotifier"/> drops book events for the bot. Posting never blocks or throws, so a slow
/// or unreachable Telegram can never slow an agent run down; <see cref="TelegramDispatcher"/> reads the events.
/// Only books with an active binding are queued.
/// </summary>
public sealed class TelegramEventSink
{
    private readonly Channel<TgBookEvent> _events = Channel.CreateUnbounded<TgBookEvent>(
        new UnboundedChannelOptions { SingleReader = true });
    private readonly ConcurrentDictionary<int, byte> _boundBooks = new();
    private readonly ConcurrentDictionary<int, long> _lastStream = new();

    public TelegramEventSink(TelegramOptions options) => Enabled = options.Enabled;

    public bool Enabled { get; }
    public ChannelReader<TgBookEvent> Reader => _events.Reader;

    public void SetBound(int bookId, bool bound)
    {
        if (bound) _boundBooks[bookId] = 0;
        else _boundBooks.TryRemove(bookId, out _);
    }

    public void SetBoundBooks(IEnumerable<int> bookIds)
    {
        _boundBooks.Clear();
        foreach (var id in bookIds) _boundBooks[id] = 0;
    }

    public void Post(TgBookEvent ev)
    {
        if (Enabled && _boundBooks.ContainsKey(ev.BookId)) _events.Writer.TryWrite(ev);
    }

    /// <summary>Tokens are not queued one by one: the dispatcher reads the live text when it next draws a draft.</summary>
    public void MarkStreaming(int bookId)
    {
        if (Enabled && _boundBooks.ContainsKey(bookId)) _lastStream[bookId] = DateTime.UtcNow.Ticks;
    }

    /// <summary>When the book last streamed content (ticks; 0 = never), so each bound thread can tell it changed.</summary>
    public long LastStreamTicks(int bookId) => _lastStream.GetValueOrDefault(bookId);
}

/// <summary>
/// The <see cref="IBookNotifier"/> every agent and service uses: sends each event to the web UI (SignalR) and
/// hands a copy to the Telegram bot. The SignalR send goes first and stays the one that can fail the call.
/// </summary>
public sealed class FanOutBookNotifier(ABook.Api.Services.SignalRBookNotifier signalR, TelegramEventSink telegram) : IBookNotifier
{
    public Task StreamTokenAsync(int bookId, int? chapterId, AgentRole agentRole, string token, CancellationToken ct = default)
    {
        telegram.MarkStreaming(bookId);
        return signalR.StreamTokenAsync(bookId, chapterId, agentRole, token, ct);
    }

    public async Task NotifyQuestionAsync(int bookId, AgentMessage message, CancellationToken ct = default)
    {
        await signalR.NotifyQuestionAsync(bookId, message, ct);
        telegram.Post(new TgQuestionEvent(bookId, message.Id));
    }

    public async Task NotifyStatusChangedAsync(int bookId, AgentRole role, string state, int? chapterId = null, CancellationToken ct = default)
    {
        await signalR.NotifyStatusChangedAsync(bookId, role, state, chapterId, ct);
        telegram.Post(new TgStatusEvent(bookId, role, state, chapterId));
    }

    public async Task NotifyChapterUpdatedAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        await signalR.NotifyChapterUpdatedAsync(bookId, chapterId, ct);
        telegram.Post(new TgChapterEvent(bookId, chapterId));
    }

    public async Task NotifyWorkflowProgressAsync(int bookId, string step, bool isComplete, CancellationToken ct = default)
    {
        await signalR.NotifyWorkflowProgressAsync(bookId, step, isComplete, ct);
        telegram.Post(new TgProgressEvent(bookId, step, isComplete));
    }

    public async Task NotifyTokenStatsAsync(int bookId, int? chapterId, string agentRole, int promptTokens, int completionTokens, CancellationToken ct = default)
    {
        await signalR.NotifyTokenStatsAsync(bookId, chapterId, agentRole, promptTokens, completionTokens, ct);
        telegram.Post(new TgTokensEvent(bookId, promptTokens, completionTokens));
    }

    public async Task NotifyAgentErrorAsync(int bookId, string agentRole, string errorMessage, CancellationToken ct = default)
    {
        await signalR.NotifyAgentErrorAsync(bookId, agentRole, errorMessage, ct);
        telegram.Post(new TgErrorEvent(bookId, agentRole, errorMessage));
    }

    public Task NotifyMessagesUpdatedAsync(int bookId, CancellationToken ct = default) =>
        signalR.NotifyMessagesUpdatedAsync(bookId, ct);

    public async Task NotifyIllustrationsUpdatedAsync(int bookId, int? illustrationId, CancellationToken ct = default)
    {
        await signalR.NotifyIllustrationsUpdatedAsync(bookId, illustrationId, ct);
        telegram.Post(new TgIllustrationEvent(bookId, illustrationId));
    }

    public async Task NotifyBookChatAsync(int bookId, int? messageId, CancellationToken ct = default)
    {
        await signalR.NotifyBookChatAsync(bookId, messageId, ct);
        telegram.Post(new TgChatEvent(bookId, messageId));
    }

    public async Task NotifyRewoundAsync(int bookId, DateTime at, IReadOnlyList<int> hiddenChatMessageIds, string? origin, CancellationToken ct = default)
    {
        await signalR.NotifyRewoundAsync(bookId, at, hiddenChatMessageIds, origin, ct);
        telegram.Post(new TgRewoundEvent(bookId, at, hiddenChatMessageIds, origin));
    }
}
