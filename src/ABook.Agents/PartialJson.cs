using System.Text.Json;
using System.Text.RegularExpressions;

namespace ABook.Agents;

/// <summary>
/// Recovers usable JSON from a response that may have been cut short mid-stream.
/// Planning agents stream their JSON array to the UI token by token, so a provider timeout,
/// a dropped connection, or a user-pressed Stop leaves the author looking at content that the
/// backend would otherwise throw away. These helpers keep every array element that finished
/// streaming and discard only the trailing, incomplete one.
/// </summary>
internal static partial class PartialJson
{
    /// <summary>Maximum number of candidate '[' positions probed before giving up.</summary>
    private const int MaxArrayStartCandidates = 8;

    /// <summary>
    /// Returns a syntactically valid JSON array built from the top-level elements that are
    /// complete in <paramref name="raw"/>, or an empty string when nothing usable is present.
    /// A complete, well-formed array is returned unchanged.
    /// </summary>
    public static string SalvageArray(string raw) => FindArray(raw, completeOnly: false);

    /// <summary>
    /// True when <paramref name="raw"/> holds a complete, well-formed JSON array — i.e. the response
    /// was not cut off. A response that ends mid-array returns false even though
    /// <see cref="SalvageArray"/> can still recover elements from it.
    /// </summary>
    public static bool IsComplete(string raw) => FindArray(raw, completeOnly: true).Length > 0;

    /// <summary>
    /// Scans candidate '[' positions and returns the first that yields an array of objects. Bracketed
    /// prose ("the [requested] list") therefore never masks the real payload. An empty array is only
    /// returned when no candidate with content is found.
    /// </summary>
    private static string FindArray(string raw, bool completeOnly)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

        var text = StripNoise(raw);
        string? emptyArrayFallback = null;

        int searchFrom = 0;
        for (int attempt = 0; attempt < MaxArrayStartCandidates; attempt++)
        {
            var start = text.IndexOf('[', searchFrom);
            if (start < 0) break;
            searchFrom = start + 1;

            var candidate = BuildCandidate(text, start, completeOnly);
            if (candidate.Length == 0) continue;

            switch (Classify(candidate))
            {
                case ArrayShape.Objects: return candidate;
                case ArrayShape.Empty: emptyArrayFallback ??= candidate; break;
            }
        }

        return emptyArrayFallback ?? string.Empty;
    }

    private enum ArrayShape { NotAnObjectArray, Empty, Objects }

    private static ArrayShape Classify(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return ArrayShape.NotAnObjectArray;

            int count = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                if (el.ValueKind != JsonValueKind.Object) return ArrayShape.NotAnObjectArray;
                count++;
            }
            return count == 0 ? ArrayShape.Empty : ArrayShape.Objects;
        }
        catch (JsonException) { return ArrayShape.NotAnObjectArray; }
    }

    /// <summary>
    /// Walks the array starting at <paramref name="start"/> tracking bracket depth and string
    /// state. Returns the whole array when its closing bracket was reached, otherwise the
    /// elements that finished, re-closed with a ']'.
    /// </summary>
    private static string BuildCandidate(string text, int start, bool completeOnly = false)
    {
        int depth = 0;
        int lastElementEnd = -1;
        bool inString = false, escaped = false;

        for (int i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped) escaped = false;
                else if (ch == '\\') escaped = true;
                else if (ch == '"') inString = false;
                continue;
            }

            switch (ch)
            {
                case '"':
                    inString = true;
                    break;
                case '[':
                case '{':
                    depth++;
                    break;
                case ']':
                case '}':
                    depth--;
                    if (depth == 0) return text[start..(i + 1)];   // array closed — complete
                    if (depth == 1) lastElementEnd = i;            // one top-level element finished
                    break;
            }
        }

        if (completeOnly || lastElementEnd < 0) return string.Empty;
        return text[start..(lastElementEnd + 1)] + "]";
    }

    /// <summary>
    /// Removes reasoning tags and markdown fences. An unterminated &lt;think&gt; block means the
    /// stream died while the model was still reasoning, so everything from it on is dropped.
    /// </summary>
    private static string StripNoise(string raw)
    {
        var text = ClosedThinkTag().Replace(raw, string.Empty);

        var openThink = OpenThinkTag().Match(text);
        if (openThink.Success) text = text[..openThink.Index];

        text = text.Trim();
        if (text.StartsWith("```"))
            text = string.Join('\n', text.Split('\n').Skip(1).TakeWhile(l => !l.StartsWith("```")));

        return text;
    }

    [GeneratedRegex(@"<think(?:ing)?>.*?</think(?:ing)?>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex ClosedThinkTag();

    [GeneratedRegex(@"<think(?:ing)?>", RegexOptions.IgnoreCase)]
    private static partial Regex OpenThinkTag();
}
