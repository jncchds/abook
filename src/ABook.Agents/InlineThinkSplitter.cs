using System.Text;

namespace ABook.Agents;

/// <summary>
/// Separates inline reasoning (<c>&lt;think&gt;…&lt;/think&gt;</c> or <c>&lt;thinking&gt;…&lt;/thinking&gt;</c>)
/// from the content of a streamed LLM response, chunk by chunk. Tags may be split across chunk
/// boundaries: a trailing fragment that could still become a tag is held back until the next
/// <see cref="Feed"/> or <see cref="Flush"/> resolves it.
/// </summary>
internal sealed class InlineThinkSplitter
{
    private static readonly string[] OpenTags = ["<think>", "<thinking>"];
    private static readonly string[] CloseTags = ["</think>", "</thinking>"];

    private string _pending = string.Empty;
    private bool _inThink;

    /// <summary>Consumes one chunk; returns the content and thinking text that can be released now.</summary>
    public (string Content, string Thinking) Feed(string chunk)
    {
        var text = _pending + chunk;
        _pending = string.Empty;

        var content = new StringBuilder();
        var thinking = new StringBuilder();
        int i = 0;

        while (i < text.Length)
        {
            var target = _inThink ? thinking : content;
            var lt = text.IndexOf('<', i);
            if (lt < 0)
            {
                target.Append(text, i, text.Length - i);
                break;
            }

            target.Append(text, i, lt - i);
            var rest = text.AsSpan(lt);
            var tags = _inThink ? CloseTags : OpenTags;

            var matched = FindTagAtStart(rest, tags);
            if (matched is not null)
            {
                _inThink = !_inThink;
                if (_inThink) SawThinking = true;
                i = lt + matched.Length;
                continue;
            }

            if (IsPrefixOfAnyTag(rest, tags))
            {
                // Could still become a tag once the next chunk arrives — hold it back.
                _pending = rest.ToString();
                break;
            }

            target.Append('<');
            i = lt + 1;
        }

        return (content.ToString(), thinking.ToString());
    }

    /// <summary>Releases any held-back fragment at the end of the stream.</summary>
    public (string Content, string Thinking) Flush()
    {
        var rest = _pending;
        _pending = string.Empty;
        return _inThink ? (string.Empty, rest) : (rest, string.Empty);
    }

    private static string? FindTagAtStart(ReadOnlySpan<char> text, string[] tags)
    {
        foreach (var tag in tags)
            if (text.StartsWith(tag, StringComparison.OrdinalIgnoreCase)) return tag;
        return null;
    }

    private static bool IsPrefixOfAnyTag(ReadOnlySpan<char> text, string[] tags)
    {
        foreach (var tag in tags)
            if (tag.AsSpan().StartsWith(text, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>True once any inline thinking section has been opened.</summary>
    public bool SawThinking { get; private set; }
}
