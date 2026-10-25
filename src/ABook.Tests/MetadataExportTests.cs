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

    /// <summary>The timeline part of the export (the current-state sections before it repeat what is current).</summary>
    private static string Timeline(string html) => html[Pos(html, "id=\"timeline\"")..];

    private static int Pos(string html, string text)
    {
        var i = html.IndexOf(text, StringComparison.Ordinal);
        Assert.True(i >= 0, $"\"{text}\" not found");
        return i;
    }

    [Fact]
    public void Generate_ShowsEachSectionsCurrentState_BeforeTheTimeline()
    {
        var html = MetadataExport.Generate(new MetadataSources
        {
            Book = BookWithChapter(),
            Bible = new StoryBible { SettingDescription = "A drowned city." },
            Characters = [new CharacterCard { Id = 1, Name = "Mara", Role = CharacterRole.Protagonist, Personality = "Stubborn." },
                          new CharacterCard { Id = 2, Name = "Gone", IsArchived = true }],
            PlotThreads = [new PlotThread { Id = 1, Name = "The flood", Description = "Water rises." }],
            Clarifications = [new Clarification { Source = ClarificationSource.Author, Question = "Tone?", Answer = "Bleak.", CreatedAt = T0 }],
            // Book info: the state before the author's edits, then the state they saved.
            BookSnapshots =
            [
                new BookSnapshot { Title = "Old title", Premise = "p", Reason = "before-edit", CreatedAt = T0.AddMinutes(1) },
                new BookSnapshot { Title = "New title", Premise = "p", Reason = EditSources.UserEdit, CreatedAt = T0.AddMinutes(5) },
            ],
        });

        var timeline = Pos(html, "id=\"timeline\"");
        var order = new[] { "id=\"story-bible\"", "A drowned city.", "id=\"clarifications\"", "Bleak.", "id=\"characters\"", "Stubborn.",
                            "Archived · 1", "id=\"plot-threads\"", "Water rises.", "id=\"chapters\"" }.Select(t => Pos(html, t)).ToList();
        Assert.Equal(order.OrderBy(p => p), order);
        Assert.True(order[^1] < timeline);
        // The edit is filed when the author saved it.
        Assert.Contains("10:05:00</time><span class=\"actor\">Author</span><span class=\"title\">Book details edited", html);
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

        // The current state lists them before the timeline.
        Assert.True(Pos(html, "His daughter.") < Pos(html, "id=\"timeline\""));
        html = Timeline(html);

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
    public void Generate_MergesOnlyLinkedQuestionsAndAnswers()
    {
        var html = MetadataExport.Generate(new MetadataSources
        {
            Book = BookWithChapter(),
            // A phase note saved with a link to its chat question.
            Clarifications =
            [
                new Clarification { Source = ClarificationSource.PhaseNote, Question = "Anything to add after the Story Bible?",
                                    Answer = "More rain.", QuestionMessageId = 1,
                                    CreatedAt = T0.AddMinutes(3), AnsweredAt = T0.AddMinutes(3), UpdatedAt = T0.AddMinutes(3) },
            ],
            Messages =
            [
                new AgentMessage { Id = 1, AgentRole = AgentRole.StoryBibleAgent, MessageType = MessageType.Question, IsOptional = true,
                                   Content = "Story Bible is ready — anything to add?", IsResolved = true, CreatedAt = T0.AddMinutes(1) },
                new AgentMessage { Id = 2, AgentRole = AgentRole.StoryBibleAgent, MessageType = MessageType.Answer, QuestionMessageId = 1,
                                   Content = "More rain.", IsResolved = true, CreatedAt = T0.AddMinutes(2) },
                // A linked chapter pause (no clarification).
                new AgentMessage { Id = 3, AgentRole = AgentRole.Editor, MessageType = MessageType.Question, IsOptional = true, ChapterId = 10,
                                   Content = "Any instructions for the rewrite?", IsResolved = true, CreatedAt = T0.AddMinutes(10) },
                new AgentMessage { Id = 4, AgentRole = AgentRole.Editor, MessageType = MessageType.Answer, QuestionMessageId = 3, ChapterId = 10,
                                   Content = "Shorter dialogue.", IsResolved = true, CreatedAt = T0.AddMinutes(11) },
                // An old answer without a link stays its own entry.
                new AgentMessage { Id = 5, AgentRole = AgentRole.Writer, MessageType = MessageType.Question, ChapterId = 10,
                                   Content = "Which gate?", IsResolved = true, CreatedAt = T0.AddMinutes(20) },
                new AgentMessage { Id = 6, AgentRole = AgentRole.Writer, MessageType = MessageType.Answer, ChapterId = 10,
                                   Content = "The north one.", IsResolved = true, CreatedAt = T0.AddMinutes(21) },
            ],
        });

        html = Timeline(html);
        // The phase note replaces its chat messages: filed at the question's time, by the asking agent.
        Assert.DoesNotContain("Story Bible is ready", html);
        Assert.Single(Regex.Matches(html, "More rain\\."));
        Assert.True(Pos(html, "Anything to add after the Story Bible?") < Pos(html, "More rain."));

        // The linked chapter pause is one entry; the unlinked pair is two.
        Assert.True(Pos(html, "Any instructions for the rewrite?") < Pos(html, "Shorter dialogue."));
        Assert.DoesNotContain("Answered the Editor agent", html);
        Assert.Contains("Answered the Writer agent", html);
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
