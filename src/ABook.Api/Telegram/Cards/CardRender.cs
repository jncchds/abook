using System.Text;
using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram.Cards;

/// <summary>Drawing the cards: the text (fields, long ones in expandable quotes) and the buttons.</summary>
public sealed partial class TelegramCardService
{
    private static TgButton B(Ctx ctx, string text, string op, object? arg = null, string? style = null) =>
        new(text, $"k{ctx.Card.Id}:{op}{(arg is null ? "" : ":" + arg)}", Style: style);

    private static List<IReadOnlyList<TgButton>> Rows(IEnumerable<TgButton> buttons, int perRow)
    {
        var rows = new List<IReadOnlyList<TgButton>>();
        var row = new List<TgButton>();
        foreach (var b in buttons)
        {
            row.Add(b);
            if (row.Count == perRow) { rows.Add(row); row = []; }
        }
        if (row.Count > 0) rows.Add(row);
        return rows;
    }

    private async Task<(string Html, IReadOnlyList<IReadOnlyList<TgButton>> Keyboard)> RenderAsync(Ctx ctx)
    {
        var s = ctx.State;
        var (html, rows) = s.Kind switch
        {
            "newbook" => RenderNewBook(ctx),
            "created" => ($"📘 <b>New book</b>", new List<IReadOnlyList<TgButton>>()),
            "books" => await RenderBooksAsync(ctx),
            "book" => await RenderBookAsync(ctx),
            "bible" => await RenderBibleAsync(ctx),
            "chars" => await RenderListAsync(ctx, "chars"),
            "threads" => await RenderListAsync(ctx, "threads"),
            "chapters" => await RenderListAsync(ctx, "chapters"),
            "char" or "thread" or "chapter" => await RenderItemAsync(ctx),
            "clars" => await RenderClarificationsAsync(ctx),
            _ => ("This card is no longer available.", new List<IReadOnlyList<TgButton>>()),
        };
        var sb = new StringBuilder(html);
        if (s.Awaiting is { } awaiting && AwaitingLabel(s, awaiting) is { } label)
            sb.Append("\n\n✍️ <b>Reply to this message</b> with the ").Append(H(label)).Append('.');
        if (s.Note is { Length: > 0 } note) sb.Append("\n\n").Append(H(note));
        return (sb.ToString(), rows);
    }

    private static string? AwaitingLabel(CardState s, string key) =>
        key == "note" ? "note"
        : key.StartsWith("answer.", StringComparison.Ordinal) ? "answer"
        : CardFields.For(s.Kind).FirstOrDefault(f => f.Key == key)?.Label.ToLowerInvariant();

    // ── Field cards ───────────────────────────────────────────────────────────

    /// <summary>
    /// The fields, with the draft's values in place of the saved ones (marked ✏️). Long fields go in expandable quotes,
    /// clipped to share the message; a clipped one gets a 📖 button to read it in full.
    /// </summary>
    private static (string Text, List<string> Clipped) Fields(IReadOnlyList<CardField> fields, IReadOnlyDictionary<string, string> saved, CardState s)
    {
        var sb = new StringBuilder();
        var clipped = new List<string>();
        var longCount = Math.Max(1, fields.Count(f => f.IsLong));
        var budget = Math.Max(160, TextBudget / longCount);
        foreach (var f in fields)
        {
            var edited = s.Draft.TryGetValue(f.Key, out var draft);
            var value = edited ? draft! : saved.GetValueOrDefault(f.Key, "");
            // In the new-book wizard everything is new: no "edited" marks.
            var mark = edited && s.Kind != "newbook" ? " ✏️" : "";
            var asking = s.Awaiting == f.Key ? " ⬅️" : "";
            if (f.IsLong)
            {
                sb.Append("\n<b>").Append(H(f.Label)).Append("</b>").Append(mark).Append(asking).Append('\n');
                if (value.Length == 0) { sb.Append("<i>—</i>\n"); continue; }
                var shown = Clip(value, budget);
                if (shown.Length < value.Length) clipped.Add(f.Key);
                sb.Append("<blockquote expandable>").Append(H(shown)).Append("</blockquote>");
            }
            else
                sb.Append("\n<b>").Append(H(f.Label)).Append(":</b> ")
                  .Append(value.Length > 0 ? H(Clip(value, 200)) : "<i>—</i>").Append(mark).Append(asking);
        }
        return (sb.ToString(), clipped);
    }

    /// <summary>✏️ per field, the choice buttons of the field being asked, 📖 for clipped fields, Save/Discard.</summary>
    private static List<IReadOnlyList<TgButton>> FieldButtons(Ctx ctx, IReadOnlyList<CardField> fields, List<string> clipped, bool canEdit = true)
    {
        var s = ctx.State;
        var rows = new List<IReadOnlyList<TgButton>>();
        if (canEdit && s.Awaiting is { } key && fields.FirstOrDefault(f => f.Key == key) is { Kind: FieldKind.Choice } choice)
        {
            rows.AddRange(Rows(choice.Choices!.Select(c => B(ctx, c, "set", $"{key}={c}")), 3));
            rows.Add([B(ctx, "✖️ Never mind", "x")]);
        }
        if (canEdit)
            rows.AddRange(Rows(fields.Select(f => B(ctx, $"✏️ {Short(f.Label)}", "ed", f.Key)), 3));
        if (clipped.Count > 0)
            rows.AddRange(Rows(clipped.Select(k => B(ctx, $"📖 {Short(fields.First(f => f.Key == k).Label)}", "full", k)), 3));
        if (s.Draft.Count > 0)
            rows.Add([B(ctx, "💾 Save", s.Note?.StartsWith("⚠️ It changed") == true ? "save!" : "save", style: "success"), B(ctx, "↩️ Discard", "undo")]);
        return rows;
    }

    /// <summary>A label short enough for a button: the first word of a long label.</summary>
    private static string Short(string label)
    {
        if (label.Length <= 14) return label;
        var space = label.IndexOf(' ');
        return space > 0 ? label[..space] : label[..14];
    }

    /// <summary>One long field in full, paged (3,500 characters a page).</summary>
    private static (string, List<IReadOnlyList<TgButton>>) RenderFull(Ctx ctx, string title, string label, string value, string back)
    {
        const int pageSize = 3500;
        var pages = Math.Max(1, (value.Length + pageSize - 1) / pageSize);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var part = value.Length == 0 ? "—" : value.Substring(page * pageSize, Math.Min(pageSize, value.Length - page * pageSize));
        var html = $"{title}\n\n<b>{H(label)}</b>{(pages > 1 ? $" · page {page + 1}/{pages}" : "")}\n<blockquote>{H(part)}</blockquote>";
        var nav = new List<TgButton>();
        if (page > 0) nav.Add(B(ctx, "◀", "pg", page - 1));
        nav.Add(B(ctx, "⬅️ Back", "view", back));
        if (page < pages - 1) nav.Add(B(ctx, "▶", "pg", page + 1));
        return (html, [nav]);
    }

    // ── New book ──────────────────────────────────────────────────────────────

    private (string, List<IReadOnlyList<TgButton>>) RenderNewBook(Ctx ctx)
    {
        var s = ctx.State;
        var interactive = s.Draft.GetValueOrDefault("mode") == nameof(BookMode.Interactive);
        var fields = CardFields.NewBook.Where(f => !(f.Key == "chapters" && interactive)).ToList();
        var (text, clipped) = Fields(fields, new Dictionary<string, string>(), s);
        var html = "📘 <b>New book</b>\n<i>Fill it in by replying to this message; tap ✏️ to change anything.</i>\n" + text;
        var rows = new List<IReadOnlyList<TgButton>>();
        if (s.Awaiting is { } key && fields.FirstOrDefault(f => f.Key == key) is { Kind: FieldKind.Choice } choice)
        {
            rows.AddRange(Rows(choice.Choices!.Select(c => B(ctx, c, "set", $"{key}={c}")), 3));
            if (key is "genre") rows.Add([B(ctx, "Skip", "set", "genre=")]);
        }
        if (s.Awaiting == "chapters")
            rows.Add(new[] { 5, 8, 12, 20 }.Select(n => B(ctx, n.ToString(), "set", $"chapters={n}")).ToList());
        rows.AddRange(Rows(fields.Where(f => s.Draft.ContainsKey(f.Key)).Select(f => B(ctx, $"✏️ {Short(f.Label)}", "ed", f.Key)), 3));
        var ready = s.Draft.ContainsKey("title") && s.Draft.ContainsKey("premise")
                    && (interactive || s.Draft.ContainsKey("mode") && s.Draft.ContainsKey("chapters"));
        var last = new List<TgButton>();
        if (ready) last.Add(B(ctx, "✅ Create", "create", style: "success"));
        last.Add(B(ctx, "✖️ Cancel", "nav", "books"));
        rows.Add(last);
        _ = clipped;
        return (html, rows);
    }

    // ── Books ─────────────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderBooksAsync(Ctx ctx)
    {
        var books = (await repo.GetAllAsync(ctx.Owner.Id)).Where(b => b.UserId == ctx.Owner.Id).OrderByDescending(b => b.UpdatedAt).ToList();
        var pages = Math.Max(1, (books.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var sb = new StringBuilder("📚 <b>Your books</b>");
        if (books.Count == 0) sb.Append("\n<i>None yet — start one with ➕ New book.</i>");
        else sb.Append("\n<i>Tap one to open it here; it gets its own topic.</i>");
        var rows = new List<IReadOnlyList<TgButton>>();
        foreach (var b in books.Skip(page * ListPage).Take(ListPage))
            rows.Add([B(ctx, $"{(b.Mode == BookMode.Interactive ? "💬" : "🤖")} {Clip(b.Title, 40)}", "open", b.Id)]);
        rows.Add(Pager(ctx, page, pages));
        rows.Add([B(ctx, "➕ New book", "nav", "newbook", "primary")]);
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
    }

    private static List<TgButton> Pager(Ctx ctx, int page, int pages)
    {
        var nav = new List<TgButton>();
        if (page > 0) nav.Add(B(ctx, "◀", "pg", page - 1));
        if (pages > 1) nav.Add(new TgButton($"{page + 1}/{pages}", $"k{ctx.Card.Id}:pg:{page}"));
        if (page < pages - 1) nav.Add(B(ctx, "▶", "pg", page + 1));
        return nav;
    }

    // ── Book ──────────────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderBookAsync(Ctx ctx)
    {
        var book = ctx.Book!;
        var s = ctx.State;
        var saved = CardFields.Of(book);
        if (s.View == "full" && s.Focus is { } focus)
            return RenderFull(ctx, $"📖 <b>{H(book.Title)}</b>", CardFields.Book.First(f => f.Key == focus).Label, s.Draft.GetValueOrDefault(focus, saved[focus]), "main");

        if (s.View == "presets")
        {
            var presets = (await repo.GetPresetsAsync(ctx.Owner.Id)).ToList();
            var prow = presets.Select(p => (IReadOnlyList<TgButton>)[B(ctx, $"{Clip(p.Name, 30)} · {Clip(p.ModelName, 25)}", "preset", p.Id)]).ToList();
            prow.Add([B(ctx, "⬅️ Back", "view", "main")]);
            return ("🧠 <b>LLM settings</b>\n<i>Copy a preset's settings into this book (later changes to the preset don't follow).</i>", prow);
        }

        var config = await repo.GetLlmConfigAsync(book.Id);
        var chapters = (await repo.GetChaptersAsync(book.Id)).ToList();
        var (text, clipped) = Fields(CardFields.Book, saved, s);
        var html = $"📖 <b>{H(book.Title)}</b>\n" +
                   $"{(book.Mode == BookMode.Interactive ? "💬 Interactive" : "🤖 Autonomous")} · {chapters.Count(c => c.Status == ChapterStatus.Done)}/{chapters.Count} chapters done" +
                   (BookContentService.IsBibleLocked(book) ? " · 🔒 bible locked" : "") +
                   $"\n🧠 {H(config is null ? "no LLM settings" : $"{config.Provider} · {config.ModelName}")}\n" + text;
        var rows = FieldButtons(ctx, CardFields.Book, clipped);
        rows.Add([B(ctx, "🧠 LLM preset", "view", "presets")]);
        rows.Add([B(ctx, "🌍 Story Bible", "nav", "bible"), B(ctx, "👤 Characters", "nav", "chars")]);
        rows.Add([B(ctx, "🧵 Plot threads", "nav", "threads"), B(ctx, "📚 Chapters", "nav", "chapters")]);
        rows.Add([B(ctx, "❓ Clarifications", "nav", "clars")]);
        if (book.Mode == BookMode.Interactive && !(await repo.GetChatMessagesAsync(book.Id, null, 1)).Any(m => m.Role != BookChatRole.Event))
            rows.Add([B(ctx, "💬 Start the book chat", "kick", style: "success")]);
        return (html, rows);
    }

    // ── Story Bible ───────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderBibleAsync(Ctx ctx)
    {
        var s = ctx.State;
        var bookId = ctx.Book!.Id;
        var bible = await repo.GetStoryBibleAsync(bookId);
        var saved = bible is null ? new Dictionary<string, string>() : BookContentRender.Fields(bible);
        var title = $"🌍 <b>Story Bible</b> · {H(Clip(ctx.Book.Title, 60))}" + (BookContentService.IsBibleLocked(ctx.Book) ? " · 🔒 locked" : "");

        if (s.View == "full" && s.Focus is { } focus)
            return RenderFull(ctx, title, CardFields.Bible.First(f => f.Key == focus).Label, s.Draft.GetValueOrDefault(focus, saved.GetValueOrDefault(focus, "")), "main");
        if (s.View == "versions")
        {
            var snaps = (await repo.GetStoryBibleSnapshotsAsync(bookId)).OrderByDescending(x => x.CreatedAt).ToList();
            return VersionList(ctx, title, snaps.Select(x => (x.Id, $"{x.CreatedAt:dd MMM HH:mm} · {x.Reason}")).ToList());
        }
        if (s.View == "version" && s.VersionId is int vid && await repo.GetStoryBibleSnapshotAsync(bookId, vid) is { } snap)
        {
            var fields = new Dictionary<string, string>
            {
                ["settingDescription"] = snap.SettingDescription, ["timePeriod"] = snap.TimePeriod, ["themes"] = snap.Themes,
                ["toneAndStyle"] = snap.ToneAndStyle, ["worldRules"] = snap.WorldRules, ["notes"] = snap.Notes,
            };
            var (vtext, _) = Fields(CardFields.Bible, fields, new CardState());
            return ($"{title}\n🕘 <b>Version of {snap.CreatedAt:dd MMM HH:mm}</b> · {H(snap.Reason)}\n{vtext}",
                [[B(ctx, "♻️ Restore this version", "restore", style: "danger"), B(ctx, "⬅️ Back", "view", "versions")]]);
        }

        var (text, clipped) = Fields(CardFields.Bible, saved, s);
        var rows = FieldButtons(ctx, CardFields.Bible, clipped, canEdit: !BookContentService.IsBibleLocked(ctx.Book));
        rows.Add([B(ctx, "🕘 Versions", "view", "versions"), B(ctx, "⬅️ Book", "nav", "book")]);
        return (title + "\n" + text, rows);
    }

    private static (string, List<IReadOnlyList<TgButton>>) VersionList(Ctx ctx, string title, List<(int Id, string Label)> versions)
    {
        var pages = Math.Max(1, (versions.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var rows = versions.Skip(page * ListPage).Take(ListPage)
            .Select(v => (IReadOnlyList<TgButton>)[B(ctx, Clip(v.Label, 50), "ver", v.Id)]).ToList();
        rows.Add(Pager(ctx, page, pages));
        rows.Add([B(ctx, "⬅️ Back", "view", "main")]);
        return ($"{title}\n🕘 <b>Versions</b> · {versions.Count}\n<i>Tap one to see it and restore it. Restoring keeps everything else in the history.</i>",
            rows.Where(r => r.Count > 0).ToList());
    }

    // ── Lists: characters, plot threads, chapters ─────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderListAsync(Ctx ctx, string kind)
    {
        var bookId = ctx.Book!.Id;
        var archived = ctx.State.View == "archived";
        List<(int Id, string Label)> items;
        string title;
        string itemKind;
        switch (kind)
        {
            case "chars":
                items = (await repo.GetAllCharacterCardsAsync(bookId)).Where(c => c.IsArchived == archived)
                    .OrderBy(c => c.Role).ThenBy(c => c.Name).Select(c => (c.Id, $"{c.Name} · {c.Role}")).ToList();
                title = "👤 <b>Characters</b>";
                itemKind = "char";
                break;
            case "threads":
                items = (await repo.GetAllPlotThreadsAsync(bookId)).Where(t => t.IsArchived == archived)
                    .OrderBy(t => t.Type).ThenBy(t => t.Name).Select(t => (t.Id, $"{t.Name} · {t.Status}")).ToList();
                title = "🧵 <b>Plot threads</b>";
                itemKind = "thread";
                break;
            default:
                items = (await repo.GetChaptersAsync(bookId, includeArchived: true)).Where(c => c.IsArchived == archived)
                    .OrderBy(c => c.Number).Select(c => (c.Id, $"{c.Number}. {c.Title} · {c.Status}")).ToList();
                title = "📚 <b>Chapters</b>";
                itemKind = "chapter";
                break;
        }
        var pages = Math.Max(1, (items.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var rows = items.Skip(page * ListPage).Take(ListPage)
            .Select(i => (IReadOnlyList<TgButton>)[B(ctx, Clip(i.Label, 50), "nav", $"{itemKind}.{i.Id}")]).ToList();
        rows.Add(Pager(ctx, page, pages));
        var last = new List<TgButton>();
        if (!archived) last.Add(B(ctx, "➕ Add", "add", style: "primary"));
        last.Add(archived ? B(ctx, "⬅️ Active", "view", "main") : B(ctx, "🗄 Archived", "view", "archived"));
        last.Add(B(ctx, "⬅️ Book", "nav", "book"));
        rows.Add(last);
        var html = $"{title} · {H(Clip(ctx.Book.Title, 60))}{(archived ? " · 🗄 archived" : "")}\n" +
                   (items.Count == 0 ? "<i>None.</i>" : $"<i>{items.Count} — tap one to open it.</i>");
        return (html, rows.Where(r => r.Count > 0).ToList());
    }

    // ── One character / plot thread / chapter ─────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderItemAsync(Ctx ctx)
    {
        var s = ctx.State;
        var bookId = ctx.Book!.Id;
        var fields = CardFields.For(s.Kind);
        var listKind = s.Kind == "char" ? "chars" : s.Kind == "thread" ? "threads" : "chapters";
        Dictionary<string, string> saved = new();
        string heading;
        bool archived = false;
        Chapter? chapter = null;

        switch (s.Kind)
        {
            case "char":
            {
                var card = s.ItemId is int id ? (await repo.GetAllCharacterCardsAsync(bookId)).FirstOrDefault(c => c.Id == id) : null;
                if (card is not null) { saved = BookContentRender.Fields(card); archived = card.IsArchived; }
                heading = card is null ? "👤 <b>New character</b>" : $"👤 <b>{H(card.Name)}</b>";
                break;
            }
            case "thread":
            {
                var thread = s.ItemId is int id ? (await repo.GetAllPlotThreadsAsync(bookId)).FirstOrDefault(t => t.Id == id) : null;
                if (thread is not null) { saved = BookContentRender.Fields(thread); archived = thread.IsArchived; }
                heading = thread is null ? "🧵 <b>New plot thread</b>" : $"🧵 <b>{H(thread.Name)}</b>";
                break;
            }
            default:
            {
                chapter = s.ItemId is int id ? await repo.GetChapterAsync(bookId, id) : null;
                if (chapter is not null) { saved = BookContentRender.Fields(chapter); archived = chapter.IsArchived; }
                heading = chapter is null
                    ? "📚 <b>New chapter</b> <i>(added after the last one)</i>"
                    : $"📚 <b>Chapter {chapter.Number}: {H(chapter.Title)}</b> · {chapter.Status}" +
                      (string.IsNullOrWhiteSpace(chapter.Content) ? "" : $" · {N(CountWords(chapter.Content))} words");
                break;
            }
        }
        heading += archived ? " · 🗄 archived" : "";

        if (s.View == "full" && s.Focus is { } focus)
            return RenderFull(ctx, heading, fields.First(f => f.Key == focus).Label, s.Draft.GetValueOrDefault(focus, saved.GetValueOrDefault(focus, "")), "main");
        if (s.View is "versions" or "version" && s.ItemId is int itemId)
            return await RenderItemVersionsAsync(ctx, heading, itemId);

        var (text, clipped) = Fields(fields, saved, s);
        var rows = FieldButtons(ctx, fields, clipped, canEdit: !archived);
        if (chapter is not null && !archived)
        {
            var actions = new List<TgButton>();
            if (!string.IsNullOrWhiteSpace(chapter.Content)) actions.Add(B(ctx, "📄 File", "file"));
            actions.Add(B(ctx, string.IsNullOrWhiteSpace(chapter.Content) ? "✍️ Write" : "✍️ Rewrite", "write"));
            if (!string.IsNullOrWhiteSpace(chapter.Content)) actions.Add(B(ctx, "🔍 Check", "check"));
            rows.Add(actions);
        }
        var last = new List<TgButton>();
        if (s.ItemId is not null)
        {
            last.Add(B(ctx, "🕘 Versions", "view", "versions"));
            last.Add(archived ? B(ctx, "♻️ Bring back", "unarch") : B(ctx, "🗄 Archive", "arch"));
        }
        last.Add(B(ctx, "⬅️ List", "nav", listKind));
        rows.Add(last);
        return (heading + "\n" + text, rows);
    }

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderItemVersionsAsync(Ctx ctx, string heading, int itemId)
    {
        var s = ctx.State;
        var bookId = ctx.Book!.Id;
        switch (s.Kind)
        {
            case "char":
            {
                var versions = (await repo.GetCharacterVersionsAsync(bookId, itemId)).OrderByDescending(v => v.VersionNumber).ToList();
                if (s.View == "version" && versions.FirstOrDefault(v => v.Id == s.VersionId) is { } v)
                {
                    var (vt, _) = Fields(CardFields.Character, new Dictionary<string, string>
                    {
                        ["name"] = v.Name, ["role"] = v.Role.ToString(), ["physicalDescription"] = v.PhysicalDescription,
                        ["personality"] = v.Personality, ["backstory"] = v.Backstory, ["goalMotivation"] = v.GoalMotivation,
                        ["arc"] = v.Arc, ["firstAppearanceChapterNumber"] = v.FirstAppearanceChapterNumber?.ToString() ?? "", ["notes"] = v.Notes,
                    }, new CardState());
                    return VersionView(ctx, heading, $"v{v.VersionNumber} · {v.CreatedAt:dd MMM HH:mm} · {v.CreatedBy}", vt);
                }
                return VersionList(ctx, heading, versions.Select(x => (x.Id, $"v{x.VersionNumber} · {x.CreatedAt:dd MMM HH:mm} · {x.CreatedBy}")).ToList());
            }
            case "thread":
            {
                var versions = (await repo.GetPlotThreadVersionsAsync(bookId, itemId)).OrderByDescending(v => v.VersionNumber).ToList();
                if (s.View == "version" && versions.FirstOrDefault(v => v.Id == s.VersionId) is { } v)
                {
                    var (vt, _) = Fields(CardFields.Thread, new Dictionary<string, string>
                    {
                        ["name"] = v.Name, ["description"] = v.Description, ["type"] = v.Type.ToString(), ["status"] = v.Status.ToString(),
                        ["introducedChapterNumber"] = v.IntroducedChapterNumber?.ToString() ?? "",
                        ["resolvedChapterNumber"] = v.ResolvedChapterNumber?.ToString() ?? "",
                    }, new CardState());
                    return VersionView(ctx, heading, $"v{v.VersionNumber} · {v.CreatedAt:dd MMM HH:mm} · {v.CreatedBy}", vt);
                }
                return VersionList(ctx, heading, versions.Select(x => (x.Id, $"v{x.VersionNumber} · {x.CreatedAt:dd MMM HH:mm} · {x.CreatedBy}")).ToList());
            }
            default:
            {
                var versions = (await repo.GetChapterVersionsAsync(itemId)).OrderByDescending(v => v.VersionNumber).ToList();
                if (s.View == "version" && versions.FirstOrDefault(v => v.Id == s.VersionId) is { } v)
                {
                    var start = Clip(v.Content, 600);
                    var body = $"\n<b>Title:</b> {H(v.Title)}\n<b>Status:</b> {v.Status} · {N(CountWords(v.Content))} words" +
                               (v.Content.Length > 0 ? $"\n<b>Beginning</b>\n<blockquote expandable>{H(start)}</blockquote>" : "") +
                               (v.Outline.Length > 0 ? $"\n<b>Outline</b>\n<blockquote expandable>{H(Clip(v.Outline, 900))}</blockquote>" : "");
                    return VersionView(ctx, heading, $"v{v.VersionNumber} · {v.CreatedAt:dd MMM HH:mm} · {v.CreatedBy}{(v.IsActive ? " · current" : "")}", body);
                }
                return VersionList(ctx, heading, versions.Select(x => (x.Id,
                    $"v{x.VersionNumber}{(x.IsActive ? " ●" : "")} · {x.CreatedAt:dd MMM HH:mm} · {x.CreatedBy} · {N(CountWords(x.Content))} w")).ToList());
            }
        }
    }

    private static (string, List<IReadOnlyList<TgButton>>) VersionView(Ctx ctx, string heading, string label, string body) =>
        ($"{heading}\n🕘 <b>{H(label)}</b>\n{body}",
            [[B(ctx, "♻️ Restore this version", "restore", style: "danger"), B(ctx, "⬅️ Versions", "view", "versions")]]);

    // ── Clarifications ────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderClarificationsAsync(Ctx ctx)
    {
        var all = (await repo.GetClarificationsAsync(ctx.Book!.Id)).OrderBy(c => c.IsOpen ? 0 : 1).ThenByDescending(c => c.Id).ToList();
        var pages = Math.Max(1, (all.Count + 5) / 6);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var shown = all.Skip(page * 6).Take(6).ToList();
        var sb = new StringBuilder($"❓ <b>Clarifications</b> · {H(Clip(ctx.Book.Title, 60))}\n<i>The agents follow these answers and notes.</i>\n");
        if (all.Count == 0) sb.Append("\n<i>None yet.</i>");
        var rows = new List<IReadOnlyList<TgButton>>();
        var i = page * 6;
        foreach (var c in shown)
        {
            i++;
            sb.Append($"\n<b>{i}. {H(Clip(c.Question, 160))}</b>\n")
              .Append(c.IsOpen ? "<i>open — not answered yet</i>\n" : $"<blockquote expandable>{H(Clip(c.Answer, 400))}</blockquote>");
            rows.Add([B(ctx, $"✏️ {i}. {(c.IsOpen ? "Answer" : "Change answer")}", "ans", c.Id), B(ctx, "🗄", "skip", c.Id)]);
        }
        rows.Add(Pager(ctx, page, pages));
        rows.Add([B(ctx, "📝 Add a note", "note", style: "primary"), B(ctx, "⬅️ Book", "nav", "book")]);
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
    }
}
