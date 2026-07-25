using ABook.Agents;

namespace ABook.Tests;

public class CheckerReportTests
{
    private static CheckerIssue Issue(string type, string desc = "desc",
        string? location = null, string? problem = null, string? canonicalFact = null) =>
        new(type, desc, "proposed fix", "original", "replacement",
            Location: location, Problem: problem, CanonicalFact: canonicalFact);

    [Fact]
    public void FormatReport_NoIssues_ShowsCheckmark()
    {
        var result = new CheckerResult(false, [], "All good.");
        var report = ContinuityCheckerAgent.FormatCheckerReport(result);
        Assert.Contains("✅ No issues found.", report);
    }

    [Fact]
    public void FormatReport_HasIssues_ShowsWarning()
    {
        var result = new CheckerResult(true, [Issue("grammar")], "Found issues.");
        var report = ContinuityCheckerAgent.FormatCheckerReport(result);
        Assert.Contains("⚠️ Issues found", report);
        Assert.Contains("1 total", report);
    }

    [Fact]
    public void FormatReport_SummaryAppears()
    {
        var result = new CheckerResult(true, [Issue("grammar")], "Summary text here.");
        var report = ContinuityCheckerAgent.FormatCheckerReport(result);
        Assert.Contains("Summary text here.", report);
    }

    [Fact]
    public void FormatReport_IssuesGroupedByType()
    {
        var issues = new[]
        {
            Issue("continuity", "c1"),
            Issue("grammar", "g1"),
            Issue("repetition", "r1"),
            Issue("style", "s1"),
        };
        var result = new CheckerResult(true, issues, "");
        var report = ContinuityCheckerAgent.FormatCheckerReport(result);
        Assert.Contains("### Continuity", report);
        Assert.Contains("### Grammar", report);
        Assert.Contains("### Repetition", report);
        Assert.Contains("### Style", report);
    }

    [Fact]
    public void FormatReport_TypeOrder_ContinuityBeforeGrammar()
    {
        var issues = new[] { Issue("grammar"), Issue("continuity") };
        var result = new CheckerResult(true, issues, "");
        var report = ContinuityCheckerAgent.FormatCheckerReport(result);
        Assert.True(report.IndexOf("### Continuity", StringComparison.Ordinal)
                  < report.IndexOf("### Grammar", StringComparison.Ordinal));
    }

    [Fact]
    public void FormatReport_RewriteType_RendersLocationAndProblem()
    {
        var issue = Issue("rewrite", "needs rework",
            location: "Paragraph 3", problem: "contradicts timeline",
            canonicalFact: "event happened before chapter 1");
        var result = new CheckerResult(true, [issue], "");
        var report = ContinuityCheckerAgent.FormatCheckerReport(result);
        Assert.Contains("Paragraph 3", report);
        Assert.Contains("contradicts timeline", report);
        Assert.Contains("event happened before chapter 1", report);
    }
}
