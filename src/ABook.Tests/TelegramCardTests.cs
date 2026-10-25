using ABook.Api.Telegram;
using ABook.Api.Telegram.Cards;
using ABook.Core.Models;

namespace ABook.Tests;

public class TelegramCardFieldTests
{
    private static CardField F(string key) => CardFields.NewBook.Concat(CardFields.Character).First(f => f.Key == key);

    [Theory]
    [InlineData("chapters", "12", "12", null)]
    [InlineData("chapters", " 7 ", "7", null)]
    [InlineData("chapters", "many", null, "Chapters must be a whole number.")]
    [InlineData("mode", "interactive", "Interactive", null)]
    [InlineData("mode", "both", null, "Mode is one of: Autonomous, Interactive.")]
    [InlineData("genre", "Cozy mystery", "Cozy mystery", null)]
    [InlineData("language", "russian", "Russian", null)]
    [InlineData("title", "  ", null, "Title can't be empty.")]
    [InlineData("role", "antagonist", "Antagonist", null)]
    public void Validate(string key, string text, string? value, string? error)
    {
        var (v, e) = CardFields.Validate(F(key), text);
        Assert.Equal(value, v);
        Assert.Equal(error, e);
    }

    [Fact]
    public void State_RoundTrips_AndGoToClearsTheDraft()
    {
        var s = new CardState { Kind = "char", ItemId = 4, Awaiting = "arc", Draft = { ["arc"] = "falls" }, Note = "x" };
        var back = CardState.Parse(s.Serialize());
        Assert.Equal(("char", 4, "arc", "falls"), (back.Kind, back.ItemId, back.Awaiting, back.Draft["arc"]));
        back.GoTo("chars");
        Assert.Equal(("chars", (int?)null, (string?)null, 0), (back.Kind, back.ItemId, back.Awaiting, back.Draft.Count));
    }

    [Theory]
    [InlineData("k12:ed:title", true)]
    [InlineData("12:apply:3", false)]
    [InlineData("k", false)]
    [InlineData(null, false)]
    public void CardCallbacks_AreRecognized(string? data, bool expected) => Assert.Equal(expected, TelegramCardService.IsCardCallback(data));
}

public class TelegramProposalDetailsTests
{
    [Fact]
    public void Details_ShowTheChangeAsTheUiDiff()
    {
        var p = new ChatProposal
        {
            Id = 45, BookId = 7, Label = "Make Mortimer crueler", Type = "upsert_character", Status = ChatProposalStatus.Pending,
            PreviewBefore = "{\n  \"name\": \"Mortimer\",\n  \"personality\": \"grumpy\",\n  \"arc\": \"redemption\"\n}",
            PreviewAfter = "{\n  \"name\": \"Mortimer\",\n  \"personality\": \"cruel <and> cold\",\n  \"arc\": \"redemption\"\n}",
            ConflictNote = "Chapter 2 calls him kind.",
        };
        var html = TelegramFormat.ProposalDetailsRich(p, 2);
        Assert.Contains("🔍 2. Make Mortimer crueler", html);
        Assert.Contains("<b>Character</b>", html);
        Assert.Contains("−   \"personality\": \"grumpy\",", html);
        Assert.Contains("+   \"personality\": \"cruel &lt;and&gt; cold\",", html);
        Assert.Contains("    \"arc\": \"redemption\"", html);   // context
        Assert.Contains("Conflict", html);
        Assert.DoesNotContain("Open ↗", html);   // only once applied
    }

    [Fact]
    public void Details_OfAWriteAction_ShowItsInstructions()
    {
        var p = new ChatProposal
        {
            Label = "Write Chapter 4", Type = "write_chapter", Status = ChatProposalStatus.Pending,
            PayloadJson = """{"type":"write_chapter","instructions":"A tense chase across the rooftops."}""",
        };
        var html = TelegramFormat.ProposalDetailsRich(p, 1, bibleLocked: false);
        Assert.Contains("rooftops", html);
        Assert.Contains("locks the premise", html);
        Assert.DoesNotContain("Changes", html);
    }

    [Fact]
    public void Details_OfAnAppliedChange_LinkToIt()
    {
        var p = new ChatProposal { BookId = 7, Label = "Add Mara", Type = "upsert_character", Status = ChatProposalStatus.Applied,
            Target = "character:Mara", PreviewAfter = "{\n  \"name\": \"Mara\"\n}" };
        Assert.Contains("https://book.example.com/books/7/characters", TelegramFormat.ProposalDetailsRich(p, 1, baseUrl: "https://book.example.com"));
    }

    [Fact]
    public void Diff_FoldsLongUnchangedStretches()
    {
        var before = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"line {i}"));
        var after = before.Replace("line 10", "line ten");
        var diff = TelegramFormat.DiffText(before, after);
        Assert.Contains("⋯ 7 unchanged lines", diff);
        Assert.Contains("− line 10", diff);
        Assert.Contains("+ line ten", diff);
        Assert.Contains("  line 12", diff);
        Assert.DoesNotContain("line 13", diff);
        Assert.Equal("", TelegramFormat.DiffText("same", "same"));
    }

    [Fact]
    public void SuggestionsList_LinksEachToItsDetails()
    {
        var text = TelegramFormat.ProposalsText([new ChatProposal { Id = 45, Label = "A" }, new ChatProposal { Id = 46, Label = "B" }]);
        Assert.Contains("/details_45", text);
        Assert.Contains("/details_46", text);
    }
}
