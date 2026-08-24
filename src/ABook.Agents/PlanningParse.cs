using System.Text.Json;

namespace ABook.Agents;

/// <summary>
/// Turns a planning-agent parse failure into something the author can act on.
/// A bare <see cref="FormatException"/> from <c>System.Text.Json</c> reaches the chat as
/// "One of the identified items was in an invalid format", which names neither the field
/// nor the response that produced it — useless when a local endpoint is quietly ignoring
/// the schema. Every message built here quotes what the model actually returned.
/// </summary>
internal static class PlanningParse
{
    /// <summary>How much of a response or element is quoted back before it is elided.</summary>
    private const int SnippetLength = 300;

    /// <summary>Opens the JSON array in <paramref name="raw"/>, salvaging a truncated tail.</summary>
    public static JsonDocument OpenArray(string raw, string what) =>
        Open(PartialJson.SalvageArray(raw), raw, what);

    /// <summary>Opens the JSON object in <paramref name="raw"/>, tolerating prose and code fences.</summary>
    public static JsonDocument OpenObject(string raw, string what)
    {
        var doc = Open(AgentBase.ExtractJson(raw, '{', '}'), raw, what);
        if (doc.RootElement.ValueKind == JsonValueKind.Object) return doc;
        var kind = doc.RootElement.ValueKind;
        doc.Dispose();
        throw new FormatException(
            $"{what} response was a JSON {kind.ToString().ToLowerInvariant()}, not an object. {Describe(raw)}");
    }

    private static JsonDocument Open(string json, string raw, string what)
    {
        if (string.IsNullOrWhiteSpace(json))
            throw new FormatException($"{what} response contained no JSON data. {Describe(raw)}");
        try { return JsonDocument.Parse(json); }
        catch (JsonException ex)
        {
            throw new FormatException($"{what} response was not valid JSON — {ex.Message} {Describe(raw)}", ex);
        }
    }

    /// <summary>Quotes what the model actually returned, so a failure names its real cause.</summary>
    public static string Describe(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "The model returned an empty response.";
        var text = raw.Trim();
        return $"The model returned {text.Length} characters starting: \"{Snippet(text)}\"";
    }

    /// <summary>One line naming a dropped element, why it was dropped, and the JSON behind it.</summary>
    public static string SkipNote(string what, int index, JsonElement el, string reason) =>
        $"{what} #{index} skipped — {reason}: {Snippet(el.GetRawText())}";

    /// <inheritdoc cref="SkipNote(string,int,JsonElement,string)"/>
    public static string SkipNote(string what, int index, JsonElement el, Exception ex) =>
        SkipNote(what, index, el, $"{ex.GetType().Name}: {ex.Message.TrimEnd('.')}");

    /// <summary>
    /// Explains an all-elements-rejected outcome by listing why each was rejected, so the author
    /// sees the schema mismatch rather than an empty-result message.
    /// </summary>
    public static string SkipSummary(IReadOnlyCollection<string> skipped, int total, string raw)
    {
        if (skipped.Count == 0)
            return total == 0
                ? $"The response held an empty list. {Describe(raw)}"
                : Describe(raw);
        return $"All {total} returned entries were rejected:{Environment.NewLine}"
            + string.Join(Environment.NewLine, skipped.Select(s => $"• {s}"));
    }

    /// <summary>Reports the entries that were dropped while the rest of the run succeeded.</summary>
    public static string KeptSummary(IReadOnlyCollection<string> skipped, int kept, string what) =>
        $"Kept {kept} {what}; dropped {skipped.Count} entry/entries the model returned in an unusable shape:"
        + Environment.NewLine
        + string.Join(Environment.NewLine, skipped.Select(s => $"• {s}"));

    /// <summary>Collapses whitespace and truncates, so a snippet stays one readable line.</summary>
    private static string Snippet(string text)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= SnippetLength ? collapsed : collapsed[..SnippetLength] + "…";
    }
}
