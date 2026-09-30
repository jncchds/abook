using ABook.Agents;

namespace ABook.Tests;

public class InlineThinkSplitterTests
{
    private static (string Content, string Thinking, bool SawThinking) Run(params string[] chunks)
    {
        var splitter = new InlineThinkSplitter();
        var content = new System.Text.StringBuilder();
        var thinking = new System.Text.StringBuilder();
        foreach (var chunk in chunks)
        {
            var (c, t) = splitter.Feed(chunk);
            content.Append(c);
            thinking.Append(t);
        }
        var (fc, ft) = splitter.Flush();
        content.Append(fc);
        thinking.Append(ft);
        return (content.ToString(), thinking.ToString(), splitter.SawThinking);
    }

    [Fact]
    public void NoTags_PassesContentThrough()
    {
        var r = Run("Hello ", "world.");
        Assert.Equal("Hello world.", r.Content);
        Assert.Equal("", r.Thinking);
        Assert.False(r.SawThinking);
    }

    [Fact]
    public void ThinkBlockInSingleChunk_IsSplitOff()
    {
        var r = Run("<think>plan it</think>\n\nThe prose.");
        Assert.Equal("\n\nThe prose.", r.Content);
        Assert.Equal("plan it", r.Thinking);
        Assert.True(r.SawThinking);
    }

    [Fact]
    public void TagsSplitAcrossChunks_AreRecognised()
    {
        var r = Run("<thi", "nk>reason", "ing</th", "ink>{\"a\":1}");
        Assert.Equal("{\"a\":1}", r.Content);
        Assert.Equal("reasoning", r.Thinking);
    }

    [Fact]
    public void ThinkingTagVariant_CaseInsensitive()
    {
        var r = Run("<THINKING>x</Thinking>y");
        Assert.Equal("y", r.Content);
        Assert.Equal("x", r.Thinking);
    }

    [Fact]
    public void ContentReleasedPerChunk_BeforeHeldBackFragment()
    {
        var splitter = new InlineThinkSplitter();
        var (content, _) = splitter.Feed("Some text <th");
        Assert.Equal("Some text ", content);
        Assert.Equal(("<th", ""), splitter.Flush());
    }

    [Fact]
    public void LessThanThatIsNotATag_StaysInContent()
    {
        var r = Run("a < b and <b>bold</b>", " <3");
        Assert.Equal("a < b and <b>bold</b> <3", r.Content);
        Assert.Equal("", r.Thinking);
    }

    [Fact]
    public void UnterminatedThink_GoesToThinking()
    {
        var r = Run("<think>still reasoning when cut off {\"x\":");
        Assert.Equal("", r.Content);
        Assert.Equal("still reasoning when cut off {\"x\":", r.Thinking);
    }
}
