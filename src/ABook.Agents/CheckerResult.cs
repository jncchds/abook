namespace ABook.Agents;

/// <summary>
/// A single issue found by the Checker, with the type of problem, a description,
/// a human-readable proposed fix, and optional verbatim patch fields for mechanical application.
/// </summary>
public record CheckerIssue(
    /// <summary>"continuity" or "style"</summary>
    string Type,
    /// <summary>Human-readable description of the problem.</summary>
    string Description,
    /// <summary>Specific, actionable text change to apply (human-readable).</summary>
    string ProposedFix,
    /// <summary>
    /// Verbatim text from the chapter that must be replaced.
    /// Copy character-for-character from chapter content.
    /// Empty string if the fix is structural (insertion/omission rather than replacement).
    /// </summary>
    string OriginalText,
    /// <summary>
    /// Verbatim replacement text. Empty string when OriginalText is empty.
    /// </summary>
    string ReplacementText);

/// <summary>
/// Structured result from the Checker agent. Contains a unified list of issues
/// (each with a proposed fix) so the Editor can apply them surgically.
/// </summary>
public record CheckerResult(
    bool HasIssues,
    CheckerIssue[] Issues,
    string Summary);
