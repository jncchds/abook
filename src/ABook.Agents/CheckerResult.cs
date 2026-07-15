namespace ABook.Agents;

/// <summary>
/// A single issue found by the Checker, with the type of problem, a description,
/// a human-readable proposed fix, and optional verbatim patch fields for mechanical application.
/// </summary>
public record CheckerIssue(
    /// <summary>"continuity", "grammar", "repetition", "style", or "rewrite"</summary>
    string Type,
    /// <summary>Human-readable description of the problem.</summary>
    string Description,
    /// <summary>
    /// For patchable issues: specific, actionable text change (human-readable).
    /// For rewrite issues: narrative instruction for how to resolve the inconsistency.
    /// </summary>
    string ProposedFix,
    /// <summary>
    /// Verbatim text from the chapter that must be replaced. Empty string for rewrite-type issues.
    /// Include at least 10–20 characters of surrounding context so the text is uniquely identifiable.
    /// </summary>
    string OriginalText,
    /// <summary>
    /// Verbatim replacement text. Empty string when OriginalText is empty or for rewrite-type issues.
    /// </summary>
    string ReplacementText,
    /// <summary>
    /// 1-indexed line number hint where this issue occurs (nullable — LLM-generated, may be inaccurate).
    /// Used as a positional hint for matching; falls back to full-text IndexOf if missing or wrong.
    /// </summary>
    int? Position = null,
    /// <summary>
    /// Paragraph/sentence range description for rewrite-type issues (e.g. "Paragraph 3–4").
    /// Used by the Editor to locate affected passages when verbatim text is unavailable.
    /// </summary>
    string? Location = null,
    /// <summary>
    /// Description of what's wrong — required for rewrite-type issues, optional otherwise.
    /// E.g. "jacket color contradicts between paragraphs 2 and 7" or "timeline impossible within scene".
    /// </summary>
    string? Problem = null,
    /// <summary>
    /// The correct/canonical state if determinable (e.g. "red coat per Character Card").
    /// Null or "choose consistently" if undeterminable from available context.
    /// </summary>
    string? CanonicalFact = null,
    /// <summary>
    /// Optional guidance for how to rewrite — the Editor may ignore this and use its own judgment.
    /// E.g. "change paragraph 7 mention of blue jacket to red coat" or empty if open-ended.
    /// </summary>
    string? SuggestedRewrite = null);

/// <summary>
/// Structured result from the Checker agent. Contains a unified list of issues
/// (each with a proposed fix) so the Editor can apply them surgically.
/// </summary>
public record CheckerResult(
    bool HasIssues,
    CheckerIssue[] Issues,
    string Summary);
