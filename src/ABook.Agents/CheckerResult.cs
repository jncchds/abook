namespace ABook.Agents;

/// <summary>
/// Structured result from the Checker agent. Contains separate lists for
/// continuity and style issues so the Editor can target fixes precisely.
/// </summary>
public record CheckerResult(
    bool HasIssues,
    string[] ContinuityIssues,
    string[] StyleIssues,
    string Summary);
