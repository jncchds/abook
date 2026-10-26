using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

/// <summary>The book chat names the author's notes N1, N2 … and can update or archive them instead of adding more.</summary>
public class BookChatNotesTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Clarification C(int id, string answer, int minute, bool archived = false) =>
        new() { Id = id, Question = $"Q{id}", Answer = answer, CreatedAt = T0.AddMinutes(minute), IsArchived = archived };

    // Ids out of creation order, an archived one and an open (unanswered) one: numbering follows creation, active answered only.
    private static readonly Clarification[] Clarifications =
        [C(7, "No swearing.", 2), C(3, "Past tense.", 1), C(9, "Old rule.", 0, archived: true), C(4, "", 3)];

    private static BookChatState State() => new(false, [], [], [], BookChatNote.Of(Clarifications));

    private static RawChatAction A(string type, string? name, string? text = null) =>
        new(type, "", null, name, [], null, null, null, text, null);

    [Fact]
    public void Notes_AreNumberedByCreation_ActiveAndAnsweredOnly()
    {
        Assert.Equal([new BookChatNote(3, 1, "Past tense."), new BookChatNote(7, 2, "No swearing.")], BookChatNote.Of(Clarifications));

        var digest = PromptTemplates.BookChatDigest(
            new Book { Title = "B", Premise = "P", Clarifications = [.. Clarifications] }, false, null, [], [], [], []);
        Assert.Contains("[N1] Q: Q3 A: Past tense.", digest);
        Assert.Contains("[N2] Q: Q7 A: No swearing.", digest);
        Assert.DoesNotContain("Old rule", digest);
    }

    [Theory]
    [InlineData("N2")]
    [InlineData("n2")]
    [InlineData("[N2]")]
    [InlineData("2")]
    public void UpdateNote_TargetsTheNumberedNote(string reference)
    {
        var (actions, dropped) = BookChatActions.Normalize([A(BookChatActions.UpdateNote, reference, "Mild swearing only.")], State());

        Assert.Empty(dropped);
        var action = Assert.Single(actions);
        Assert.Equal("note:7", action.Target);
        Assert.Equal(("N2", "Mild swearing only."), (action.Payload.Name, action.Payload.Text));
    }

    [Fact]
    public void ArchiveNote_TargetsTheNumberedNote()
    {
        var (actions, _) = BookChatActions.Normalize([A(BookChatActions.ArchiveNote, "N1")], State());
        Assert.Equal("note:3", Assert.Single(actions).Target);
    }

    [Theory]
    [InlineData(null, "x", "no note number")]
    [InlineData("N5", "x", "no note N5")]
    [InlineData("N1", null, "no new note text")]
    [InlineData("N1", " Past tense. ", "already says that")]
    public void NoteActions_ThatCannotApply_AreDropped(string? reference, string? text, string reason)
    {
        var (actions, dropped) = BookChatActions.Normalize([A(BookChatActions.UpdateNote, reference, text)], State());
        Assert.Empty(actions);
        Assert.Contains(reason, Assert.Single(dropped));
    }
}
