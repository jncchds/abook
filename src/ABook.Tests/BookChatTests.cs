using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

public class BookChatTests
{
    private static BookChatState State(bool locked = false, params BookChatChapter[] chapters) =>
        new(locked, ["Mara"], ["The Map"], chapters);

    private static RawChatAction A(string type, int? chapter = null, string? name = null,
        (string, string)[]? changes = null, string? text = null, string? instructions = null,
        string? original = null, string? replacement = null, string? option = null) =>
        new(type, "", chapter, name, (changes ?? []).Select(c => KeyValuePair.Create(c.Item1, c.Item2)).ToList(),
            instructions, original, replacement, text, option);

    [Fact]
    public void Parse_ReadsMessageConflictsAndActions_AcceptingAnObjectForChanges()
    {
        var reply = BookChatActions.Parse("""
            Sure! {"message":"Done.","conflicts":[{"description":"Eyes","canon":"green","chapter":"2"}],
             "actions":[{"type":"UPSERT_CHARACTER","label":"x","chapter":null,"name":"Mara","changes":{"personality":"wry"},
                         "instructions":null,"originalText":null,"replacementText":null,"text":null,"option":"a"}]}
            """);

        Assert.NotNull(reply);
        Assert.Equal("Done.", reply!.Message);
        Assert.Equal(2, Assert.Single(reply.Conflicts).Chapter);
        var action = Assert.Single(reply.Actions);
        Assert.Equal(("upsert_character", "A"), (action.Type, action.Option));
        Assert.Equal("wry", Assert.Single(action.Changes).Value);
    }

    [Theory]
    [InlineData("just text, no JSON")]
    [InlineData("""{"actions":[]}""")]
    public void Parse_WithoutAMessageObject_IsNull(string raw) => Assert.Null(BookChatActions.Parse(raw));

    [Fact]
    public void Normalize_KeepsKnownFieldsOnly_AndMatchesExistingNamesCaseInsensitively()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [A(BookChatActions.UpsertCharacter, name: "mara", changes: [("PERSONALITY", "wry"), ("eyeColour", "green")])], State());

        var action = Assert.Single(actions);
        Assert.Empty(dropped);
        Assert.Equal("Mara", action.Payload.Name);
        Assert.Equal(new Dictionary<string, string> { ["personality"] = "wry" }, action.Payload.Changes);
        Assert.Equal("character:Mara", action.Target);
    }

    [Fact]
    public void Normalize_DropsBibleActions_WhenLocked()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [A(BookChatActions.UpdateStoryBible, changes: [("themes", "loss")]), A(BookChatActions.UpdatePremise, text: "New")],
            State(locked: true));

        Assert.Empty(actions);
        Assert.Equal(2, dropped.Count);
        Assert.All(dropped, d => Assert.Contains("locked", d));
        Assert.DoesNotContain(BookChatActions.UpdateStoryBible, BookChatActions.ReplySchema(bibleLocked: true));
        Assert.Contains(BookChatActions.UpdateStoryBible, BookChatActions.ReplySchema(bibleLocked: false));
    }

    [Fact]
    public void Normalize_AddChapter_AlwaysAppends_AndAWriteOfItDependsOnIt()
    {
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.AddChapter, chapter: 1, changes: [("title", "Ash"), ("outline", "Fire.")]),
                A(BookChatActions.WriteChapter, chapter: 3),
            ],
            State(false, new BookChatChapter(10, 1, "One", ""), new BookChatChapter(11, 2, "Two", "")));

        Assert.Empty(dropped);
        Assert.Equal(3, actions[0].Payload.ChapterNumber);
        Assert.Equal("Add Chapter 3: Ash", actions[0].Label);
        Assert.Null(actions[1].Payload.ChapterId);
        Assert.Equal(0, actions[1].DependsOnIndex);
    }

    [Fact]
    public void Normalize_ArchiveChapter_OnlyTheLastOne()
    {
        var state = State(false, new BookChatChapter(10, 1, "One", ""), new BookChatChapter(11, 2, "Two", ""));
        var (actions, dropped) = BookChatActions.Normalize(
            [A(BookChatActions.ArchiveChapter, chapter: 1), A(BookChatActions.ArchiveChapter, chapter: 2)], state);

        Assert.Equal(11, Assert.Single(actions).Payload.ChapterId);
        Assert.Contains("only the last chapter", Assert.Single(dropped));
    }

    [Fact]
    public void Normalize_Patch_NeedsTextThatIsInTheChapter()
    {
        var state = State(false, new BookChatChapter(10, 1, "One", "Mara had green eyes.\n\nShe ran."));
        var (actions, dropped) = BookChatActions.Normalize(
            [
                A(BookChatActions.PatchChapter, chapter: 1, original: "Mara had green eyes.", replacement: "Mara had blue eyes."),
                A(BookChatActions.PatchChapter, chapter: 1, original: "Not in the text.", replacement: "x"),
                A(BookChatActions.ReviseChapter, chapter: 1),
            ], state);

        Assert.Equal("prose:10", Assert.Single(actions).Target);
        Assert.Equal(2, dropped.Count);
    }

    [Fact]
    public void Normalize_UnknownTypes_ReferencesAndTheCap_AreDropped()
    {
        var many = Enumerable.Range(0, BookChatActions.MaxActions + 1)
            .Select(i => A(BookChatActions.AddNote, text: $"n{i}"))
            .Prepend(A("delete_book"))
            .Append(A(BookChatActions.ArchivePlotThread, name: "Nope"))
            .ToList();
        var (actions, dropped) = BookChatActions.Normalize(many, State());

        Assert.Equal(BookChatActions.MaxActions, actions.Count);
        Assert.Equal(3, dropped.Count);
    }

    [Fact]
    public void PatchPreview_ShowsOnlyTheTouchedParagraph()
    {
        var (before, after) = BookChatAgent.PatchPreview("First.\n\nMara had green eyes.\n\nLast.", "green eyes", "blue eyes");
        Assert.Equal("Mara had green eyes.", before);
        Assert.Equal("Mara had blue eyes.", after);
    }

    [Fact]
    public void History_ResendsRepliesAsTextAndOutcome_CutToTheLimit_AndOpensWithTheAuthor()
    {
        var reply = new BookChatMessage
        {
            Role = BookChatRole.Assistant,
            Content = "Added her.\n\n⚠️ _Left out — …_",
            Proposals = [new ChatProposal { Label = "Add Mara", Status = ChatProposalStatus.Applied, PreviewAfter = new string('x', 5000) }],
        };
        var (role, content) = BookChatAgent.Turn(reply);
        Assert.Equal(LlmChatRole.Assistant, role);
        Assert.Equal("Added her.\n[Proposed: Add Mara (applied)]", content);

        var longAuthor = BookChatAgent.Turn(new BookChatMessage { Role = BookChatRole.Author, Content = new string('a', 5000) });
        Assert.True(longAuthor.Content.Length <= PromptTemplates.BookChatHistoryMessageChars + 2);

        var messages = BookChatAgent.BuildMessages("sys", [(LlmChatRole.Assistant, "orphan"), (LlmChatRole.User, "hi")], "now");
        Assert.Equal([LlmChatRole.System, LlmChatRole.User], messages.Select(m => m.Role));
        Assert.Equal("hi\n\nnow", messages[1].Content);
    }

    [Fact]
    public void Digest_StaysWithinBudget_ShrinkingOldChaptersFirst()
    {
        var chapters = Enumerable.Range(1, 40)
            .Select(n => new Chapter { Number = n, Title = $"T{n}", Content = "x", Summary = new string('s', 400) })
            .ToList();
        var digest = PromptTemplates.BookChatDigest(new Book { Title = "B", Premise = "P" }, false, null, [], [], chapters, [], budget: 6000);

        Assert.True(digest.Length <= 6002);
        Assert.Contains("1. T1 [written]", digest);
        Assert.Contains("40. T40 [written] — sss", digest);
    }
}
