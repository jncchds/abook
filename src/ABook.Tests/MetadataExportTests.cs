using System.Text.RegularExpressions;
using ABook.Api.Services;
using ABook.Core.Models;

namespace ABook.Tests;

public class MetadataExportTests
{
    private static readonly DateTime T0 = new(2026, 9, 1, 10, 0, 0, DateTimeKind.Utc);

    private static Book BookWithChapter() => new()
    {
        Id = 1, Title = "Fog", Genre = "Mystery", Language = "English", CreatedAt = T0,
        Chapters =
        [
            new Chapter
            {
                Id = 10, Number = 1, Title = "Gate", Outline = "Mara reaches the gate.",
                Content = "Secret prose marker.", CreatedAt = T0.AddMinutes(5), UpdatedAt = T0.AddMinutes(5),
            },
        ],
    };

    private static int Pos(string html, string text)
    {
        var i = html.IndexOf(text, StringComparison.Ordinal);
        Assert.True(i >= 0, $"\"{text}\" not found");
        return i;
    }

    [Fact]
    public void Generate_OrdersEventsChronologicallyAcrossSources()
    {
        var html = MetadataExport.Generate(new MetadataSources
        {
            Book = BookWithChapter(),
            Messages =
            [
                new AgentMessage { AgentRole = AgentRole.Writer, MessageType = MessageType.Question, Content = "Which gate?", CreatedAt = T0.AddMinutes(20), ChapterId = 10 },
                new AgentMessage { AgentRole = AgentRole.Writer, MessageType = MessageType.Answer, Content = "The north one.", CreatedAt = T0.AddMinutes(21), ChapterId = 10, IsResolved = true },
            ],
            TokenUsage =
            [
                new TokenUsageRecord { AgentRole = AgentRole.Writer, PromptTokens = 1234, CompletionTokens = 56, ModelName = "qwen-test", CreatedAt = T0.AddMinutes(30) },
                new TokenUsageRecord { AgentRole = AgentRole.WorkflowStep, StepLabel = "Writing chapter 1", CreatedAt = T0.AddMinutes(10) },
            ],
            ChapterVersions =
            [
                new ChapterVersion { ChapterId = 10, VersionNumber = 1, Title = "Gate", Outline = "Mara reaches the gate.", Content = "Secret prose marker.", CreatedBy = "agent:Writer", IsActive = true, CreatedAt = T0.AddMinutes(25) },
            ],
        });

        var created  = Pos(html, "Book created");
        var outlined = Pos(html, "Chapter 1 outlined");
        var step     = Pos(html, "Writing chapter 1");
        var question = Pos(html, "Which gate?");
        var answer   = Pos(html, "The north one.");
        var drafted  = Pos(html, "Chapter 1 drafted");
        var call     = Pos(html, "1,234 in");
        Assert.True(created < outlined && outlined < step && step < question && question < answer && answer < drafted && drafted < call);
        Assert.Contains("qwen-test", html);
    }

    [Fact]
    public void Generate_LeavesOutProseAndInBookImages()
    {
        var html = MetadataExport.Generate(new MetadataSources
        {
            Book = BookWithChapter(),
            ChapterVersions =
            [
                new ChapterVersion { ChapterId = 10, VersionNumber = 1, Title = "Gate", Content = "Secret prose marker.", CreatedBy = "agent:Writer", CreatedAt = T0.AddMinutes(25) },
            ],
            Illustrations =
            [
                new Illustration { Id = 5, ChapterId = 10, Kind = IllustrationKind.Scene, Prompt = "a foggy gate", ImageId = 7, Status = IllustrationStatus.Rendered, CreatedAt = T0.AddMinutes(40), UpdatedAt = T0.AddMinutes(40) },
            ],
            Images = new Dictionary<int, BookImage> { [7] = new() { Id = 7, Data = [1, 2, 3], ContentType = "image/png", Width = 64, Height = 32, CreatedAt = T0.AddMinutes(41) } },
        });

        Assert.DoesNotContain("Secret prose marker.", html);
        Assert.DoesNotContain("data:image/png;base64,", html);
        Assert.Contains("a foggy gate", html);
        Assert.Contains("Scene rendered", html);
    }

    [Fact]
    public void Generate_ShowsClarificationQuestionsAnswersAndSkips_InOrder()
    {
        var html = MetadataExport.Generate(new MetadataSources
        {
            Book = BookWithChapter(),
            Clarifications =
            [
                new Clarification { Source = ClarificationSource.Planner, Question = "Who narrates?", Answer = "His daughter.",
                                    CreatedAt = T0.AddMinutes(1), AnsweredAt = T0.AddMinutes(3), UpdatedAt = T0.AddMinutes(3) },
                new Clarification { Source = ClarificationSource.Planner, Question = "Any magic?", IsArchived = true,
                                    CreatedAt = T0.AddMinutes(1), UpdatedAt = T0.AddMinutes(4) },
                new Clarification { Source = ClarificationSource.Imported, Question = "Old chat question", Answer = "Old answer",
                                    CreatedAt = T0.AddMinutes(2), AnsweredAt = T0.AddMinutes(2).AddSeconds(30), UpdatedAt = T0.AddMinutes(2) },
            ],
            // The chat messages the imported clarification was created from (same timestamps).
            Messages =
            [
                new AgentMessage { Id = 1, AgentRole = AgentRole.StoryBibleAgent, MessageType = MessageType.Question,
                                   Content = "Old chat question", IsResolved = true, CreatedAt = T0.AddMinutes(2) },
                new AgentMessage { Id = 2, AgentRole = AgentRole.StoryBibleAgent, MessageType = MessageType.Answer,
                                   Content = "Old answer", IsResolved = true, CreatedAt = T0.AddMinutes(2).AddSeconds(30) },
            ],
        });

        // Each question is one entry holding its outcome, in the order the questions were asked.
        var narrates = Pos(html, "Who narrates?");
        Assert.True(narrates < Pos(html, "Author answered") && Pos(html, "Author answered") < Pos(html, "His daughter."));
        Assert.True(Pos(html, "Any magic?") < Pos(html, "Author skipped it"));
        Assert.True(narrates < Pos(html, "Old chat question"));
        Assert.DoesNotContain("Answered the planner", html);

        // The imported clarification replaces its chat question and answer instead of repeating them,
        // and keeps the agent that asked it.
        Assert.Single(Regex.Matches(html, "Old chat question"));
        Assert.DoesNotContain("Answered the Story Bible agent", html);
        Assert.Contains("<span class=\"actor\">Story Bible</span>", html);
    }

    [Fact]
    public void Generate_ReconstructsAuthorBibleEditFromLaterState()
    {
        StoryBibleSnapshot Snap(string setting, string reason, int minutes) =>
            new() { SettingDescription = setting, Reason = reason, CreatedAt = T0.AddMinutes(minutes) };

        var html = MetadataExport.Generate(new MetadataSources
        {
            Book = BookWithChapter(),
            // Generated "Harbour"; the author's edit at +10 snapshots the old text, the new text is the current bible.
            BibleSnapshots = [Snap("Harbour", "agent-generated", 1), Snap("Harbour", "user-update", 10)],
            Bible = new StoryBible { SettingDescription = "Foggy harbour", CreatedAt = T0.AddMinutes(10), UpdatedAt = T0.AddMinutes(10) },
        });

        Assert.True(Pos(html, "Story bible generated") < Pos(html, "Story bible edited"));
        Assert.Contains("<ins>Foggy harbour</ins>", html.Replace("\n", ""));
        Assert.DoesNotContain("as it stood before", html);
    }
}
