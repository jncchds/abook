using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Telegram;

/// <summary>Text, captions and keyboards of the bot's messages (Telegram HTML parse mode).</summary>
public static partial class TelegramFormat
{
    public const int MaxMessage = 4096;
    public const int MaxCaption = 1024;
    /// <summary>A draft shows the end of the text being written; Telegram's limit is 4096 characters.</summary>
    public const int MaxDraftTail = 3500;

    /// <summary>Escapes text for Telegram's HTML mode, which needs only &amp;, &lt; and &gt; escaped.</summary>
    public static string H(string? text) =>
        (text ?? string.Empty).Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>At most <paramref name="max"/> characters, cut at a word boundary with an ellipsis.</summary>
    public static string Clip(string? text, int max)
    {
        text = (text ?? string.Empty).Trim();
        if (text.Length <= max) return text;
        var cut = text.LastIndexOf(' ', Math.Max(0, max - 1), Math.Min(max, 40));
        return text[..(cut > max / 2 ? cut : max - 1)].TrimEnd() + "…";
    }

    /// <summary>Splits plain text into chunks that fit a message after HTML escaping (paragraph, then line, then hard cuts).</summary>
    public static IReadOnlyList<string> SplitForMessages(string text, int max = 3500)
    {
        var chunks = new List<string>();
        var rest = text.Trim();
        while (rest.Length > max)
        {
            var cut = rest.LastIndexOf("\n\n", max, StringComparison.Ordinal);
            if (cut < max / 2) cut = rest.LastIndexOf('\n', max);
            if (cut < max / 2) cut = rest.LastIndexOf(' ', max);
            if (cut < max / 2) cut = max;
            chunks.Add(rest[..cut].Trim());
            rest = rest[cut..].Trim();
        }
        if (rest.Length > 0) chunks.Add(rest);
        return chunks;
    }

    public static int CountWords(string text) =>
        text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

    public static string N(long n) => n.ToString("N0", CultureInfo.InvariantCulture);

    public static string Hash(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32];

    /// <summary>"Telegram · @anna · Writers' room" — stored on answers, notes and chat messages from the bot.</summary>
    public static string Origin(TgUser? user, TelegramBinding binding)
    {
        var who = user is null ? "channel" : user.Display;
        return Clip($"Telegram · {who} · {(binding.IsPrivate ? "private chat" : binding.ChatTitle)}", 300);
    }

    public static string LevelText(TelegramAccessLevel level) => level switch
    {
        TelegramAccessLevel.Read => "read: status, notices and files",
        TelegramAccessLevel.Participate => "participate: also answer questions, export, add notes, and write in the book chat and apply what it suggests",
        _ => "control: also start, continue and stop runs, rewind and disconnect",
    };

    // ── Status card ───────────────────────────────────────────────────────────

    /// <summary>What the status card shows; gathered by the dispatcher.</summary>
    public sealed record StatusView(
        Book Book,
        IReadOnlyList<Chapter> Chapters,
        AgentRunStatus? Run,
        string? Step,
        int? LiveWords,
        long PromptTokens,
        long CompletionTokens,
        int OpenClarifications);

    /// <summary>
    /// The status card as a rich message: the title as a heading, then a compact table — the book and its progress bar,
    /// the run's state, step, words written and tokens — and when it was updated, in the reader's own time zone.
    /// </summary>
    public static string StatusCard(StatusView v)
    {
        var rows = new List<(string Key, string Value)>();
        var done = v.Chapters.Count(c => c.Status == ChapterStatus.Done);
        var total = Math.Max(v.Chapters.Count, v.Book.Mode == BookMode.Autonomous ? v.Book.TargetChapterCount : 0);
        rows.Add(("Book", v.Book.Mode == BookMode.Interactive ? "💬 Interactive" : "🤖 Autonomous"));
        if (total > 0) rows.Add(("Chapters", $"{done}/{total} chapters done {Bar(done, total)}"));

        var run = v.Run;
        if (run is null)
            rows.Add(("Status", "⚪️ Idle"));
        else
        {
            var chapter = run.ChapterId is int id ? v.Chapters.FirstOrDefault(c => c.Id == id) : null;
            var (icon, label) = run.State switch
            {
                RunStates.Running => ("▶️", "Running"),
                RunStates.WaitingForInput => ("⏸", "Waiting for answers"),
                RunStates.Done => ("✅", "Finished"),
                RunStates.Failed => ("❌", "Failed"),
                RunStates.Cancelled => ("⏹", "Stopped"),
                _ => ("•", run.State),
            };
            var state = $"{icon} <b>{label}</b>";
            if (run.IsActive)
                state += $" — {H(RoleName(run.Role))}{(chapter is not null ? $" · Chapter {chapter.Number}" : "")}";
            rows.Add(("Status", state));
            if (run.IsActive && !string.IsNullOrWhiteSpace(v.Step))
                rows.Add(("Step", $"<i>{H(Clip(v.Step, 200))}</i>"));
            if (run.IsActive && v.LiveWords is int words and > 0)
                rows.Add(("Written", $"✍️ ~{N(words)} words so far"));
            if (run.State == RunStates.WaitingForInput && v.OpenClarifications > 0)
                rows.Add(("Questions", $"❓ {v.OpenClarifications} to answer — reply to the question messages"));
        }
        if (v.PromptTokens + v.CompletionTokens > 0)
            rows.Add(("Tokens", $"{N(v.PromptTokens)} in · {N(v.CompletionTokens)} out"));

        var sb = new StringBuilder();
        sb.Append("<h3>📖 ").Append(H(v.Book.Title)).Append("</h3><table compact>");
        foreach (var (key, value) in rows)
            sb.Append("<tr><td><b>").Append(key).Append("</b></td><td>").Append(value).Append("</td></tr>");
        // To the minute, so a card that didn't change is not edited again just for the time.
        var now = DateTime.UtcNow;
        sb.Append("</table><footer>🕒 Updated ").Append(TelegramRich.Time(new DateTime(now.Year, now.Month, now.Day, now.Hour, now.Minute, 0, DateTimeKind.Utc)))
          .Append("</footer>");
        return sb.ToString();
    }

    public static IReadOnlyList<IReadOnlyList<TgButton>> StatusKeyboard(TelegramBinding binding, StatusView v)
    {
        var rows = new List<IReadOnlyList<TgButton>>();
        var b = binding.Id;
        if (binding.Level == TelegramAccessLevel.Control && v.Book.Mode == BookMode.Autonomous)
        {
            if (v.Run is { IsActive: true })
                rows.Add([new TgButton("⏹ Stop", Callback(b, "stop"), Style: "danger")]);
            else if (v.Chapters.Count == 0)
                rows.Add([new TgButton("▶️ Start the book", Callback(b, "start"), Style: "success"),
                          new TgButton("🗂 Plan only", Callback(b, "plan"))]);
            else if (v.Chapters.Any(c => c.Status != ChapterStatus.Done))
                rows.Add([new TgButton("⏩ Continue", Callback(b, "cont"), Style: "success")]);
            else if (v.Chapters.Count < v.Book.TargetChapterCount)
                // Every chapter so far is done but the plan is short of the target: the workflow plans the rest.
                rows.Add([new TgButton("▶️ Plan and write the rest", Callback(b, "start"), Style: "success")]);
        }
        else if (binding.Level == TelegramAccessLevel.Control && v.Run is { IsActive: true })
            rows.Add([new TgButton("⏹ Stop", Callback(b, "stop"), Style: "danger")]);

        var row = new List<TgButton> { new("🔄 Refresh", Callback(b, "refresh")) };
        if (binding.Level >= TelegramAccessLevel.Participate)
            row.Add(new TgButton("📦 Export", Callback(b, "export")));
        rows.Add(row);
        return rows;
    }

    private static string Bar(int done, int total)
    {
        const int width = 10;
        var filled = total == 0 ? 0 : (int)Math.Round(width * Math.Clamp(done / (double)total, 0, 1));
        return new string('▰', filled) + new string('▱', width - filled);
    }

    /// <summary>A role sent as text (error events): its friendly name when it is a known role.</summary>
    public static string RoleName(string role) =>
        Enum.TryParse<AgentRole>(role, out var parsed) ? RoleName(parsed) : role;

    public static string RoleName(AgentRole role) => role switch
    {
        AgentRole.StoryBibleAgent => "Story Bible",
        AgentRole.CharactersAgent => "Characters",
        AgentRole.PlotThreadsAgent => "Plot threads",
        AgentRole.ChaptersAgent => "Chapter outlines",
        AgentRole.ContinuityChecker => "Checker",
        AgentRole.IllustrationPlanner => "Illustration planner",
        AgentRole.BookChat => "Book chat",
        _ => role.ToString(),
    };

    // ── Callbacks ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Button data: "{bindingId}:{action}[:{arg}]" (Telegram allows 64 bytes). Telegram only ever sends data from
    /// buttons the bot made, and the handler checks the press came from the binding's chat, so it needs no signature.
    /// </summary>
    public static string Callback(int bindingId, string action, long? arg = null) =>
        arg is null ? $"{bindingId}:{action}" : $"{bindingId}:{action}:{arg}";

    public static bool TryParseCallback(string? data, out int bindingId, out string action, out long arg)
    {
        bindingId = 0; action = string.Empty; arg = 0;
        var parts = (data ?? string.Empty).Split(':');
        if (parts.Length < 2 || !int.TryParse(parts[0], out bindingId)) return false;
        action = parts[1];
        return parts.Length < 3 || long.TryParse(parts[2], out arg);
    }

    // ── Rewind ────────────────────────────────────────────────────────────────

    /// <summary>A moment as microseconds since 1970 — fits button data and keeps the database's precision.</summary>
    public static long Micros(DateTime at) => (at.ToUniversalTime() - DateTime.UnixEpoch).Ticks / 10;
    public static DateTime FromMicros(long micros) => DateTime.SpecifyKind(DateTime.UnixEpoch.AddTicks(micros * 10), DateTimeKind.Utc);

    public static string MomentText(DateTime at) => at.ToUniversalTime().ToString("d MMM HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";

    /// <summary>The preview: a one-line summary, then every change in an expandable quote.</summary>
    public static string RewindPreview(ABook.Core.Interfaces.RewindPlan plan)
    {
        var sb = new StringBuilder();
        sb.Append("<h3>⏪ Rewind the book to ").Append(MomentText(plan.At)).Append("?</h3>");
        var parts = plan.Changes
            .GroupBy(c => (c.EntityType, c.Kind))
            .Select(g => $"{g.Count()} {TypeWords(g.Key.EntityType, g.Count())} {KindWords(g.Key.Kind)}")
            .ToList();
        if (plan.HiddenChatMessages > 0) parts.Add($"{plan.HiddenChatMessages} chat message(s) hidden");
        sb.Append("<p>").Append(H(string.Join(" · ", parts))).Append("</p>");

        if (plan.Changes.Count > 0)
        {
            sb.Append(plan.Changes.Count <= 8 ? "<details open>" : "<details>").Append("<summary>All changes (").Append(plan.Changes.Count).Append(")</summary><ul>");
            foreach (var c in plan.Changes.Take(80))
                sb.Append("<li><b>").Append(KindWords(c.Kind)).Append(":</b> ").Append(H(Clip(c.Label, 90)))
                  .Append(c.Fields.Count > 0 ? $" <i>({H(string.Join(", ", c.Fields.Take(6)))})</i>" : "").Append("</li>");
            if (plan.Changes.Count > 80) sb.Append("<li>… and ").Append(plan.Changes.Count - 80).Append(" more</li>");
            sb.Append("</ul></details>");
        }
        sb.Append("<footer>Each part gets the state it had then, as a new version — everything in between stays in its history. " +
                  "Later book-chat messages are hidden for good.</footer>");
        return sb.ToString();
    }

    private static string KindWords(ABook.Core.Interfaces.RewindChangeKind kind) => kind switch
    {
        ABook.Core.Interfaces.RewindChangeKind.Restore => "restored",
        ABook.Core.Interfaces.RewindChangeKind.Archive => "archived",
        _ => "brought back",
    };

    private static string TypeWords(string type, int count) => (type, count == 1) switch
    {
        ("Book", _) => "book fields",
        ("Chapter", true) => "chapter", ("Chapter", false) => "chapters",
        ("StoryBible", _) => "Story Bible",
        ("CharacterCard", true) => "character", ("CharacterCard", false) => "characters",
        ("PlotThread", true) => "plot thread", ("PlotThread", false) => "plot threads",
        ("Clarification", true) => "clarification", ("Clarification", false) => "clarifications",
        ("ChatProposal", true) => "chat suggestion", ("ChatProposal", false) => "chat suggestions",
        _ => type,
    };

    // ── Book chat ─────────────────────────────────────────────────────────────

    public static string ProposalStatusIcon(ChatProposalStatus status) => status switch
    {
        ChatProposalStatus.Pending => "•",
        ChatProposalStatus.Applying => "⏳",
        ChatProposalStatus.Applied => "✅",
        ChatProposalStatus.Rejected => "✖️",
        ChatProposalStatus.Superseded => "↪️",
        ChatProposalStatus.Failed => "⚠️",
        ChatProposalStatus.Undone => "↩️",
        ChatProposalStatus.Queued => "🕒",
        _ => "•",
    };

    public static string ProposalsText(IReadOnlyList<ChatProposal> proposals)
    {
        var sb = new StringBuilder("🛠 <b>Suggested changes</b>\n");
        var i = 0;
        foreach (var p in proposals)
        {
            sb.Append(++i).Append(". ").Append(ProposalStatusIcon(p.Status)).Append(' ').Append(H(Clip(p.Label, 200)));
            if (p.OptionGroup is { Length: > 0 } g) sb.Append(" <i>(option ").Append(H(g)).Append(")</i>");
            // Tapping the command posts the suggestion's full details as a separate message.
            sb.Append("  ").Append(DetailsCommandPrefix).Append(p.Id);
            if (p.Status is ChatProposalStatus.Failed && p.ResultNote is { Length: > 0 } note)
                sb.Append("\n   <i>").Append(H(Clip(note, 200))).Append("</i>");
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    public const string DetailsCommandPrefix = "/details_";

    /// <summary>
    /// The suggestions as a rich message: one numbered line each (numbers are what "1 3" replies refer to), with an
    /// inline <b>details</b> link that posts the suggestion in full.
    /// </summary>
    public static string ProposalsRich(int bindingId, IReadOnlyList<ChatProposal> proposals, string? footer)
    {
        var sb = new StringBuilder("<h3>🛠 Suggested changes</h3>");
        var i = 0;
        foreach (var p in proposals)
        {
            sb.Append("<p><b>").Append(++i).Append(".</b> ").Append(ProposalStatusIcon(p.Status)).Append(' ');
            sb.Append(p.Status is ChatProposalStatus.Rejected or ChatProposalStatus.Superseded or ChatProposalStatus.Undone
                ? $"<s>{H(Clip(p.Label, 200))}</s>" : H(Clip(p.Label, 200)));
            if (p.OptionGroup is { Length: > 0 } g) sb.Append(" <i>(option ").Append(H(g)).Append(")</i>");
            sb.Append(" · ").Append(TelegramRich.Link("details", Callback(bindingId, "det", p.Id)));
            if (p.Status is ChatProposalStatus.Failed or ChatProposalStatus.Queued && p.ResultNote is { Length: > 0 } note)
                sb.Append("<br><i>").Append(H(Clip(note, 200))).Append("</i>");
            sb.Append("</p>");
        }
        if (footer is { Length: > 0 }) sb.Append("<footer>").Append(footer).Append("</footer>");
        return sb.ToString();
    }

    /// <summary>What the web UI calls each action type (the card's tag).</summary>
    private static readonly Dictionary<string, string> TypeLabels = new()
    {
        ["update_premise"] = "Premise", ["update_story_bible"] = "Story Bible",
        ["upsert_character"] = "Character", ["archive_character"] = "Character",
        ["upsert_plot_thread"] = "Plot thread", ["archive_plot_thread"] = "Plot thread",
        ["add_chapter"] = "New chapter", ["update_chapter_outline"] = "Outline", ["archive_chapter"] = "Remove chapter", ["move_chapter"] = "Move chapter",
        ["write_chapter"] = "Write", ["revise_chapter"] = "Revise", ["patch_chapter"] = "Edit passage", ["add_note"] = "Note",
        ["set_part_requests"] = "Setting",
        ["check_outline"] = "Outline check", ["check_chapter"] = "Continuity check", ["summarize_chapter"] = "Summary",
    };

    public static string TypeLabel(string type) => TypeLabels.GetValueOrDefault(type, type.Replace('_', ' '));

    /// <summary>
    /// One suggestion in full, as the web UI's card shows it: its kind and status, the Writer's or Editor's instructions
    /// for long actions, the conflict it answers, the change as a line diff (unchanged stretches folded), what applying
    /// it did, and — once applied — a link to what it changed.
    /// </summary>
    public static string ProposalDetailsRich(ChatProposal p, int number, bool bibleLocked = true, string? baseUrl = null)
    {
        var step = ABook.Agents.BookChatActions.IsStep(p.Type);
        var longAction = p.Type is ABook.Agents.BookChatActions.WriteChapter or ABook.Agents.BookChatActions.ReviseChapter;
        var status = step && p.Status == ChatProposalStatus.Applying ? "Running"
            : step && p.Status == ChatProposalStatus.Applied ? "Done" : p.Status.ToString();

        var sb = new StringBuilder();
        sb.Append("<h3>🔍 ").Append(number).Append(". ").Append(H(Clip(p.Label, 300))).Append("</h3>");
        sb.Append("<p><b>").Append(H(TypeLabel(p.Type))).Append("</b> · ").Append(ProposalStatusIcon(p.Status)).Append(' ').Append(status);
        if (p.OptionGroup is { Length: > 0 } g) sb.Append(" · option ").Append(H(g));
        sb.Append("</p>");
        if (longAction && PayloadInstructions(p) is { Length: > 0 } instructions)
            sb.Append("<blockquote>").Append(Lines(Clip(instructions, 4000))).Append("</blockquote>");
        if (p.Type == ABook.Agents.BookChatActions.WriteChapter && !bibleLocked && IsOpen(p))
            sb.Append("<p><i>Applying this locks the premise and Story Bible.</i></p>");
        if (p.ConflictNote is { Length: > 0 } conflict)
            sb.Append("<p><b>⚠️ Conflict with the book</b></p><blockquote>").Append(Lines(Clip(conflict, 1500))).Append("</blockquote>");

        if (!longAction && (p.PreviewBefore.Length > 0 || p.PreviewAfter.Length > 0))
        {
            var diff = DiffRich(p.PreviewBefore, p.PreviewAfter, 12000);
            sb.Append(diff.Length > 0
                ? $"<p><b>Changes</b></p><blockquote>{diff}</blockquote>"
                : "<p><i>No changes.</i></p>");
        }
        if (p.ResultNote is { Length: > 0 } result)
            sb.Append("<p><b>Result:</b> ").Append(H(Clip(result, 600))).Append("</p>");
        if (p.Status == ChatProposalStatus.Applied && baseUrl is { Length: > 0 } && TargetPath(p.BookId, p.Target) is { } path)
            sb.Append("<p><a href=\"").Append(H(baseUrl.TrimEnd('/') + path)).Append("\">Open ↗</a></p>");
        return sb.ToString();
    }

    /// <summary>The instructions a write/revise suggestion gives the Writer or the Editor.</summary>
    private static string? PayloadInstructions(ChatProposal p)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(p.PayloadJson);
            return doc.RootElement.ValueKind == System.Text.Json.JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("instructions", out var v) && v.ValueKind == System.Text.Json.JsonValueKind.String
                ? v.GetString() : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The web page of what a suggestion changed (as the UI's "Open" link), or null.</summary>
    private static string? TargetPath(int bookId, string? target)
    {
        if (string.IsNullOrEmpty(target)) return null;
        var colon = target.IndexOf(':');
        var (kind, key) = colon < 0 ? (target, "") : (target[..colon], target[(colon + 1)..]);
        return kind switch
        {
            "premise" => $"/books/{bookId}/overview",
            "bible" => $"/books/{bookId}/story-bible",
            "character" => $"/books/{bookId}/characters",
            "thread" => $"/books/{bookId}/plot-threads",
            "chapter" or "prose" => $"/books/{bookId}/chapters/{Uri.EscapeDataString(key)}",
            "note" => $"/books/{bookId}/clarifications",
            "setting" => $"/books/{bookId}/settings",
            _ => null,
        };
    }

    /// <summary>A blank line or an empty JSON field (<c>"notes": "",</c>): adding or removing one says nothing.</summary>
    [System.Text.RegularExpressions.GeneratedRegex("^\\s*(\"[^\"]*\"\\s*:\\s*\"\")?\\s*,?\\s*$")]
    private static partial System.Text.RegularExpressions.Regex EmptyLine();

    /// <summary>
    /// The change as the web UI's diff shows it: "+ " added and "− " removed lines, two lines of context around each
    /// change, longer unchanged stretches folded to "⋯ N unchanged lines". Empty when nothing differs. Clipped to
    /// about <paramref name="max"/> characters.
    /// </summary>
    public static string DiffText(string before, string after, int max = 12000)
    {
        const int context = 2;
        var lines = LineDiff(before.Replace("\r\n", "\n"), after.Replace("\r\n", "\n"))
            .Where(l => l.Kind == ' ' || !EmptyLine().IsMatch(l.Text)).ToList();
        if (lines.All(l => l.Kind == ' ')) return "";
        var sb = new StringBuilder();
        var skipped = 0;
        void Flush()
        {
            if (skipped == 0) return;
            sb.Append("⋯ ").Append(skipped).Append(skipped == 1 ? " unchanged line\n" : " unchanged lines\n");
            skipped = 0;
        }
        for (var i = 0; i < lines.Count; i++)
        {
            var near = false;
            for (var j = Math.Max(0, i - context); j <= Math.Min(lines.Count - 1, i + context) && !near; j++)
                near = lines[j].Kind != ' ';
            if (!near) { skipped++; continue; }
            Flush();
            sb.Append(lines[i].Kind switch { '+' => "+ ", '-' => "− ", _ => "  " }).Append(lines[i].Text).Append('\n');
            if (sb.Length > max) { sb.Append("…\n"); skipped = 0; break; }
        }
        Flush();
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// <see cref="DiffText"/> as HTML whose lines wrap (a code block scrolls sideways on a phone): added lines marked 🟢,
    /// removed ones 🔴 and struck through, unchanged context as is, folded stretches in italics. Empty when nothing differs.
    /// </summary>
    public static string DiffRich(string before, string after, int max = 12000)
    {
        var diff = DiffText(before, after, max);
        if (diff.Length == 0) return "";
        return string.Join("<br>", diff.Split('\n').Select(line =>
            line.StartsWith("+ ", StringComparison.Ordinal) ? "🟢 " + H(line[2..])
            : line.StartsWith("− ", StringComparison.Ordinal) ? "🔴 <s>" + H(line[2..]) + "</s>"
            : line.StartsWith("  ", StringComparison.Ordinal) ? H(line[2..])
            : "<i>" + H(line) + "</i>"));
    }

    /// <summary>Line diff (longest common subsequence after the common prefix and suffix), as the web UI computes it.</summary>
    private static List<(char Kind, string Text)> LineDiff(string before, string after)
    {
        // An empty text has no lines: something new is all additions, not an empty line replaced.
        var a = before.Length == 0 ? [] : before.Split('\n');
        var b = after.Length == 0 ? [] : after.Split('\n');
        var start = 0;
        while (start < a.Length && start < b.Length && a[start] == b[start]) start++;
        int endA = a.Length, endB = b.Length;
        while (endA > start && endB > start && a[endA - 1] == b[endB - 1]) { endA--; endB--; }

        var output = a[..start].Select(t => (' ', t)).ToList();
        int n = endA - start, m = endB - start;
        if ((long)n * m > 4_000_000)
        {
            // Too big for the table: everything in the middle replaced.
            output.AddRange(a[start..endA].Select(t => ('-', t)));
            output.AddRange(b[start..endB].Select(t => ('+', t)));
        }
        else
        {
            var lcs = new int[n + 1, m + 1];
            for (var i = n - 1; i >= 0; i--)
                for (var j = m - 1; j >= 0; j--)
                    lcs[i, j] = a[start + i] == b[start + j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
            int x = 0, y = 0;
            while (x < n || y < m)
            {
                if (x < n && y < m && a[start + x] == b[start + y]) { output.Add((' ', a[start + x])); x++; y++; }
                else if (y < m && (x >= n || lcs[x, y + 1] >= lcs[x + 1, y])) { output.Add(('+', b[start + y])); y++; }
                else { output.Add(('-', a[start + x])); x++; }
            }
        }
        output.AddRange(a[endA..].Select(t => (' ', t)));
        return output;
    }

    /// <summary>Escaped text with its line breaks kept (rich HTML collapses newlines like any HTML).</summary>
    public static string Lines(string text) => H(text).Replace("\r\n", "\n").Replace("\n", "<br>");

    public static bool IsOpen(ChatProposal p) => p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed;

    /// <summary>
    /// The suggestion buttons. Each open suggestion: a tick box (☐/☑ with its number and label), ✅ to apply just it and
    /// ✖️ to reject it. Alternatives (option A/B) are chosen as a whole with one button per option. Queued chapter
    /// writes can be started early or cancelled. Applied ones offer
    /// Undo (steps: Run again). The last row acts on the ticked ones — or on all of them when none is ticked.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<TgButton>> ProposalsKeyboard(
        int bindingId, int chatMessageId, IReadOnlyList<ChatProposal> proposals, IReadOnlySet<int>? ticked = null)
    {
        ticked ??= new HashSet<int>();
        var rows = new List<IReadOnlyList<TgButton>>();
        var shownGroups = new HashSet<string>();
        for (var i = 0; i < proposals.Count; i++)
        {
            var p = proposals[i];
            var n = i + 1;
            if (p.OptionGroup is { Length: > 0 } group)
            {
                if (IsOpen(p) && shownGroups.Add(group))
                    rows.Add([new TgButton($"Choose option {group}", Callback(bindingId, "opt", p.Id), Style: "primary")]);
                else if (p.Status == ChatProposalStatus.Applied)
                    rows.Add([new TgButton($"↩️ Undo {n}. {Clip(p.Label, 30)}", Callback(bindingId, "undo", p.Id))]);
                continue;
            }
            if (IsOpen(p))
                rows.Add([
                    new TgButton($"{(ticked.Contains(p.Id) ? "☑" : "☐")} {n}. {Clip(p.Label, 26)}", Callback(bindingId, "tick", p.Id)),
                    new TgButton("✅", Callback(bindingId, "apply", p.Id), Style: "success"),
                    new TgButton("✖️", Callback(bindingId, "reject", p.Id)),
                ]);
            else if (p.Status == ChatProposalStatus.Queued)
                rows.Add([
                    new TgButton($"▶ Start {n}. {Clip(p.Label, 26)} now", Callback(bindingId, "apply", p.Id)),
                    new TgButton("✖️ Cancel", Callback(bindingId, "reject", p.Id)),
                ]);
            else if (p.Status == ChatProposalStatus.Applied && ABook.Agents.BookChatActions.IsStep(p.Type))
                rows.Add([new TgButton($"↻ Run {n}. {Clip(p.Label, 30)} again", Callback(bindingId, "apply", p.Id))]);
            else if (p.Status == ChatProposalStatus.Applied)
                rows.Add([new TgButton($"↩️ Undo {n}. {Clip(p.Label, 30)}", Callback(bindingId, "undo", p.Id))]);
        }

        var tickable = proposals.Where(p => IsOpen(p) && p.OptionGroup is not { Length: > 0 }).ToList();
        var tickedOpen = tickable.Count(p => ticked.Contains(p.Id));
        if (tickedOpen > 0)
            rows.Add([
                new TgButton($"✅ Apply ticked ({tickedOpen})", Callback(bindingId, "aptk", chatMessageId), Style: "success"),
                new TgButton("✖️ Reject ticked", Callback(bindingId, "rjtk", chatMessageId)),
                new TgButton("Clear", Callback(bindingId, "clrtk", chatMessageId)),
            ]);
        else if (tickable.Count >= 2)
            rows.Add([
                new TgButton($"✅ Apply all ({tickable.Count})", Callback(bindingId, "applyall", chatMessageId), Style: "primary"),
                new TgButton("✖️ Reject all", Callback(bindingId, "rejall", chatMessageId)),
            ]);
        return rows;
    }

    /// <summary>
    /// A reply to the suggestions: "1 3", "apply 1, 3", "✅ 2" or "reject 2" → the action and the numbers (1-based, as
    /// listed). Null when the text is anything else (then it is a book-chat message like any other).
    /// </summary>
    public static (bool Reject, IReadOnlyList<int> Numbers)? ParseProposalPick(string text)
    {
        var m = ProposalPick().Match(text.Trim());
        if (!m.Success) return null;
        var verb = m.Groups["verb"].Value.ToLowerInvariant();
        var numbers = System.Text.RegularExpressions.Regex.Matches(m.Groups["nums"].Value, "[0-9]+")
            .Select(x => int.Parse(x.Value, CultureInfo.InvariantCulture)).Distinct().ToList();
        return numbers.Count == 0 ? null : (verb is "reject" or "✖️" or "✖" or "x", numbers);
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"^(?<verb>apply|accept|reject|✅|✖️|✖|x)?\s*(?<nums>[0-9]+(?:\s*(?:,|\s|and)\s*[0-9]+)*)\s*$",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex ProposalPick();

    public static string ProposalsHash(IReadOnlyList<ChatProposal> proposals) =>
        Hash(string.Join(",", proposals.Select(p => $"{p.Id}={p.Status}")));
}
