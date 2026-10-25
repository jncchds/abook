using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

/// <summary>"Anything else?": a reply's suggestions go back to the LLM with the author's note.</summary>
public class BookChatReviseTests
{
    [Fact]
    public void ReviseTurn_CarriesEverySuggestionInFull_AndTheNote()
    {
        var turn = PromptTemplates.BookChatReviseTurn("", "Here is what I propose.",
        [
            ("Pending", "upsert_character", "Make Mortimer crueler", """{"type":"upsert_character","name":"Mortimer"}""", null),
            ("Applied", "add_note", "Remember the storm", """{"type":"add_note","text":"storm"}""", null),
            ("Failed", "patch_chapter", "Fix the date", """{"type":"patch_chapter"}""", "not found"),
        ], "Also give him a limp.");
        Assert.Contains("Here is what I propose.", turn);
        Assert.Contains("1. [pending] upsert_character: Make Mortimer crueler", turn);
        Assert.Contains("\"name\":\"Mortimer\"", turn);
        Assert.Contains("2. [applied] add_note", turn);
        Assert.Contains("(note: not found)", turn);
        Assert.Contains("complete new list", turn);
        Assert.EndsWith("My note: Also give him a limp.", turn);
    }

    [Fact]
    public void ResentRevision_SaysWhatItWas()
    {
        var (_, content) = BookChatAgent.Turn(new BookChatMessage { Role = BookChatRole.Author, Content = "Shorter.", RevisesMessageId = 4 });
        Assert.StartsWith("[Asked to revise", content);
        Assert.EndsWith("Shorter.", content);
    }

    [Theory]
    [InlineData(ChatProposalStatus.Pending, true)]
    [InlineData(ChatProposalStatus.Failed, true)]
    [InlineData(ChatProposalStatus.Queued, true)]
    [InlineData(ChatProposalStatus.Applied, false)]
    [InlineData(ChatProposalStatus.Rejected, false)]
    public void OnlyOpenSuggestionsAreReplaced(ChatProposalStatus status, bool revisable) =>
        Assert.Equal(revisable, BookChatAgent.IsRevisable(new ChatProposal { Status = status }));
}
