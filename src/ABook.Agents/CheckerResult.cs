namespace ABook.Agents;

/// <summary>
/// A single issue found by the Checker, with the type of problem, a description,
/// a human-readable proposed fix, and optional verbatim patch fields for mechanical application.
/// </summary>
public record CheckerIssue(
    /// <summary>"continuity", "grammar", "repetition", or "style"</summary>
    string Type,
    /// <summary>Human-readable description of the problem.</summary>
    string Description,
    /// <summary>Specific, actionable text change to apply (human-readable).</summary>
    string ProposedFix,
    /// <summary>
    /// Verbatim text from the chapter that must be replaced.
    /// Include at least 10–20 characters of surrounding context so the text is uniquely identifiable.
    /// Empty string if the fix is structural (insertion/omission rather than replacement).
    /// </summary>
    string OriginalText,
    /// <summary>
    /// Verbatim replacement text. Empty string when OriginalText is empty.
    /// </summary>
    string ReplacementText,
    /// <summary>
    /// 1-indexed line number in the chapter content where this occurrence lives.
    /// Used to disambiguate when OriginalText appears multiple times.
    /// </summary>
    int Position);

/// <summary>
/// Structured result from the Checker agent. Contains a unified list of issues
/// (each with a proposed fix) so the Editor can apply them surgically.
/// </summary>
public record CheckerResult(
    bool HasIssues,
    CheckerIssue[] Issues,
    string Summary);
