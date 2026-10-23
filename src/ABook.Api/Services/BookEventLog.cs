using ABook.Api.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;

namespace ABook.Api.Services;

/// <summary>One hub event of a book, numbered in the order it was sent.</summary>
public sealed record BookEvent(long Seq, string Name, object?[] Args);

/// <summary>
/// Where a client stands after joining a book: the server session (<see cref="Epoch"/>) and the last
/// sequence number sent so far. <see cref="Events"/> are the events it missed, or null when they can no
/// longer be replayed (first join, server restarted, or the gap fell out of the buffer) — the client
/// then re-reads the book's state instead.
/// </summary>
public sealed record BookSyncState(string Epoch, long Seq, IReadOnlyList<BookEvent>? Events);

/// <summary>
/// Numbers every hub event of a book and keeps the recent ones, so a client that lost its connection
/// can tell exactly what it missed and get it replayed on rejoin. Events of one book are sent one at a
/// time, so clients receive them in sequence order.
/// </summary>
public sealed class BookEventLog(IHubContext<BookHub> hub)
{
    /// <summary>Events kept per book; a longer gap is recovered by a full re-read.</summary>
    public const int Capacity = 5000;
    public static readonly TimeSpan Retention = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(1);

    /// <summary>Identifies this server process; sequence numbers restart with it.</summary>
    public string Epoch { get; } = Guid.NewGuid().ToString("N");

    private readonly ConcurrentDictionary<int, BookStream> _books = new();
    private long _lastSweepTicks = DateTime.UtcNow.Ticks;

    private sealed class BookStream
    {
        public readonly SemaphoreSlim Gate = new(1, 1);
        public readonly Queue<(BookEvent Event, DateTime At)> Recent = new();
        public long LastSeq;
        // Updated once an event has been handed to every connection — what Heartbeat reports.
        public long LastSent;
    }

    private BookStream Get(int bookId) => _books.GetOrAdd(bookId, _ => new BookStream());

    public async Task PublishAsync(int bookId, string name, object?[] args, CancellationToken ct = default)
    {
        SweepIfDue();
        var s = Get(bookId);
        await s.Gate.WaitAsync(ct);
        try
        {
            var ev = new BookEvent(++s.LastSeq, name, args);
            lock (s.Recent)
            {
                s.Recent.Enqueue((ev, DateTime.UtcNow));
                Trim(s.Recent, DateTime.UtcNow);
            }
            try
            {
                await hub.Clients.Group(bookId.ToString()).SendAsync("BookEvent", ev, ct);
            }
            finally
            {
                // Even if the send was cancelled the event is numbered and buffered: clients see the gap
                // (next event or heartbeat) and get it replayed.
                Volatile.Write(ref s.LastSent, ev.Seq);
            }
        }
        finally
        {
            s.Gate.Release();
        }
    }

    /// <summary>
    /// Adds the connection to the book's group and returns what it missed since <paramref name="afterSeq"/>
    /// of session <paramref name="epoch"/>. Done under the book's send lock, so every event is either in
    /// the returned list or sent live to the connection afterwards — never neither.
    /// </summary>
    public async Task<BookSyncState> JoinAsync(int bookId, Func<Task> addToGroup, string? epoch, long? afterSeq)
    {
        var s = Get(bookId);
        await s.Gate.WaitAsync();
        try
        {
            await addToGroup();
            var last = s.LastSeq;
            if (epoch != Epoch || afterSeq is not { } after || after > last)
                return new BookSyncState(Epoch, last, null);

            lock (s.Recent)
            {
                Trim(s.Recent, DateTime.UtcNow);
                var oldest = s.Recent.Count > 0 ? s.Recent.Peek().Event.Seq : last + 1;
                if (after + 1 < oldest)
                    return new BookSyncState(Epoch, last, null);
                return new BookSyncState(Epoch, last, s.Recent.Select(r => r.Event).Where(e => e.Seq > after).ToList());
            }
        }
        finally
        {
            s.Gate.Release();
        }
    }

    /// <summary>The last event of the book already sent to its group (0 if none this session).</summary>
    public long LastSent(int bookId) =>
        _books.TryGetValue(bookId, out var s) ? Volatile.Read(ref s.LastSent) : 0;

    private static void Trim(Queue<(BookEvent Event, DateTime At)> q, DateTime now)
    {
        while (q.Count > 0 && (q.Count > Capacity || now - q.Peek().At > Retention))
            q.Dequeue();
    }

    // Books that went quiet would otherwise keep their last events until the next one.
    private void SweepIfDue()
    {
        var now = DateTime.UtcNow;
        var last = Interlocked.Read(ref _lastSweepTicks);
        if (now.Ticks - last < SweepInterval.Ticks
            || Interlocked.CompareExchange(ref _lastSweepTicks, now.Ticks, last) != last)
            return;
        foreach (var s in _books.Values)
            lock (s.Recent) Trim(s.Recent, now);
    }
}
