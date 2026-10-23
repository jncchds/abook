using ABook.Api.Hubs;
using ABook.Api.Services;
using Microsoft.AspNetCore.SignalR;

namespace ABook.Tests;

public class BookEventLogTests
{
    private sealed class FakeHub : IHubContext<BookHub>
    {
        private readonly FakeClients _clients = new();
        public List<(string Group, BookEvent Event)> Sent => _clients.Sent;
        public IHubClients Clients => _clients;
        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeClients : IHubClients, IClientProxy
    {
        public readonly List<(string Group, BookEvent Event)> Sent = [];
        private string _group = "";

        public IClientProxy Group(string groupName) { _group = groupName; return this; }

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Assert.Equal("BookEvent", method);
            lock (Sent) Sent.Add((_group, (BookEvent)args[0]!));
            return Task.CompletedTask;
        }

        public IClientProxy All => throw new NotSupportedException();
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Client(string connectionId) => throw new NotSupportedException();
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => throw new NotSupportedException();
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => throw new NotSupportedException();
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => throw new NotSupportedException();
        public IClientProxy User(string userId) => throw new NotSupportedException();
        public IClientProxy Users(IReadOnlyList<string> userIds) => throw new NotSupportedException();
    }

    private static Task NoOp() => Task.CompletedTask;

    [Fact]
    public async Task Events_are_numbered_per_book_in_order()
    {
        var hub = new FakeHub();
        var log = new BookEventLog(hub);

        await log.PublishAsync(1, "A", [1]);
        await log.PublishAsync(2, "B", [2]);
        await log.PublishAsync(1, "C", [1]);

        Assert.Equal([("1", 1L), ("2", 1L), ("1", 2L)], hub.Sent.Select(s => (s.Group, s.Event.Seq)));
        Assert.Equal(2, log.LastSent(1));
        Assert.Equal(1, log.LastSent(2));
        Assert.Equal(0, log.LastSent(3));
    }

    [Fact]
    public async Task Rejoin_replays_exactly_the_missed_events()
    {
        var log = new BookEventLog(new FakeHub());
        for (var i = 0; i < 5; i++) await log.PublishAsync(1, "E", [i]);

        var state = await log.JoinAsync(1, NoOp, log.Epoch, 2);

        Assert.Equal(log.Epoch, state.Epoch);
        Assert.Equal(5, state.Seq);
        Assert.Equal([3L, 4L, 5L], state.Events!.Select(e => e.Seq));
    }

    [Fact]
    public async Task Up_to_date_rejoin_gets_an_empty_replay()
    {
        var log = new BookEventLog(new FakeHub());
        await log.PublishAsync(1, "E", []);

        var state = await log.JoinAsync(1, NoOp, log.Epoch, 1);

        Assert.NotNull(state.Events);
        Assert.Empty(state.Events);
    }

    [Fact]
    public async Task First_join_or_another_server_session_gets_no_replay()
    {
        var log = new BookEventLog(new FakeHub());
        await log.PublishAsync(1, "E", []);

        var first = await log.JoinAsync(1, NoOp, null, null);
        var restarted = await log.JoinAsync(1, NoOp, "old-session", 1);
        var ahead = await log.JoinAsync(1, NoOp, log.Epoch, 7);

        Assert.Null(first.Events);
        Assert.Equal(1, first.Seq);
        Assert.Null(restarted.Events);
        Assert.Null(ahead.Events);
    }

    [Fact]
    public async Task Gap_older_than_the_buffer_gets_no_replay()
    {
        var log = new BookEventLog(new FakeHub());
        for (var i = 0; i < BookEventLog.Capacity + 10; i++) await log.PublishAsync(1, "E", []);

        var tooOld = await log.JoinAsync(1, NoOp, log.Epoch, 5);
        var justInside = await log.JoinAsync(1, NoOp, log.Epoch, 10);

        Assert.Null(tooOld.Events);
        Assert.Equal(BookEventLog.Capacity, justInside.Events!.Count);
        Assert.Equal(11, justInside.Events[0].Seq);
    }

    [Fact]
    public void Wire_format_matches_the_client()
    {
        // SignalR's JSON protocol defaults to camelCase; args keep their runtime types.
        var opts = new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web);
        var state = new BookSyncState("e", 3, [new BookEvent(3, "ChapterUpdated", [7, null, "x"])]);

        var json = System.Text.Json.JsonSerializer.Serialize(state, opts);

        Assert.Equal("""{"epoch":"e","seq":3,"events":[{"seq":3,"name":"ChapterUpdated","args":[7,null,"x"]}]}""", json);
    }

    [Fact]
    public async Task Join_adds_the_connection_to_the_group()
    {
        var log = new BookEventLog(new FakeHub());
        var added = false;

        await log.JoinAsync(1, () => { added = true; return Task.CompletedTask; }, null, null);

        Assert.True(added);
    }
}
