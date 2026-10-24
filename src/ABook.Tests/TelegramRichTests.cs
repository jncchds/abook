using ABook.Api.Telegram;
using ABook.Core.Models;

namespace ABook.Tests;

public class TelegramRichTests
{
    [Fact]
    public void Link_IsALinkStyledCallbackButton()
    {
        var link = TelegramRich.Link("details <x>", "12:det:45");
        Assert.Equal("<tg-button type=\"callback_data\" style=\"link\" data=\"12:det:45\">details &lt;x&gt;</tg-button>", link);
    }

    [Fact]
    public void ToPlain_TurnsInlineButtonsIntoKeyboardButtons_AndBlocksIntoLines()
    {
        var rich = "<h3>📖 A book</h3><p><b>Title:</b> One " + TelegramRich.Link("edit", "k1:ed:title") + "</p>" +
                   "<ul><li>Alpha</li><li>Beta</li></ul>" +
                   "<table compact><tr><td><b>Status</b></td><td>Idle</td></tr></table>" +
                   "<details open><summary>Premise</summary><p>Long text</p></details>" +
                   "<footer>Note " + TelegramRich.Time(new DateTime(2026, 10, 24, 13, 5, 0, DateTimeKind.Utc)) + "</footer>";
        var (html, buttons) = TelegramRich.ToPlain(rich);

        Assert.Equal([("✏️ Title", "k1:ed:title")], buttons.Select(b => (b.Text, b.CallbackData!)));
        Assert.DoesNotContain("tg-button", html);
        Assert.DoesNotContain("<h3>", html);
        Assert.Contains("<b>📖 A book</b>", html);
        Assert.Contains("• Alpha", html);
        Assert.Contains("<b>Status</b> · Idle", html);
        Assert.Contains("<b>Premise</b>\n<blockquote expandable>", html);
        Assert.DoesNotContain("<b><b>", html);
        Assert.Contains("13:05 UTC", html);
        Assert.Contains("<i>Note", html);
    }

    [Fact]
    public void ToPlain_ClipsLongContent_WithoutLeavingATagOpen()
    {
        var (html, _) = TelegramRich.ToPlain("<p><b>" + new string('x', 5000) + "</b></p>");
        Assert.True(html.Length < 4096);
        Assert.EndsWith("</b>", html);
    }

    [Fact]
    public void SuggestionsRich_HaveNumberedLinesWithADetailsLink()
    {
        var html = TelegramFormat.ProposalsRich(9, [new ChatProposal { Id = 45, Label = "Add Mortimer" }, new ChatProposal { Id = 46, Label = "Note", Status = ChatProposalStatus.Rejected }], "hint");
        Assert.Contains("<b>1.</b>", html);
        Assert.Contains("data=\"9:det:45\">details</tg-button>", html);
        Assert.Contains("<s>Note</s>", html);
        Assert.DoesNotContain("/details_", html);
        Assert.Contains("<footer>hint</footer>", html);
    }

    [Fact]
    public void Lines_KeepLineBreaks() => Assert.Equal("a<br>b &amp; c", TelegramFormat.Lines("a\nb & c"));
}
