using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace ABook.Api.Telegram;

/// <summary>
/// Rich messages (Bot API 10.1+): headings, lists, tables, collapsible sections and buttons inside the text, up to
/// 32,768 characters. The bot writes rich HTML once; when Telegram (or a self-hosted Bot API server) refuses rich
/// messages, the same content is turned into a plain HTML message — inline buttons move into the keyboard — so nothing
/// stops working. After one refusal the process sends plain messages only.
/// </summary>
public static partial class TelegramRich
{
    private static volatile bool _unsupported;

    /// <summary>True once Telegram refused a rich message: everything goes plain from then on.</summary>
    public static bool Unsupported => _unsupported;

    /// <summary>An inline link-styled button inside the text (a callback the bot handles like a keyboard button).</summary>
    public static string Link(string label, string callbackData) =>
        $"<tg-button type=\"callback_data\" style=\"link\" data=\"{WebUtility.HtmlEncode(callbackData)}\">{TelegramFormat.H(label)}</tg-button>";

    /// <summary>A moment shown in the reader's own time zone (<c>t</c> time, <c>d</c> date, combinations as in tg-time).</summary>
    public static string Time(DateTime utc, string format = "t") =>
        $"<tg-time unix=\"{new DateTimeOffset(DateTime.SpecifyKind(utc, DateTimeKind.Utc)).ToUnixTimeSeconds()}\" format=\"{format}\">{utc:HH:mm} UTC</tg-time>";

    /// <summary>Set at startup: where refused rich messages are reported.</summary>
    public static ILogger? Logger { get; set; }

    public static async Task<TgMessage> SendAsync(
        TelegramApi api, long chatId, int? threadId, string richHtml, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null,
        int? replyTo = null, bool silent = false, CancellationToken ct = default)
    {
        if (!_unsupported)
        {
            try { return await api.SendRichMessageAsync(chatId, threadId, richHtml, keyboard, replyTo, silent, ct); }
            catch (TelegramApiException ex) when (Refused(ex, richHtml)) { }
        }
        var (html, buttons) = ToPlain(richHtml);
        return await api.SendMessageAsync(chatId, threadId, html, Merge(buttons, keyboard), replyTo, silent, ct);
    }

    public static async Task EditAsync(
        TelegramApi api, long chatId, int messageId, string richHtml, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null,
        CancellationToken ct = default)
    {
        if (!_unsupported)
        {
            try { await api.EditRichMessageAsync(chatId, messageId, richHtml, keyboard, ct); return; }
            catch (TelegramApiException ex) when (ex.NotModified) { return; }
            catch (TelegramApiException ex) when (Refused(ex, richHtml)) { }
        }
        var (html, buttons) = ToPlain(richHtml);
        try { await api.EditMessageTextAsync(chatId, messageId, html, Merge(buttons, keyboard), ct); }
        catch (TelegramApiException ex) when (ex.NotModified) { }
    }

    /// <summary>
    /// Whether to fall back to a plain message: always for a 400 about this message's content (logged — it is a bug in
    /// what the bot wrote, and only this message goes plain); and for good when the server doesn't know rich messages.
    /// Other errors (the chat is gone, flood control) are not about the format and are rethrown.
    /// </summary>
    private static bool Refused(TelegramApiException ex, string richHtml)
    {
        if (ex.Code == 404 || ex.Description.Contains("method not found", StringComparison.OrdinalIgnoreCase)
            || ex.Description.Contains("unknown parameter", StringComparison.OrdinalIgnoreCase))
        {
            _unsupported = true;
            Logger?.LogWarning("Telegram: this Bot API server doesn't support rich messages ({Description}); sending plain messages.", ex.Description);
            return true;
        }
        if (ex.Code != 400 || ex.ChatGone || ex.NotModified || ex.MessageMissing) return false;
        Logger?.LogWarning("Telegram: a rich message was refused ({Description}); sent as plain instead. Start: {Start}",
            ex.Description, richHtml.Length > 300 ? richHtml[..300] : richHtml);
        return true;
    }

    private static IReadOnlyList<IReadOnlyList<TgButton>>? Merge(List<TgButton> inline, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard)
    {
        if (inline.Count == 0) return keyboard;
        var rows = new List<IReadOnlyList<TgButton>>();
        for (var i = 0; i < inline.Count; i += 3) rows.Add(inline.Skip(i).Take(3).ToList());
        if (keyboard is not null) rows.AddRange(keyboard);
        return rows;
    }

    /// <summary>
    /// The plain-message version of rich HTML the bot wrote: headings bold, paragraphs and list items as lines, table
    /// rows joined with " · ", collapsible sections as a bold title plus an expandable quote, footers italic; inline
    /// callback buttons are taken out and returned as keyboard buttons. Clipped to fit a plain message.
    /// </summary>
    public static (string Html, List<TgButton> Buttons) ToPlain(string rich)
    {
        var buttons = new List<TgButton>();
        var s = InlineButton().Replace(rich, m =>
        {
            var label = WebUtility.HtmlDecode(Regex.Replace(m.Groups["label"].Value, "<[^>]+>", "")).Trim();
            // A bare "edit"/"add" next to a field means nothing as a keyboard button: name the field (the last bold
            // text before it — the field's label).
            if (label is "edit" or "add" && LastBold(rich, m.Index) is { } field) label = $"✏️ {field}";
            buttons.Add(new TgButton(label, WebUtility.HtmlDecode(m.Groups["data"].Value)));
            return "";
        });
        s = Regex.Replace(s, "<tg-button[^>]*>.*?</tg-button>", "", RegexOptions.Singleline);
        s = Regex.Replace(s, "</?tg-button-row[^>]*>", "");
        s = Regex.Replace(s, "<tg-time[^>]*>(.*?)</tg-time>", "$1", RegexOptions.Singleline);
        s = Regex.Replace(s, "<h[1-6][^>]*>(.*?)</h[1-6]>", "<b>$1</b>\n", RegexOptions.Singleline);
        s = Regex.Replace(s, "<details[^>]*>\\s*<summary>(.*?)</summary>(.*?)</details>",
            m => $"<b>{Regex.Replace(m.Groups[1].Value, "</?b>", "").Trim()}</b>\n<blockquote expandable>{Regex.Replace(m.Groups[2].Value, "</?(p|blockquote)[^>]*>", "\n").Trim()}</blockquote>\n",
            RegexOptions.Singleline);
        s = Regex.Replace(s, "<tr[^>]*>(.*?)</tr>", m =>
            string.Join(" · ", Regex.Matches(m.Groups[1].Value, "<t[hd][^>]*>(.*?)</t[hd]>", RegexOptions.Singleline)
                .Select(c => c.Groups[1].Value.Trim()).Where(c => c.Length > 0)) + "\n", RegexOptions.Singleline);
        s = Regex.Replace(s, "</?(table|caption)[^>]*>", "");
        s = Regex.Replace(s, "<input type=\"checkbox\" checked[^>]*>", "☑ ");
        s = Regex.Replace(s, "<input type=\"checkbox\"[^>]*>", "☐ ");
        s = Regex.Replace(s, "<li[^>]*>", "• ");
        s = Regex.Replace(s, "</li>|<br\\s*/?>|</p>|<hr\\s*/?>", "\n");
        s = Regex.Replace(s, "</?(ul|ol|p)[^>]*>", "");
        s = Regex.Replace(s, "<footer>(.*?)</footer>", "<i>$1</i>", RegexOptions.Singleline);
        s = Regex.Replace(s, "\n{3,}", "\n\n").Trim();
        return (ClipHtml(s, 3900), buttons);
    }

    /// <summary>The text of the last &lt;b&gt; before <paramref name="index"/> (within a few hundred characters), without a trailing colon.</summary>
    private static string? LastBold(string html, int index)
    {
        var from = Math.Max(0, index - 600);
        var bolds = Regex.Matches(html[from..index], "<b>(.*?)</b>", RegexOptions.Singleline);
        if (bolds.Count == 0) return null;
        var text = WebUtility.HtmlDecode(Regex.Replace(bolds[^1].Groups[1].Value, "<[^>]+>", "")).Trim().TrimEnd(':').Trim();
        return text.Length is > 0 and <= 40 ? text : null;
    }

    /// <summary>Cuts plain HTML to about <paramref name="max"/> characters without leaving a tag open.</summary>
    private static string ClipHtml(string html, int max)
    {
        if (html.Length <= max) return html;
        var cut = html[..max];
        var lt = cut.LastIndexOf('<');
        if (lt > cut.LastIndexOf('>')) cut = cut[..lt];
        var open = new Stack<string>();
        foreach (Match m in Regex.Matches(cut, "<(/?)([a-z]+)[^>]*>"))
        {
            if (m.Groups[1].Value == "/") { if (open.Count > 0) open.Pop(); }
            else open.Push(m.Groups[2].Value);
        }
        var sb = new StringBuilder(cut).Append('…');
        while (open.Count > 0) sb.Append("</").Append(open.Pop()).Append('>');
        return sb.ToString();
    }

    [GeneratedRegex("<tg-button type=\"callback_data\"[^>]*data=\"(?<data>[^\"]*)\"[^>]*>(?<label>.*?)</tg-button>", RegexOptions.Singleline)]
    private static partial Regex InlineButton();
}
