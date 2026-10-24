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

    public static string StatusCard(StatusView v)
    {
        var sb = new StringBuilder();
        sb.Append("📖 <b>").Append(H(v.Book.Title)).Append("</b>\n");

        var done = v.Chapters.Count(c => c.Status == ChapterStatus.Done);
        var total = Math.Max(v.Chapters.Count, v.Book.Mode == BookMode.Autonomous ? v.Book.TargetChapterCount : 0);
        sb.Append(v.Book.Mode == BookMode.Interactive ? "💬 Interactive book" : "🤖 Autonomous book");
        if (total > 0)
            sb.Append(" · ").Append(done).Append('/').Append(total).Append(" chapters done ").Append(Bar(done, total));
        sb.Append('\n');

        var run = v.Run;
        if (run is null)
            sb.Append("⚪️ Idle\n");
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
            sb.Append(icon).Append(" <b>").Append(label).Append("</b>");
            if (run.IsActive)
            {
                sb.Append(" — ").Append(H(RoleName(run.Role)));
                if (chapter is not null) sb.Append(" · Chapter ").Append(chapter.Number);
            }
            sb.Append('\n');
            if (run.IsActive && !string.IsNullOrWhiteSpace(v.Step))
                sb.Append("<i>").Append(H(Clip(v.Step, 200))).Append("</i>\n");
            if (run.IsActive && v.LiveWords is int words and > 0)
                sb.Append("✍️ ~").Append(N(words)).Append(" words so far\n");
            if (run.State == RunStates.WaitingForInput && v.OpenClarifications > 0)
                sb.Append("❓ ").Append(v.OpenClarifications).Append(" question(s) to answer — reply to the question messages\n");
        }
        if (v.PromptTokens + v.CompletionTokens > 0)
            sb.Append("🔢 Tokens this run: ").Append(N(v.PromptTokens)).Append(" in · ").Append(N(v.CompletionTokens)).Append(" out\n");
        sb.Append("🕒 Updated ").Append(DateTime.UtcNow.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(" UTC");
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
                rows.Add([new TgButton("▶️ Start the book", Callback(b, "start"), Style: "success")]);
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
        sb.Append("⏪ <b>Rewind the book to ").Append(MomentText(plan.At)).Append("?</b>\n");
        var parts = plan.Changes
            .GroupBy(c => (c.EntityType, c.Kind))
            .Select(g => $"{g.Count()} {TypeWords(g.Key.EntityType, g.Count())} {KindWords(g.Key.Kind)}")
            .ToList();
        if (plan.HiddenChatMessages > 0) parts.Add($"{plan.HiddenChatMessages} chat message(s) hidden");
        sb.Append(H(string.Join(" · ", parts))).Append('\n');

        var lines = plan.Changes.Take(40).Select(c =>
            $"• {KindWords(c.Kind)}: {H(Clip(c.Label, 90))}" + (c.Fields.Count > 0 ? $" <i>({H(string.Join(", ", c.Fields.Take(6)))})</i>" : ""));
        if (plan.Changes.Count > 0)
        {
            sb.Append("<blockquote expandable>").Append(string.Join("\n", lines));
            if (plan.Changes.Count > 40) sb.Append($"\n… and {plan.Changes.Count - 40} more");
            sb.Append("</blockquote>\n");
        }
        sb.Append("<i>Each part gets the state it had then, as a new version — everything in between stays in its history. " +
                  "Later book-chat messages are hidden for good.</i>");
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
    /// One suggestion in full: what it is, its status, the conflict it answers, and what it changes — field by field,
    /// as it was and as it would be (or was made), each in an expandable quote.
    /// </summary>
    public static string ProposalDetails(ChatProposal p, int number)
    {
        var sb = new StringBuilder();
        sb.Append("🔍 <b>").Append(number).Append(". ").Append(H(Clip(p.Label, 300))).Append("</b>\n");
        sb.Append(ProposalStatusIcon(p.Status)).Append(' ').Append(p.Status).Append(" · <i>").Append(H(p.Type.Replace('_', ' '))).Append("</i>");
        if (p.OptionGroup is { Length: > 0 } g) sb.Append(" · option ").Append(H(g));
        sb.Append('\n');
        if (p.ConflictNote is { Length: > 0 } conflict)
            sb.Append("\n⚠️ <b>Conflict</b>\n<blockquote expandable>").Append(H(Clip(conflict, 600))).Append("</blockquote>");
        if (p.ResultNote is { Length: > 0 } result)
            sb.Append("\n<b>Result:</b> ").Append(H(Clip(result, 400))).Append('\n');

        var before = FlatFields(p.PreviewBefore);
        var after = FlatFields(p.PreviewAfter);
        if (before is not null || after is not null)
        {
            before ??= new Dictionary<string, string>();
            after ??= new Dictionary<string, string>();
            var keys = after.Keys.Union(before.Keys)
                .Where(k => before.GetValueOrDefault(k, "") != after.GetValueOrDefault(k, "")).ToList();
            if (keys.Count == 0) sb.Append("\n<i>No field changes.</i>");
            var budget = Math.Max(200, 3000 / Math.Max(1, keys.Count * 2));
            foreach (var k in keys)
            {
                var was = before.GetValueOrDefault(k, "");
                var now = after.GetValueOrDefault(k, "");
                sb.Append("\n<b>").Append(H(FieldLabel(k))).Append("</b>\n");
                if (was.Length > 0) sb.Append("<i>was</i><blockquote expandable>").Append(H(Clip(was, budget))).Append("</blockquote>");
                sb.Append(was.Length > 0 ? "<i>now</i>" : "")
                  .Append("<blockquote expandable>").Append(now.Length > 0 ? H(Clip(now, budget)) : "<i>(empty)</i>").Append("</blockquote>");
            }
        }
        else
        {
            // Prose actions and notes carry plain text: the instructions, the passage, the note.
            if (p.PreviewBefore.Length > 0)
                sb.Append("\n<b>Was</b>\n<blockquote expandable>").Append(H(Clip(p.PreviewBefore, 1500))).Append("</blockquote>");
            if (p.PreviewAfter.Length > 0)
                sb.Append("\n<b>").Append(p.PreviewBefore.Length > 0 ? "Now" : "What it does").Append("</b>\n<blockquote expandable>")
                  .Append(H(Clip(p.PreviewAfter, 1500))).Append("</blockquote>");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>A preview rendered as a flat JSON object of text fields, or null when it is plain text.</summary>
    private static Dictionary<string, string>? FlatFields(string preview)
    {
        if (!preview.TrimStart().StartsWith('{')) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(preview);
            return doc.RootElement.EnumerateObject().ToDictionary(
                x => x.Name,
                x => x.Value.ValueKind == System.Text.Json.JsonValueKind.String ? x.Value.GetString() ?? "" : x.Value.GetRawText());
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>"physicalDescription" → "Physical description".</summary>
    private static string FieldLabel(string key)
    {
        var words = string.Concat(key.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));
        return words.Length == 0 ? key : char.ToUpperInvariant(words[0]) + words[1..];
    }

    public static bool IsOpen(ChatProposal p) => p.Status is ChatProposalStatus.Pending or ChatProposalStatus.Failed;

    /// <summary>
    /// The suggestion buttons. Each open suggestion: a tick box (☐/☑ with its number and label), ✅ to apply just it and
    /// ✖️ to reject it. Alternatives (option A/B) are chosen as a whole with one button per option. Applied ones offer
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
