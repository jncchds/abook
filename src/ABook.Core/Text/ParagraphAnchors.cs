using System.Text;

namespace ABook.Core.Text;

/// <summary>
/// Places illustrations between chapter paragraphs. A paragraph is a maximal run of non-blank lines —
/// the same rule the UI (<c>ChapterContent.tsx</c>), the exports and the illustration planner use, so an
/// image lands after the same paragraph everywhere.
/// </summary>
public static class ParagraphAnchors
{
    /// <summary>Splits markdown into paragraphs (blocks separated by blank or whitespace-only lines).</summary>
    public static IReadOnlyList<string> Split(string? markdown)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(markdown)) return result;

        var current = new List<string>();
        foreach (var raw in markdown.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                if (current.Count > 0) { result.Add(string.Join('\n', current)); current.Clear(); }
                continue;
            }
            current.Add(line);
        }
        if (current.Count > 0) result.Add(string.Join('\n', current));
        return result;
    }

    /// <summary>
    /// Returns the 0-based index of the paragraph an illustration follows: the paragraph containing
    /// <paramref name="anchorText"/> (nearest to the hint when it occurs more than once), otherwise
    /// the 1-based <paramref name="paragraphNumberHint"/> clamped into range. -1 when there are no paragraphs.
    /// </summary>
    public static int Resolve(IReadOnlyList<string> paragraphs, string? anchorText, int paragraphNumberHint)
    {
        if (paragraphs.Count == 0) return -1;
        var hintIndex = Math.Clamp(paragraphNumberHint - 1, 0, paragraphs.Count - 1);

        var needle = Normalize(anchorText);
        if (needle.Length == 0) return hintIndex;

        var best = -1;
        for (int i = 0; i < paragraphs.Count; i++)
        {
            if (!Normalize(paragraphs[i]).Contains(needle, StringComparison.Ordinal)) continue;
            if (best < 0 || Math.Abs(i - hintIndex) < Math.Abs(best - hintIndex)) best = i;
        }
        return best >= 0 ? best : hintIndex;
    }

    /// <summary>Lower-cased, markdown emphasis removed, typographic quotes/dashes folded, whitespace collapsed.</summary>
    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var sb = new StringBuilder(text.Length);
        var lastSpace = true;
        foreach (var ch in text)
        {
            var c = ch switch
            {
                '‘' or '’' or '‚' or '′' => '\'',
                '“' or '”' or '„' or '«' or '»' or '″' => '"',
                '–' or '—' or '―' => '-',
                '…' => '.',
                _ => ch,
            };
            if (c is '*' or '_' or '`' or '#' or '>') continue;
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) sb.Append(' ');
                lastSpace = true;
                continue;
            }
            sb.Append(char.ToLowerInvariant(c));
            lastSpace = false;
        }
        return sb.ToString().Trim();
    }
}
