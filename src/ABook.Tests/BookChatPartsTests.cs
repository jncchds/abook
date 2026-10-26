using System.Text.Json;
using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

/// <summary>Two-step book chat replies: asking for parts of the book, and the set_part_requests suggestion.</summary>
public class BookChatPartsTests
{
    [Fact]
    public void Parse_ReadsRequestedParts_AlsoAsPlainText_AndNoneWhenTheKeyIsMissing()
    {
        var reply = BookChatActions.Parse("""
            {"message":"Let me read it.","conflicts":[],"actions":[],
             "parts":[{"kind":"CHAPTER_TEXT","chapter":3,"name":null},{"kind":"character","chapter":null,"name":" Mara "},"chapter_plan 2",""]}
            """);

        Assert.NotNull(reply);
        Assert.Equal(
            [new ChatPartRequest("chapter_text", 3, null), new ChatPartRequest("character", null, "Mara"), new ChatPartRequest("chapter_plan", 2, null)],
            reply!.Parts);
        Assert.Empty(BookChatActions.Parse("""{"message":"Hi","conflicts":[],"actions":[]}""")!.Parts);
    }

    [Fact]
    public void ReplySchema_HasThePartsKey_OnlyForTheFirstStep()
    {
        using var first = JsonDocument.Parse(BookChatActions.ReplySchema(bibleLocked: false, partRequests: true));
        using var plain = JsonDocument.Parse(BookChatActions.ReplySchema(bibleLocked: false));

        Assert.True(first.RootElement.GetProperty("properties").TryGetProperty("parts", out _));
        Assert.Contains("parts", first.RootElement.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.False(plain.RootElement.GetProperty("properties").TryGetProperty("parts", out _));
        Assert.Equal(3, plain.RootElement.GetProperty("required").GetArrayLength());
    }

    [Fact]
    public void SystemPrompt_ExplainsParts_OnlyWhenTheyMayBeAskedFor()
    {
        var book = new Book { Title = "B", Genre = "G", Language = "English" };
        var on = PromptTemplates.BookChatSystem(book, false, BookChatActions.Available(false), partRequests: true);
        var off = PromptTemplates.BookChatSystem(book, false, BookChatActions.Available(false));

        Assert.Contains("\"parts\"", on);
        Assert.Contains("chapter_text", on);
        Assert.DoesNotContain("\"parts\"", off);
        Assert.Contains(BookChatActions.SetPartRequests, off);
    }

    [Fact]
    public void ResolveParts_ServesWhatExists_SaysWhyNot_AndKeepsToTheBudget()
    {
        Chapter[] chapters =
        [
            new() { Number = 1, Title = "One", Outline = "Mara arrives.", Content = new string('a', 3000), Summary = "She came." },
            new() { Number = 2, Title = "Two", Outline = "Planned.", Content = "" },
            new() { Number = 3, Title = "Three", Content = new string('c', 10_000) },
        ];
        CharacterCard[] characters = [new() { Name = "Mara", Role = CharacterRole.Supporting, Personality = "Wry" }];
        PlotThread[] threads = [new() { Name = "The Map", Description = "Who drew it?" }];

        var (parts, missing, labels) = BookChatAgent.ResolveParts(
            [
                new("chapter_text", 1, null), new("chapter_text", 1, null), new("chapter_text", 2, null), new("chapter_plan", 2, null),
                new("character", null, "mara"), new("plot_thread", null, "The Map"), new("plot_thread", null, "Nope"),
                new("chapter_text", 9, null), new("chapter_text", 3, null), new("section", 1, null),
            ],
            chapters, characters, threads, budget: 6000);

        Assert.Equal(["Chapter 1", "the plan of Chapter 2", "Mara", "the thread The Map", "Chapter 3"], labels);
        Assert.Equal(new string('a', 3000), parts[0].Body);
        Assert.Contains("outline: Planned.", parts[1].Body);
        Assert.Contains("personality: Wry", parts[2].Body);
        // Chapter 3 is cut to the room left.
        Assert.EndsWith(PromptTemplates.BookChatPartCut, parts[4].Body);
        Assert.True(parts.Sum(p => p.Body.Length) <= 6000 + PromptTemplates.BookChatPartCut.Length);
        Assert.Equal(4, missing.Count);   // chapter 2 not written, unknown thread, no chapter 9, unknown kind
        Assert.Contains(missing, m => m.StartsWith("chapter_text 2:"));
        Assert.Contains(missing, m => m.StartsWith("section 1:"));

        var turn = PromptTemplates.BookChatPartsTurn(parts, missing);
        Assert.Contains("## Chapter 1: One — full text", turn);
        Assert.Contains("Not available:", turn);
    }

    [Fact]
    public void ResolveParts_StopsAtTheMostPartsPerReply()
    {
        var characters = Enumerable.Range(1, PromptTemplates.BookChatPartsMax + 2)
            .Select(i => new CharacterCard { Name = $"C{i}" }).ToList();
        var (parts, missing, _) = BookChatAgent.ResolveParts(
            characters.Select(c => new ChatPartRequest("character", null, c.Name)).ToList(), [], characters, []);

        Assert.Equal(PromptTemplates.BookChatPartsMax, parts.Count);
        Assert.Equal(2, missing.Count);
    }

    [Theory]
    [InlineData("on", "on")]
    [InlineData("Enable", "on")]
    [InlineData("off", "off")]
    public void Normalize_SetPartRequests_TakesOnOrOff(string text, string stored)
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [new RawChatAction(BookChatActions.SetPartRequests, "", null, null, [], null, null, null, text, null)],
            new BookChatState(true, [], [], []));

        Assert.Empty(dropped);
        var action = Assert.Single(actions);
        Assert.Equal(stored, action.Payload.Text);
        Assert.Equal(BookContentService.PartRequestsTarget, action.Target);
    }

    [Fact]
    public void Normalize_SetPartRequests_WithoutOnOrOff_IsDropped()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [new RawChatAction(BookChatActions.SetPartRequests, "", null, null, [], null, null, null, "maybe", null)],
            new BookChatState(false, [], [], []));
        Assert.Empty(actions);
        Assert.Single(dropped);
    }
}
