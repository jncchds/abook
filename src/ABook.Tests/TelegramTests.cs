using System.Reflection;
using System.Text;
using System.Text.Json;
using ABook.Api.Services;
using ABook.Api.Telegram;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Configuration;

namespace ABook.Tests;

public class TelegramOptionsTests
{
    [Theory]
    [InlineData("book.example.com", "https://book.example.com")]
    [InlineData("https://book.example.com/", "https://book.example.com")]
    [InlineData("book.example.com:8443/some/path", "https://book.example.com:8443")]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void NormalizeBaseUrl(string? host, string? expected) =>
        Assert.Equal(expected, TelegramOptions.NormalizeBaseUrl(host));

    private static TelegramOptions From(Dictionary<string, string?> values) =>
        TelegramOptions.FromConfiguration(new ConfigurationBuilder().AddInMemoryCollection(values).Build());

    [Fact]
    public void NoToken_BotIsOff()
    {
        var options = From(new() { ["ABOOK_HOSTNAME"] = "book.example.com" });
        Assert.False(options.Enabled);
        Assert.False(options.UseWebhook);
    }

    [Fact]
    public void TokenAndHostname_UseTheWebhook()
    {
        var options = From(new() { ["TELEGRAM_BOT_TOKEN"] = "123:abc", ["ABOOK_HOSTNAME"] = "book.example.com" });
        Assert.True(options.UseWebhook);
        Assert.Equal("https://book.example.com/api/telegram/webhook", options.WebhookUrl);
    }

    [Fact]
    public void TokenWithoutHostname_Polls()
    {
        var options = From(new() { ["TELEGRAM_BOT_TOKEN"] = "123:abc" });
        Assert.True(options.Enabled);
        Assert.False(options.UseWebhook);
    }

    [Fact]
    public void InvalidSecret_IsReplacedByARandomOne()
    {
        var options = From(new() { ["TELEGRAM_BOT_TOKEN"] = "1:a", ["TELEGRAM_WEBHOOK_SECRET"] = "has spaces!" });
        Assert.Matches("^[A-F0-9]{64}$", options.WebhookSecret);
        var kept = From(new() { ["TELEGRAM_BOT_TOKEN"] = "1:a", ["TELEGRAM_WEBHOOK_SECRET"] = "my_secret-1" });
        Assert.Equal("my_secret-1", kept.WebhookSecret);
    }
}

public class TelegramFormatTests
{
    [Fact]
    public void Callback_RoundTrips()
    {
        var data = TelegramFormat.Callback(42, "apply", 1234);
        Assert.True(Encoding.UTF8.GetByteCount(data) <= 64);
        Assert.True(TelegramFormat.TryParseCallback(data, out var binding, out var action, out var arg));
        Assert.Equal((42, "apply", 1234L), (binding, action, arg));

        Assert.True(TelegramFormat.TryParseCallback(TelegramFormat.Callback(7, "stop"), out binding, out action, out arg));
        Assert.Equal((7, "stop", 0L), (binding, action, arg));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("stop")]
    [InlineData("x:stop")]
    [InlineData("1:apply:notanumber")]
    public void Callback_RejectsGarbage(string? data) =>
        Assert.False(TelegramFormat.TryParseCallback(data, out _, out _, out _));

    [Fact]
    public void Clip_CutsAtAWordWithAnEllipsis()
    {
        var clipped = TelegramFormat.Clip("The quick brown fox jumps over the lazy dog", 20);
        Assert.True(clipped.Length <= 20);
        Assert.EndsWith("…", clipped);
        Assert.Equal("short", TelegramFormat.Clip("  short  ", 20));
    }

    [Fact]
    public void SplitForMessages_KeepsEveryWord_AndRespectsTheLimit()
    {
        var text = string.Join("\n\n", Enumerable.Range(1, 200).Select(i => $"Paragraph {i} has a few words in it."));
        var chunks = TelegramFormat.SplitForMessages(text, 500);
        Assert.True(chunks.Count > 1);
        Assert.All(chunks, c => Assert.True(c.Length <= 500));
        Assert.Equal(TelegramFormat.CountWords(text), chunks.Sum(TelegramFormat.CountWords));
    }

    [Fact]
    public void H_EscapesHtml() => Assert.Equal("a &lt;b&gt; &amp; c", TelegramFormat.H("a <b> & c"));

    private static TelegramFormat.StatusView View(BookMode mode, AgentRunStatus? run, int chapters = 2) => new(
        new Book { Id = 1, Title = "T", Mode = mode, TargetChapterCount = chapters },
        Enumerable.Range(1, chapters).Select(i => new Chapter { Id = i, Number = i, Status = i == 1 ? ChapterStatus.Done : ChapterStatus.Outlined }).ToList(),
        run, null, null, 0, 0, 0);

    private static List<string> Buttons(TelegramAccessLevel level, TelegramFormat.StatusView view) =>
        TelegramFormat.StatusKeyboard(new TelegramBinding { Id = 9, Level = level }, view)
            .SelectMany(r => r).Select(b => b.CallbackData!).ToList();

    [Fact]
    public void StatusKeyboard_FollowsTheLevel()
    {
        var running = View(BookMode.Autonomous, new AgentRunStatus(AgentRole.Writer, RunStates.Running, 2));
        Assert.Equal(["9:refresh"], Buttons(TelegramAccessLevel.Read, running));
        Assert.Equal(["9:refresh", "9:export"], Buttons(TelegramAccessLevel.Participate, running));
        Assert.Equal(["9:stop", "9:refresh", "9:export"], Buttons(TelegramAccessLevel.Control, running));

        var idle = View(BookMode.Autonomous, null);
        Assert.Contains("9:cont", Buttons(TelegramAccessLevel.Control, idle));
        Assert.Contains("9:start", Buttons(TelegramAccessLevel.Control, View(BookMode.Autonomous, null, chapters: 0)));
        // Interactive books are written in the chat: no Start/Continue.
        Assert.DoesNotContain("9:cont", Buttons(TelegramAccessLevel.Control, View(BookMode.Interactive, null)));
    }

    [Fact]
    public void StatusCard_ShowsProgressAndEscapesTheTitle()
    {
        var view = View(BookMode.Autonomous, new AgentRunStatus(AgentRole.Writer, RunStates.Running, 2)) with
        {
            Book = new Book { Title = "<Dune>", Mode = BookMode.Autonomous, TargetChapterCount = 2 },
            Step = "Writing Chapter 2…",
            LiveWords = 1234,
        };
        var html = TelegramFormat.StatusCard(view);
        Assert.Contains("&lt;Dune&gt;", html);
        Assert.Contains("1/2 chapters done", html);
        Assert.Contains("Chapter 2", html);
        Assert.Contains("1,234 words", html);
    }
}

public class TelegramRewindFormatTests
{
    [Fact]
    public void Micros_RoundTrip_AndFitButtonData()
    {
        var at = new DateTime(2026, 10, 24, 13, 5, 12, DateTimeKind.Utc).AddTicks(1234560);
        Assert.Equal(at, TelegramFormat.FromMicros(TelegramFormat.Micros(at)));
        Assert.True(Encoding.UTF8.GetByteCount(TelegramFormat.Callback(123456, "rwat", TelegramFormat.Micros(at))) <= 64);
    }

    [Fact]
    public void Micros_OfAMomentJustBefore_StaysBefore()
    {
        var created = new DateTime(2026, 10, 24, 13, 5, 12, DateTimeKind.Utc).AddTicks(10 * 777);   // whole microseconds, like Postgres
        Assert.True(TelegramFormat.FromMicros(TelegramFormat.Micros(created.AddTicks(-1))) < created);
    }

    [Fact]
    public void Preview_SummarizesAndListsTheChanges()
    {
        var plan = new RewindPlan(1, new DateTime(2026, 10, 24, 13, 5, 0, DateTimeKind.Utc),
        [
            new RewindChange("Chapter", 3, RewindChangeKind.Restore, "Chapter 3: <Dawn>", ["text", "status"]),
            new RewindChange("Chapter", 4, RewindChangeKind.Archive, "Chapter 4: Later", []),
            new RewindChange("CharacterCard", 7, RewindChangeKind.Recreate, "Character Mortimer", []),
        ], 5, "1-2");
        var html = TelegramFormat.RewindPreview(plan);
        Assert.Contains("24 Oct 13:05:00 UTC", html);
        Assert.Contains("1 chapter restored · 1 chapter archived · 1 character brought back · 5 chat message(s) hidden", html);
        Assert.Contains("Chapter 3: &lt;Dawn&gt;", html);
        Assert.Contains("<i>(text, status)</i>", html);
        Assert.Contains("<blockquote expandable>", html);
    }
}

public class TelegramBindCodeTests
{
    [Theory]
    [InlineData("AbCdEfGhIjKlMnOpQrStUvWx", true)]
    [InlineData("short", false)]
    [InlineData("has space in the middle!!", false)]
    [InlineData(null, false)]
    public void LooksLikeCode(string? code, bool expected) =>
        Assert.Equal(expected, TelegramBindingService.LooksLikeCode(code));

    [Fact]
    public void HashCode_IsStableLowercaseHex()
    {
        var hash = TelegramBindingService.HashCode("AbCdEfGhIjKlMnOpQrStUvWx");
        Assert.Matches("^[a-f0-9]{64}$", hash);
        Assert.Equal(hash, TelegramBindingService.HashCode("AbCdEfGhIjKlMnOpQrStUvWx"));
    }
}

public class TelegramUpdateParsingTests
{
    [Fact]
    public void TopicMessage_HasItsTopic_ButAPlainReplyThreadDoesNot()
    {
        const string json = """
            {"update_id":5,"message":{"message_id":10,"message_thread_id":77,"is_topic_message":true,
             "from":{"id":1,"is_bot":false,"first_name":"Anna","username":"anna"},
             "chat":{"id":-100123,"type":"supergroup","title":"Writers","is_forum":true},
             "date":1700000000,"text":"/bind ABC",
             "reply_to_message":{"message_id":9,"chat":{"id":-100123,"type":"supergroup"},"date":1}}}
            """;
        var update = JsonSerializer.Deserialize<TgUpdate>(json, TelegramApi.Json)!;
        Assert.Equal(77, update.Message!.TopicId);
        Assert.Equal("@anna", update.Message.From!.Display);
        Assert.Equal(9, update.Message.ReplyToMessage!.MessageId);

        const string reply = """{"update_id":6,"message":{"message_id":11,"message_thread_id":9,"chat":{"id":-5,"type":"supergroup"},"date":1}}""";
        Assert.Null(JsonSerializer.Deserialize<TgUpdate>(reply, TelegramApi.Json)!.Message!.TopicId);
    }

    [Fact]
    public void StoppedGeneration_Parses()
    {
        const string json = """{"update_id":7,"stopped_message_generation":{"chat":{"id":3,"type":"private"},"message_thread_id":4,"draft_id":99}}""";
        var stopped = JsonSerializer.Deserialize<TgUpdate>(json, TelegramApi.Json)!.StoppedMessageGeneration!;
        Assert.Equal((3L, 4, 99L), (stopped.Chat.Id, stopped.MessageThreadId, stopped.DraftId));
    }
}

public class TelegramCompletionTests
{
    [Fact]
    public void BookIsComplete_OnlyWhenEveryChapterIsDone_AndTheTargetIsReached()
    {
        var book = new Book { Mode = BookMode.Autonomous, TargetChapterCount = 2 };
        Chapter Ch(int n, ChapterStatus s) => new() { Id = n, Number = n, Status = s };
        Assert.True(BindingWorker.IsBookComplete(book, [Ch(1, ChapterStatus.Done), Ch(2, ChapterStatus.Done)]));
        Assert.False(BindingWorker.IsBookComplete(book, [Ch(1, ChapterStatus.Done), Ch(2, ChapterStatus.Editing)]));
        Assert.False(BindingWorker.IsBookComplete(book, [Ch(1, ChapterStatus.Done)]));
        var interactive = new Book { Mode = BookMode.Interactive, TargetChapterCount = 2 };
        Assert.False(BindingWorker.IsBookComplete(interactive, [Ch(1, ChapterStatus.Done), Ch(2, ChapterStatus.Done)]));
    }
}

public class ChapterExportTests
{
    private class FakeRepo : DispatchProxy
    {
        public Book Book = null!;

        public static (IBookRepository Repo, FakeRepo Fake) Create()
        {
            var repo = Create<IBookRepository, FakeRepo>();
            return (repo, (FakeRepo)(object)repo);
        }

        protected override object? Invoke(MethodInfo? method, object?[]? args) => method!.Name switch
        {
            nameof(IBookRepository.GetByIdWithActiveDetailsAsync) => Task.FromResult<Book?>(Book),
            nameof(IBookRepository.GetStoryBibleAsync) => Task.FromResult<StoryBible?>(null),
            nameof(IBookRepository.GetIllustrationsAsync) => Task.FromResult<IReadOnlyList<Illustration>>([]),
            _ => throw new NotSupportedException(method.Name),
        };
    }

    [Theory]
    [InlineData("html", ".html")]
    [InlineData("epub", ".epub")]
    [InlineData("fb2", ".fb2")]
    public async Task ExportsOnlyTheChapter_AndLeavesTheBookAlone(string format, string extension)
    {
        var (repo, fake) = FakeRepo.Create();
        fake.Book = new Book
        {
            Id = 1, Title = "My Book", Language = "English",
            Chapters =
            [
                new Chapter { Id = 10, Number = 1, Title = "One", Content = "First chapter text." },
                new Chapter { Id = 11, Number = 2, Title = "Two", Content = "Second chapter text." },
            ],
        };

        var (bytes, _, fileName) = await new BookExportService(repo).ExportChapterAsync(1, 11, format);

        Assert.Equal("my-book-ch02" + extension, fileName);
        Assert.Equal(2, fake.Book.Chapters.Count);
        if (format != "epub")
        {
            var text = Encoding.UTF8.GetString(bytes);
            Assert.Contains("Second chapter text.", text);
            Assert.DoesNotContain("First chapter text.", text);
        }
        else Assert.True(bytes.Length > 0);
    }
}
