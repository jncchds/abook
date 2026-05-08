namespace ABook.Agents;

/// <summary>
/// A single issue found by the Checker, with the type of problem, a description, and
/// a concrete proposed fix that the Editor should apply verbatim.
/// </summary>
public record CheckerIssue(
    /// <summary>"continuity" or "style"</summary>
    string Type,
    /// <summary>Human-readable description of the problem.</summary>
    string Description,
    /// <summary>Specific, actionable text change to apply.</summary>
    string ProposedFix);

/// <summary>
/// Structured result from the Checker agent. Contains a unified list of issues
/// (each with a proposed fix) so the Editor can apply them surgically.
/// </summary>
public record CheckerResult(
    bool HasIssues,
    CheckerIssue[] Issues,
    string Summary);
