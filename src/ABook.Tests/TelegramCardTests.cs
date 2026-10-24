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
    public void Details_ShowEachChangedField_WasAndNow()
    {
        var p = new ChatProposal
        {
            Id = 45, Label = "Make Mortimer crueler", Type = "upsert_character", Status = ChatProposalStatus.Pending,
            PreviewBefore = """{"name":"Mortimer","personality":"grumpy","arc":"redemption"}""",
            PreviewAfter = """{"name":"Mortimer","personality":"cruel <and> cold","arc":"redemption"}""",
            ConflictNote = "Chapter 2 calls him kind.",
        };
        var html = TelegramFormat.ProposalDetails(p, 2);
        Assert.Contains("🔍 <b>2. Make Mortimer crueler</b>", html);
        Assert.Contains("<b>Personality</b>", html);
        Assert.Contains("grumpy", html);
        Assert.Contains("cruel &lt;and&gt; cold", html);
        Assert.DoesNotContain("<b>Arc</b>", html);   // unchanged
        Assert.Contains("Conflict", html);
    }

    [Fact]
    public void Details_OfAPlainTextAction_ShowWhatItDoes()
    {
        var p = new ChatProposal { Label = "Write Chapter 4", Type = "write_chapter", PreviewAfter = "A tense chase across the rooftops." };
        var html = TelegramFormat.ProposalDetails(p, 1);
        Assert.Contains("What it does", html);
        Assert.Contains("rooftops", html);
    }

    [Fact]
    public void SuggestionsList_LinksEachToItsDetails()
    {
        var text = TelegramFormat.ProposalsText([new ChatProposal { Id = 45, Label = "A" }, new ChatProposal { Id = 46, Label = "B" }]);
        Assert.Contains("/details_45", text);
        Assert.Contains("/details_46", text);
    }
}
