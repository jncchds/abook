using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

public class SectionChatTests
{
    private const string Marker = PromptTemplates.SectionChatUpdateMarker;

    [Fact]
    public void Split_NoMarker_IsReplyOnly()
    {
        var (reply, update) = SectionChatAgent.SplitResponse("  The hero is 30.  ");
        Assert.Equal("The hero is 30.", reply);
        Assert.Null(update);
    }

    [Fact]
    public void Split_Marker_SeparatesReplyAndUpdate()
    {
        var (reply, update) = SectionChatAgent.SplitResponse($"Made him older.\n{Marker}\n[{{\"name\":\"Ann\"}}]\n");
        Assert.Equal("Made him older.", reply);
        Assert.Equal("[{\"name\":\"Ann\"}]", update);
    }

    [Fact]
    public void Split_FencedUpdate_FencesRemoved()
    {
        var (_, update) = SectionChatAgent.SplitResponse($"Done.\n{Marker}\n```json\n{{\"a\":1}}\n```");
        Assert.Equal("{\"a\":1}", update);
    }

    [Fact]
    public void Split_MarkerInsideALine_IsNotAnUpdate()
    {
        var (reply, update) = SectionChatAgent.SplitResponse($"I would write {Marker} only when changing it.");
        Assert.Null(update);
        Assert.Contains(Marker, reply);
    }

    [Fact]
    public void Split_EmptyUpdate_IsNoUpdate()
    {
        var (_, update) = SectionChatAgent.SplitResponse($"Hmm.\n{Marker}\n   ");
        Assert.Null(update);
    }

    private static AgentMessage Msg(int id, MessageType type, string content) =>
        new() { Id = id, MessageType = type, Content = content, QuestionMessageId = 1 };

    [Fact]
    public void Build_FirstTurn_CarriesTheSection()
    {
        var messages = SectionChatAgent.BuildMessages("sys", "Story Bible", "ORIGINAL", "ORIGINAL", [], "Darker tone?");
        Assert.Equal(2, messages.Count);
        Assert.Equal(LlmChatRole.System, messages[0].Role);
        Assert.Equal(LlmChatRole.User, messages[1].Role);
        Assert.Contains("ORIGINAL", messages[1].Content);
        Assert.EndsWith("Darker tone?", messages[1].Content.TrimEnd());
    }

    [Fact]
    public void Build_LaterTurn_KeepsOriginalFirstAndAddsCurrentLast()
    {
        var history = new[]
        {
            Msg(2, MessageType.ChatUser, "Darker tone?"),
            Msg(3, MessageType.ChatReply, "Done."),
        };
        var messages = SectionChatAgent.BuildMessages("sys", "Story Bible", "ORIGINAL", "CURRENT", history, "Thanks, more?");

        Assert.Equal([LlmChatRole.System, LlmChatRole.User, LlmChatRole.Assistant, LlmChatRole.User],
            messages.Select(m => m.Role));
        Assert.Contains("ORIGINAL", messages[1].Content);
        Assert.DoesNotContain("CURRENT", messages[1].Content);
        Assert.Contains("CURRENT", messages[3].Content);
        Assert.EndsWith("Thanks, more?", messages[3].Content.TrimEnd());
    }

    [Fact]
    public void Build_UnchangedSection_IsNotRepeated()
    {
        var history = new[] { Msg(2, MessageType.ChatUser, "Why?"), Msg(3, MessageType.ChatReply, "Because.") };
        var messages = SectionChatAgent.BuildMessages("sys", "Story Bible", "SAME", "SAME", history, "Ok");
        Assert.Equal("Ok", messages[^1].Content);
    }

    [Fact]
    public void Build_FailedReplyAndOrphanUserTurn_AreMergedNotRepeatedRoles()
    {
        var history = new[]
        {
            Msg(2, MessageType.ChatUser, "First"),
            Msg(3, MessageType.ChatReply, "❌ The LLM call failed: timeout"),
        };
        var messages = SectionChatAgent.BuildMessages("sys", "Story Bible", "S", "S", history, "Second");

        Assert.Equal(2, messages.Count);
        Assert.Contains("First", messages[1].Content);
        Assert.Contains("Second", messages[1].Content);
        Assert.DoesNotContain("failed", messages[1].Content);
    }

    [Theory]
    [InlineData(AgentRole.StoryBibleAgent, null, ChatSection.StoryBible)]
    [InlineData(AgentRole.CharactersAgent, null, ChatSection.Characters)]
    [InlineData(AgentRole.PlotThreadsAgent, null, ChatSection.PlotThreads)]
    [InlineData(AgentRole.Planner, null, ChatSection.ChapterOutlines)]
    [InlineData(AgentRole.Editor, 7, ChatSection.Chapter)]
    [InlineData(AgentRole.Writer, 7, ChatSection.ChapterOutline)]
    public void FromPause_MapsHumanAssistedPauses(AgentRole role, int? chapterId, ChatSection expected)
    {
        var target = SectionChatTarget.FromPause(new AgentMessage
        {
            MessageType = MessageType.Question, IsOptional = true, AgentRole = role, ChapterId = chapterId,
        });
        Assert.NotNull(target);
        Assert.Equal(expected, target!.Section);
        Assert.Equal(chapterId, target.ChapterId);
    }

    [Fact]
    public void Change_RoundTripsThroughItsMessage()
    {
        var json = new SectionChatChange(0, 42, "before\n\"quoted\"", "after").ToJson();
        var change = SectionChatChange.FromMessage(new AgentMessage { Id = 9, MessageType = MessageType.ChatChange, Content = json });
        Assert.Equal(new SectionChatChange(9, 42, "before\n\"quoted\"", "after"), change);
    }

    [Fact]
    public void Change_UnreadableMessage_IsNull() =>
        Assert.Null(SectionChatChange.FromMessage(new AgentMessage { Content = "not json" }));

    [Theory]
    [InlineData("NONE", true)]
    [InlineData("none.", true)]
    [InlineData("**NONE**", true)]
    [InlineData("", true)]
    [InlineData("- Keep the tone dry.", false)]
    public void Summary_NothingToKeep(string note, bool expected) =>
        Assert.Equal(expected, SectionChatAgent.IsNothingToKeep(note));

    [Fact]
    public void Returning_RestoresLatestArchivedRowOfReturningNamesOnly()
    {
        var restored = SectionChatAgent.Returning(
            incoming: ["Ann", "bob", "Cleo", "Dan"],
            active: ["Ann"],
            archived: [(1, "Bob"), (5, "Bob"), (3, "Ann"), (7, "Eve")]).ToList();
        Assert.Equal([5], restored);   // Ann is active, Cleo/Dan are new, Eve is not returning
    }

    [Fact]
    public void FromPause_MandatoryOrOtherQuestions_AreNotSectionPauses()
    {
        Assert.Null(SectionChatTarget.FromPause(new AgentMessage
            { MessageType = MessageType.Question, IsOptional = false, AgentRole = AgentRole.StoryBibleAgent }));
        Assert.Null(SectionChatTarget.FromPause(new AgentMessage
            { MessageType = MessageType.Answer, IsOptional = true, AgentRole = AgentRole.StoryBibleAgent }));
        Assert.Null(SectionChatTarget.FromPause(new AgentMessage
            { MessageType = MessageType.Question, IsOptional = true, AgentRole = AgentRole.Editor, ChapterId = null }));
    }
}
