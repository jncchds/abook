using ABook.Agents;

namespace ABook.Tests;

public class EditorAgentNormalizeTests
{
    [Fact]
    public void NormalizeWhitespace_TrimsTrailingSpacesAndCr()
    {
        var input = "line one   \r\nline two\t  \nline three";
        var result = EditorAgent.NormalizeWhitespace(input);
        var lines = result.Split('\n');
        Assert.Equal("line one", lines[0]);
        Assert.Equal("line two", lines[1]);
        Assert.Equal("line three", lines[2]);
    }

    [Fact]
    public void NormalizeWhitespace_EmptyString_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, EditorAgent.NormalizeWhitespace(string.Empty));
    }

    [Fact]
    public void NormalizeWhitespace_PreservesLeadingIndent()
    {
        var result = EditorAgent.NormalizeWhitespace("  indented line  ");
        Assert.Equal("  indented line", result);
    }

    [Fact]
    public void NormalizeQuotes_CurlySingleQuotes_ReplacedWithStraight()
    {
        var result = EditorAgent.NormalizeQuotes("‘hello’");
        Assert.Equal("'hello'", result);
    }

    [Fact]
    public void NormalizeQuotes_CurlyDoubleQuotes_ReplacedWithStraight()
    {
        var result = EditorAgent.NormalizeQuotes("“hello”");
        Assert.Equal("\"hello\"", result);
    }

    [Fact]
    public void NormalizeQuotes_StraightQuotes_Unchanged()
    {
        var input = "'hello' and \"world\"";
        Assert.Equal(input, EditorAgent.NormalizeQuotes(input));
    }
}

public class EditorAgentPatchLocationTests
{
    private static CheckerIssue Issue(string original, int? position = null) =>
        new("grammar", "fix", "proposed fix", original, "X", position);

    [Fact]
    public void FindPatchLocation_ExactSingleMatch_ReturnsCorrectOffset()
    {
        var content = "The quick brown fox jumps over the lazy dog";
        var match = EditorAgent.FindPatchLocation(content, Issue("brown fox"));
        Assert.Equal(content.IndexOf("brown fox", StringComparison.Ordinal), match.Offset);
    }

    [Fact]
    public void FindPatchLocation_NoMatch_ReturnsNegativeOffset()
    {
        var match = EditorAgent.FindPatchLocation("The quick brown fox", Issue("purple elephant"));
        Assert.True(match.Offset < 0);
    }

    [Fact]
    public void FindPatchLocation_EmptyOriginal_ReturnsSkipped()
    {
        var match = EditorAgent.FindPatchLocation("Some content here", Issue(string.Empty));
        Assert.True(match.Offset < 0);
    }

    [Fact]
    public void FindPatchLocation_CurlyQuoteMatchFallback_Succeeds()
    {
        // Content has straight quotes; original uses curly quotes → NormalizeQuotes fallback
        var content = "She said \"hello\" to him";
        var match = EditorAgent.FindPatchLocation(content, Issue("“hello”"));
        Assert.True(match.Offset >= 0);
    }

    [Fact]
    public void FindPatchLocation_MultipleMatches_NoHint_Skipped()
    {
        // Multiple identical occurrences with no position hint → cannot disambiguate
        var match = EditorAgent.FindPatchLocation("cat sat on the mat, cat sat", Issue("cat sat"));
        Assert.True(match.Offset < 0);
    }

    [Fact]
    public void FindPatchLocation_MultipleMatches_WithHint_ResolvesMatch()
    {
        // Needle at line 1 and line 20; hint=20 puts line 1 outside the ±5 window,
        // so FindInLineWindow finds only the line-20 occurrence.
        var fillerLines = string.Join("\n", Enumerable.Range(2, 18).Select(i => $"line{i}"));
        var content = $"target text\n{fillerLines}\ntarget text";
        var lineCount = content.Split('\n').Length; // should be 20
        var match = EditorAgent.FindPatchLocation(content, Issue("target text", position: lineCount));
        Assert.True(match.Offset >= 0);
        // The resolved offset should point to the LAST occurrence (near line 20), not the first (line 1)
        Assert.Equal(content.LastIndexOf("target text", StringComparison.Ordinal), match.Offset);
    }

    [Fact]
    public void FindInLineWindow_NeedleWithinWindow_ReturnsOffset()
    {
        var lines = new[] { "line1", "line2", "line3", "needle here", "line5" };
        var haystack = string.Join('\n', lines);
        var idx = EditorAgent.FindInLineWindow(lines, "needle here", lineNumber: 5, window: 5);
        Assert.Equal(haystack.IndexOf("needle here", StringComparison.Ordinal), idx);
    }

    [Fact]
    public void FindInLineWindow_NeedleOutsideWindow_ReturnsNegative()
    {
        var lines = new[] { "needle here", "l2", "l3", "l4", "l5", "l6", "l7", "l8", "l9", "l10" };
        var idx = EditorAgent.FindInLineWindow(lines, "needle here", lineNumber: 10, window: 2);
        Assert.True(idx < 0);
    }

    [Fact]
    public void GetLineStartOffset_FirstLine_ReturnsZero()
    {
        var lines = new[] { "alpha", "beta", "gamma" };
        Assert.Equal(0, EditorAgent.GetLineStartOffset(lines, 1));
    }

    [Fact]
    public void GetLineStartOffset_SecondLine_EqualsFirstLineLengthPlusNewline()
    {
        var lines = new[] { "alpha", "beta", "gamma" };
        Assert.Equal("alpha".Length + 1, EditorAgent.GetLineStartOffset(lines, 2));
    }

    [Fact]
    public void CountOccurrences_NoMatch_ReturnsZero()
    {
        Assert.Equal(0, EditorAgent.CountOccurrences("hello world", "xyz"));
    }

    [Fact]
    public void CountOccurrences_MultipleNonOverlapping_ReturnsCorrectCount()
    {
        Assert.Equal(3, EditorAgent.CountOccurrences("cat bat cat bat cat", "cat"));
    }
}

public class EditorAgentFeedbackTests
{
    private static CheckerIssue MakeIssue(string type) =>
        new(type, $"fix {type}", "proposed", "old text", "new text");

    [Fact]
    public void BuildEditorialFeedback_NoPatches_HeaderOnly()
    {
        var text = EditorAgent.BuildEditorialFeedback([], []).ToString();
        Assert.Contains("0 patch(es) applied", text);
        Assert.DoesNotContain("###", text);
    }

    [Fact]
    public void BuildEditorialFeedback_GroupsByType()
    {
        var applied = new[] { MakeIssue("grammar"), MakeIssue("continuity") };
        var text = EditorAgent.BuildEditorialFeedback(applied, []).ToString();
        Assert.Contains("### Continuity", text);
        Assert.Contains("### Grammar", text);
    }

    [Fact]
    public void BuildEditorialFeedback_SkippedSection_AppearsWhenPresent()
    {
        var skipped = new[] { (MakeIssue("style"), "text not found") };
        var text = EditorAgent.BuildEditorialFeedback([], skipped).ToString();
        Assert.Contains("Could not apply", text);
        Assert.Contains("text not found", text);
    }

    [Fact]
    public void BuildEditorialFeedback_CorrectTotalCount()
    {
        var applied = new[] { MakeIssue("grammar"), MakeIssue("grammar"), MakeIssue("style") };
        var text = EditorAgent.BuildEditorialFeedback(applied, []).ToString();
        Assert.Contains("3 patch(es) applied", text);
    }

    [Fact]
    public void BuildEditorialFeedback_SkippedCountInHeader()
    {
        var skipped = new[] { (MakeIssue("continuity"), "reason") };
        var text = EditorAgent.BuildEditorialFeedback([], skipped).ToString();
        Assert.Contains("1 skipped", text);
    }
}
