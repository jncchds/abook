using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ABook.Agents;

/// <summary>
/// Reads fields out of planning-agent JSON without trusting the model to honour the schema's types.
/// Local and OpenAI-compatible endpoints (LM Studio, Ollama, vLLM) routinely return a chapter number
/// as <c>1.0</c> or <c>"3"</c> and a text field as an array or a bare number; the strict
/// <see cref="JsonElement"/> accessors throw on all of those and lose the whole response.
/// </summary>
internal static partial class LenientJson
{
    /// <summary>Reads <paramref name="key"/> as text, coercing any value kind. Missing keys yield "".</summary>
    public static string Text(JsonElement el, string key) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(key, out var v) ? Text(v) : "";

    /// <summary>Coerces any value kind to text: arrays are flattened to a comma-separated list.</summary>
    public static string Text(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.String => v.GetString() ?? "",
        JsonValueKind.Null or JsonValueKind.Undefined => "",
        JsonValueKind.Array => string.Join(", ", v.EnumerateArray().Select(Text).Where(s => s.Length > 0)),
        _ => v.GetRawText(),
    };

    /// <summary>
    /// Reads <paramref name="key"/> as an <see cref="int"/>, accepting non-integral numbers
    /// (rounded), numeric strings, and strings with the number embedded in prose ("Chapter 3").
    /// Returns null when the value is missing or holds no recoverable number.
    /// </summary>
    public static int? Int(JsonElement el, string key)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.Number => v.TryGetInt32(out var i) ? i
                : v.TryGetDouble(out var d) ? FromDouble(d) : null,
            JsonValueKind.String => FromText(v.GetString()),
            _ => null,
        };
    }

    private static int? FromDouble(double d) =>
        double.IsFinite(d) && d >= int.MinValue && d <= int.MaxValue ? (int)Math.Round(d) : null;

    private static int? FromText(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) return i;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return FromDouble(d);
        var m = FirstNumber().Match(s);
        return m.Success && double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var e)
            ? FromDouble(e) : null;
    }

    [GeneratedRegex(@"-?\d+(\.\d+)?")]
    private static partial Regex FirstNumber();
}
