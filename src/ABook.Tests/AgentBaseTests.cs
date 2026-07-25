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
