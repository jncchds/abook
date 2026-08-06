using ABook.Agents;
using ABook.Core.Models;

namespace ABook.Tests;

public class AgentBaseStripHeadingTests
{
    [Fact]
    public void Strip_HashNumberedChapterHeading_Removed()
    {
        var content = "## Chapter 3: The Storm\n\nProse starts here.";
        var result = AgentBase.StripLeadingChapterHeading(content, 3, "The Storm");
        Assert.StartsWith("Prose starts here.", result);
    }

    [Fact]
    public void Strip_WordFormChapterHeading_Removed()
    {
        var content = "Chapter Three\n\nProse starts here.";
        var result = AgentBase.StripLeadingChapterHeading(content, 3, "Any Title");
        Assert.StartsWith("Prose starts here.", result);
    }

    [Fact]
    public void Strip_TitleOnlyHeadingMatchingTitle_Removed()
    {
        var content = "The Storm\n\nProse starts here.";
        var result = AgentBase.StripLeadingChapterHeading(content, 3, "The Storm");
        Assert.StartsWith("Prose starts here.", result);
    }

    [Fact]
    public void Strip_NonHeadingFirstLine_Preserved()
    {
        var content = "Once upon a time the hero arrived.";
        var result = AgentBase.StripLeadingChapterHeading(content, 1, "The Beginning");
        Assert.StartsWith("Once upon a time", result);
    }

    [Fact]
    public void Strip_BlankLinesBeforeHeading_Consumed()
    {
        var content = "\n\n## Chapter 1: Dawn\n\nProse here.";
        var result = AgentBase.StripLeadingChapterHeading(content, 1, "Dawn");
        Assert.StartsWith("Prose here.", result);
    }

    [Fact]
    public void Strip_NumericChapterHeadingNoTitle_Removed()
    {
        var content = "## Chapter 5\n\nProse here.";
        var result = AgentBase.StripLeadingChapterHeading(content, 5, "Some Title");
        Assert.StartsWith("Prose here.", result);
    }
}

public class AgentBaseExtractJsonTests
{
    [Fact]
    public void ExtractJson_RawObject_ReturnsUnchanged()
    {
        var raw = "{\"key\": \"value\"}";
        var result = AgentBase.ExtractJson(raw, '{', '}');
        Assert.Equal(raw, result);
    }

    [Fact]
    public void ExtractJson_CodeFencedWithLanguage_Unwraps()
    {
        var raw = "```json\n{\"key\": \"value\"}\n```";
        var result = AgentBase.ExtractJson(raw, '{', '}');
        Assert.Equal("{\"key\": \"value\"}", result);
    }

    [Fact]
    public void ExtractJson_CodeFencedNoLanguage_Unwraps()
    {
        var raw = "```\n{\"key\": \"value\"}\n```";
        var result = AgentBase.ExtractJson(raw, '{', '}');
        Assert.Equal("{\"key\": \"value\"}", result);
    }

    [Fact]
    public void ExtractJson_ArrayForm_ExtractsWithBrackets()
    {
        var raw = "Here is the list:\n```json\n[1, 2, 3]\n```";
        var result = AgentBase.ExtractJson(raw, '[', ']');
        Assert.Equal("[1, 2, 3]", result);
    }

    [Fact]
    public void ExtractJson_PreambleBeforeJson_StillExtracts()
    {
        var raw = "Sure! Here is the JSON:\n{\"a\": 1}";
        var result = AgentBase.ExtractJson(raw, '{', '}');
        Assert.Equal("{\"a\": 1}", result);
    }
}

public class AgentBaseInterpolateTests
{
    private static Book MakeBook() => new()
    {
        Title = "My Book",
        Genre = "Fantasy",
        Premise = "A hero's journey",
        Language = "English",
        TargetChapterCount = 20
    };

    private static StoryBible MakeBible() => new()
    {
        SettingDescription = "A magical world",
        Themes = "courage, sacrifice",
        ToneAndStyle = "epic",
        WorldRules = "magic costs life force"
    };

    [Fact]
    public void Interpolate_BookPlaceholders_AllReplaced()
    {
        var prompt = "Title: {TITLE}, Genre: {GENRE}, Premise: {PREMISE}, Lang: {LANGUAGE}, Chapters: {CHAPTER_COUNT}";
        var result = AgentBase.InterpolateSystemPrompt(prompt, MakeBook());
        Assert.Contains("My Book", result);
        Assert.Contains("Fantasy", result);
        Assert.Contains("A hero's journey", result);
        Assert.Contains("English", result);
        Assert.Contains("20", result);
        Assert.DoesNotContain("{TITLE}", result);
    }

    [Fact]
    public void Interpolate_BiblePlaceholders_ReplacedWhenBibleProvided()
    {
        var prompt = "Setting: {SETTING}, Themes: {THEMES}, Tone: {TONE}, Rules: {WORLD_RULES}";
        var result = AgentBase.InterpolateSystemPrompt(prompt, MakeBook(), MakeBible());
        Assert.Contains("A magical world", result);
        Assert.Contains("courage, sacrifice", result);
        Assert.Contains("epic", result);
        Assert.Contains("magic costs life force", result);
    }

    [Fact]
    public void Interpolate_BiblePlaceholders_LeftWhenBibleNull()
    {
        var prompt = "Setting: {SETTING}";
        var result = AgentBase.InterpolateSystemPrompt(prompt, MakeBook(), bible: null);
        Assert.Contains("{SETTING}", result);
    }

    [Fact]
    public void Interpolate_ChapterSynopses_ReplacedWhenProvided()
    {
        var prompt = "Synopses: {CHAPTER_SYNOPSES}";
        var result = AgentBase.InterpolateSystemPrompt(prompt, MakeBook(), chapterSynopses: "1. Dawn — intro");
        Assert.Contains("1. Dawn — intro", result);
    }
}

public class AgentBaseDescribeFailureTests
{
    [Fact]
    public void Describe_UserCancellation_ReportsCancelled()
    {
        var result = AgentBase.DescribeFailure(new OperationCanceledException(), cancelledByUser: true);
        Assert.Equal("Cancelled by user", result);
    }

    [Fact]
    public void Describe_TimeoutWithoutUserCancellation_ReportsTimedOut()
    {
        // HttpClient.Timeout surfaces as TaskCanceledException while the agent's own token is untouched.
        var result = AgentBase.DescribeFailure(new TaskCanceledException(), cancelledByUser: false);
        Assert.Equal("Timed out", result);
    }

    [Fact]
    public void Describe_TimeoutWithConfiguredTimeout_IncludesDuration()
    {
        var result = AgentBase.DescribeFailure(new TaskCanceledException(), cancelledByUser: false, timeoutMs: 120_000);
        Assert.Equal("Timed out after 120000ms", result);
    }

    [Fact]
    public void Describe_HttpRequestException_IncludesStatusCode()
    {
        var ex = new HttpRequestException("Bad upstream", null, System.Net.HttpStatusCode.BadGateway);
        var result = AgentBase.DescribeFailure(ex, cancelledByUser: false);
        Assert.Contains("502", result);
        Assert.Contains("BadGateway", result);
        Assert.Contains("Bad upstream", result);
    }

    [Fact]
    public void Describe_GenericException_UsesTypeAndMessage()
    {
        var result = AgentBase.DescribeFailure(new InvalidOperationException("model not loaded"), cancelledByUser: false);
        Assert.Equal("InvalidOperationException: model not loaded", result);
    }

    [Fact]
    public void Describe_SingleAggregateException_UnwrapsInner()
    {
        var ex = new AggregateException(new InvalidOperationException("inner boom"));
        var result = AgentBase.DescribeFailure(ex, cancelledByUser: false);
        Assert.Equal("InvalidOperationException: inner boom", result);
    }

    [Fact]
    public void Describe_WrappedCancellation_StillReportsTimedOut()
    {
        var ex = new AggregateException(new TaskCanceledException());
        var result = AgentBase.DescribeFailure(ex, cancelledByUser: false);
        Assert.Equal("Timed out", result);
    }

    [Fact]
    public void Describe_VeryLongMessage_TruncatedToColumnLimit()
    {
        var result = AgentBase.DescribeFailure(new Exception(new string('x', 5000)), cancelledByUser: false);
        Assert.Equal(1000, result.Length);
        Assert.EndsWith("…", result);
    }
}
