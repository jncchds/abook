using System.Text;
using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram.Cards;

/// <summary>
/// Drawing the cards as rich messages: each field with an inline <i>edit</i> link next to it, long fields in
/// collapsible sections shown in full, list items that are links themselves. The keyboard under the card keeps
/// navigation, the choices of the field being asked, and Save / Discard.
/// </summary>
public sealed partial class TelegramCardService
{
    /// <summary>Longest a single field is shown (a rich message holds 32,768 characters in all).</summary>
    private const int FieldMax = 6000;

    private static string Data(Ctx ctx, string op, object? arg = null) => $"k{ctx.Card.Id}:{op}{(arg is null ? "" : ":" + arg)}";

    private static TgButton B(Ctx ctx, string text, string op, object? arg = null, string? style = null) =>
        new(text, Data(ctx, op, arg), Style: style);

    /// <summary>An inline link inside the card's text that works like a button.</summary>
    private static string L(Ctx ctx, string label, string op, object? arg = null) => TelegramRich.Link(label, Data(ctx, op, arg));

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
            "created" => ("<h3>📘 New book</h3>", new List<IReadOnlyList<TgButton>>()),
            "books" => await RenderBooksAsync(ctx),
            "book" => await RenderBookAsync(ctx),
            "bible" => await RenderBibleAsync(ctx),
            "chars" => await RenderListAsync(ctx, "chars"),
            "threads" => await RenderListAsync(ctx, "threads"),
            "chapters" => await RenderListAsync(ctx, "chapters"),
            "sections" => await RenderSectionsAsync(ctx),
            "section" => await RenderSectionAsync(ctx),
            "char" or "thread" or "chapter" => await RenderItemAsync(ctx),
            "clars" => await RenderClarificationsAsync(ctx),
            _ => ("<p>This card is no longer available.</p>", new List<IReadOnlyList<TgButton>>()),
        };
        var sb = new StringBuilder(html);
        if (s.Awaiting is { } awaiting && AwaitingLabel(s, awaiting) is { } label)
            sb.Append("<p>✍️ <b>Reply to this message</b> with the ").Append(H(label)).Append(".</p>");
        if (s.Note is { Length: > 0 } note) sb.Append("<footer>").Append(H(note)).Append("</footer>");
        return (sb.ToString(), rows);
    }

    private static string? AwaitingLabel(CardState s, string key) =>
        key == "note" ? "note"
        : key.StartsWith("answer.", StringComparison.Ordinal) ? "answer"
        : CardFields.For(s.Kind).FirstOrDefault(f => f.Key == key)?.Label.ToLowerInvariant();

    // ── Field cards ───────────────────────────────────────────────────────────

    /// <summary>
    /// The fields, with the draft's values in place of the saved ones (marked ✏️) and an inline <i>edit</i> link each.
    /// Short ones on one line; long ones in a collapsible section (open when edited, asked or short). Returns the fields
    /// shown clipped (very long ones), which get a 📖 button to read in full.
    /// </summary>
    private static (string Html, List<string> Clipped) Fields(
        Ctx ctx, IReadOnlyList<CardField> fields, IReadOnlyDictionary<string, string> saved, bool canEdit = true)
    {
        var s = ctx.State;
        var sb = new StringBuilder();
        var clipped = new List<string>();
        foreach (var f in fields)
        {
            var edited = s.Draft.TryGetValue(f.Key, out var draft);
            var value = edited ? draft! : saved.GetValueOrDefault(f.Key, "");
            var mark = edited && s.Kind != "newbook" ? " ✏️" : "";
            var asking = s.Awaiting == f.Key ? " ⬅️" : "";
            var edit = canEdit ? " " + L(ctx, value.Length == 0 ? "add" : "edit", "ed", f.Key) : "";
            if (f.IsLong && value.Length > 0)
            {
                var open = edited || asking.Length > 0 || value.Length < 300;
                var shown = Clip(value, FieldMax);
                if (shown.Length < value.Length) clipped.Add(f.Key);
                sb.Append(open ? "<details open>" : "<details>")
                  .Append("<summary><b>").Append(H(f.Label)).Append("</b>").Append(mark).Append(asking).Append("</summary>")
                  .Append("<p>").Append(Lines(shown)).Append("</p>");
                if (edit.Length > 0) sb.Append("<p>").Append(edit.Trim()).Append("</p>");
                sb.Append("</details>");
            }
            else
                sb.Append("<p><b>").Append(H(f.Label)).Append(":</b> ")
                  .Append(value.Length > 0 ? H(Clip(value, 300)) : "<i>—</i>").Append(mark).Append(asking).Append(edit).Append("</p>");
        }
        return (sb.ToString(), clipped);
    }

    /// <summary>Under a field card: the choices of the field being asked, 📖 for clipped fields, Save / Discard.</summary>
    private static List<IReadOnlyList<TgButton>> FieldButtons(Ctx ctx, IReadOnlyList<CardField> fields, List<string> clipped, bool canEdit = true)
    {
        var s = ctx.State;
        var rows = new List<IReadOnlyList<TgButton>>();
        if (canEdit && s.Awaiting is { } key && fields.FirstOrDefault(f => f.Key == key) is { Kind: FieldKind.Choice } choice)
        {
            rows.AddRange(Rows(choice.Choices!.Select(c => B(ctx, c, "set", $"{key}={c}")), 3));
            rows.Add([B(ctx, "✖️ Never mind", "x")]);
        }
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

    /// <summary>One very long field in full, paged (12,000 characters a page).</summary>
    private static (string, List<IReadOnlyList<TgButton>>) RenderFull(Ctx ctx, string title, string label, string value, string back)
    {
        const int pageSize = 12000;
        var pages = Math.Max(1, (value.Length + pageSize - 1) / pageSize);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var part = value.Length == 0 ? "—" : value.Substring(page * pageSize, Math.Min(pageSize, value.Length - page * pageSize));
        var html = $"{title}<h4>{H(label)}{(pages > 1 ? $" · page {page + 1}/{pages}" : "")}</h4><blockquote>{Lines(part)}</blockquote>";
        var nav = new List<TgButton>();
        if (page > 0) nav.Add(B(ctx, "◀", "pg", page - 1));
        nav.Add(B(ctx, "⬅️ Back", "view", back));
        if (page < pages - 1) nav.Add(B(ctx, "▶", "pg", page + 1));
        return (html, [nav]);
    }

    private static List<TgButton> Pager(Ctx ctx, int page, int pages)
    {
        var nav = new List<TgButton>();
        if (page > 0) nav.Add(B(ctx, "◀", "pg", page - 1));
        if (pages > 1) nav.Add(new TgButton($"{page + 1}/{pages}", Data(ctx, "pg", page)));
        if (page < pages - 1) nav.Add(B(ctx, "▶", "pg", page + 1));
        return nav;
    }

    // ── New book ──────────────────────────────────────────────────────────────

    private (string, List<IReadOnlyList<TgButton>>) RenderNewBook(Ctx ctx)
    {
        var s = ctx.State;
        var interactive = s.Draft.GetValueOrDefault("mode") == nameof(BookMode.Interactive);
        var fields = CardFields.NewBook.Where(f => !(f.Key == "chapters" && interactive)).ToList();
        var (text, _) = Fields(ctx, fields, new Dictionary<string, string>());
        var html = "<h3>📘 New book</h3><p><i>Fill it in by replying to this message; tap edit to change anything.</i></p>" + text;
        var rows = new List<IReadOnlyList<TgButton>>();
        if (s.Awaiting is { } key && fields.FirstOrDefault(f => f.Key == key) is { Kind: FieldKind.Choice } choice)
        {
            rows.AddRange(Rows(choice.Choices!.Select(c => B(ctx, c, "set", $"{key}={c}")), 3));
            if (key is "genre") rows.Add([B(ctx, "Skip", "set", "genre=")]);
        }
        if (s.Awaiting == "chapters")
            rows.Add(new[] { 5, 8, 12, 20 }.Select(n => B(ctx, n.ToString(), "set", $"chapters={n}")).ToList());
        var ready = s.Draft.ContainsKey("title") && s.Draft.ContainsKey("premise")
                    && (interactive || s.Draft.ContainsKey("mode") && s.Draft.ContainsKey("chapters"));
        var last = new List<TgButton>();
        if (ready) last.Add(B(ctx, "✅ Create", "create", style: "success"));
        last.Add(B(ctx, "✖️ Cancel", "nav", "books"));
        rows.Add(last);
        return (html, rows);
    }

    // ── Books ─────────────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderBooksAsync(Ctx ctx)
    {
        var books = (await repo.GetAllAsync(ctx.Owner.Id)).Where(b => b.UserId == ctx.Owner.Id).OrderByDescending(b => b.UpdatedAt).ToList();
        var pages = Math.Max(1, (books.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var sb = new StringBuilder("<h3>📚 Your books</h3>");
        if (books.Count == 0) sb.Append("<p><i>None yet — start one with ➕ New book.</i></p>");
        else
        {
            sb.Append("<p><i>Tap one to see and edit it here. To get a book's updates in a chat, open it and use 🔗 Connect a chat.</i></p><ul>");
            foreach (var b in books.Skip(page * ListPage).Take(ListPage))
                sb.Append("<li>").Append(b.Mode == BookMode.Interactive ? "💬 " : "🤖 ").Append(L(ctx, Clip(b.Title, 60), "open", b.Id))
                  .Append(b.Genre.Length > 0 ? $" · <i>{H(b.Genre)}</i>" : "").Append("</li>");
            sb.Append("</ul>");
        }
        List<IReadOnlyList<TgButton>> rows = [Pager(ctx, page, pages), new[] { B(ctx, "➕ New book", "nav", "newbook", "primary") }];
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
    }

    // ── Book ──────────────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderBookAsync(Ctx ctx)
    {
        var book = ctx.Book!;
        var s = ctx.State;
        var saved = CardFields.Of(book);
        var title = $"<h3>📖 {H(book.Title)}</h3>";
        if (s.View == "full" && s.Focus is { } focus)
            return RenderFull(ctx, title, CardFields.Book.First(f => f.Key == focus).Label, s.Draft.GetValueOrDefault(focus, saved[focus]), "main");

        if (s.View == "presets")
        {
            var presets = (await repo.GetPresetsAsync(ctx.Owner.Id)).ToList();
            var list = new StringBuilder("<ul>");
            foreach (var p in presets)
                list.Append("<li>").Append(L(ctx, Clip(p.Name, 40), "preset", p.Id)).Append(" · <i>").Append(H(Clip(p.ModelName, 40))).Append("</i></li>");
            list.Append("</ul>");
            return ($"{title}<h4>🧠 LLM settings</h4><p><i>Copy a preset's settings into this book (later changes to the preset don't follow).</i></p>" +
                    (presets.Count == 0 ? "<p><i>No presets — make one in abook.</i></p>" : list.ToString()),
                [[B(ctx, "⬅️ Back", "view", "main")]]);
        }

        if (s.View == "chats" && ctx.IsOwner) return await RenderChatsAsync(ctx, title);

        var config = await repo.GetLlmConfigAsync(book.Id);
        var chapters = (await repo.GetChaptersAsync(book.Id)).ToList();
        // The book's own settings are Control's; the rest of the card is open to Participate.
        var (text, clipped) = Fields(ctx, CardFields.Book, saved, canEdit: ctx.CanControl);
        var html = title +
                   $"<p>{(book.Mode == BookMode.Interactive ? "💬 Interactive" : "🤖 Autonomous")} · " +
                   $"{chapters.Count(c => c.Status == ChapterStatus.Done)}/{chapters.Count} chapters done" +
                   (BookContentService.IsBibleLocked(book) ? " · 🔒 bible locked" : "") + "<br>" +
                   $"🧠 {H(config is null ? "no LLM settings" : $"{config.Provider} · {config.ModelName}")}" +
                   (ctx.IsOwner ? $" · {L(ctx, "change", "view", "presets")}" : "") + "</p>" + text;
        var rows = FieldButtons(ctx, CardFields.Book, clipped, canEdit: ctx.CanControl);
        rows.Add([B(ctx, "🌍 Story Bible", "nav", "bible"), B(ctx, "👤 Characters", "nav", "chars")]);
        rows.Add([B(ctx, "🧵 Plot threads", "nav", "threads"), B(ctx, "🧩 Sections", "nav", "sections")]);
        rows.Add([B(ctx, "📚 Chapters", "nav", "chapters"), B(ctx, "❓ Clarifications", "nav", "clars")]);
        if (book.Mode == BookMode.Interactive && !(await repo.GetChatMessagesAsync(book.Id, null, 1)).Any(m => m.Role != BookChatRole.Event))
            rows.Add([B(ctx, "💬 Start the book chat", "kick", style: "success")]);
        else if (book.Mode == BookMode.Interactive && !BookContentService.IsBibleLocked(book) && ctx.CanControl)
            rows.Add([B(ctx, "🔒 Lock bible & start writing", "lock")]);
        rows.Add([B(ctx, "⬇️ EPUB", "exp", "epub"), B(ctx, "FB2", "exp", "fb2"), B(ctx, "HTML", "exp", "html"), B(ctx, "🗂 Metadata", "exp", "metadata")]);
        var last = new List<TgButton>();
        if (ctx.IsOwner) last.Add(B(ctx, "🔗 Chats", "view", "chats"));
        if (ctx.IsOwner && ctx.InPrivateChat) last.Add(B(ctx, "📚 All books", "nav", "books"));
        if (last.Count > 0) rows.Add(last);
        return (html, rows);
    }

    /// <summary>
    /// The chats that follow the book (each with a disconnect link) and 🔗 buttons that make a one-time code to connect
    /// another — the same codes as Book Settings → Telegram. Opening a book in a card never follows it anywhere.
    /// </summary>
    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderChatsAsync(Ctx ctx, string title)
    {
        var followed = (await tg.GetBindingsForBookAsync(ctx.Book!.Id)).Where(b => b.IsActive).ToList();
        var sb = new StringBuilder(title).Append("<h4>🔗 Chats that follow the book</h4>");
        if (followed.Count == 0) sb.Append("<p><i>None yet.</i></p>");
        else
        {
            sb.Append("<ul>");
            foreach (var b in followed)
                sb.Append("<li>").Append(H(Clip(b.ChatTitle, 80))).Append(" · <i>").Append(b.Level).Append("</i> · ")
                  .Append(L(ctx, "disconnect", "unbind", b.Id)).Append("</li>");
            sb.Append("</ul>");
        }
        sb.Append("<p><i>To connect a chat, pick what everyone there may do: the bot sends a one-time code (15 minutes) with a link ")
          .Append("for this private chat, one to add the bot to a group, and a command for a topic or channel.</i></p>");
        return (sb.ToString(),
        [
            [B(ctx, "🔗 Read", "code", nameof(TelegramAccessLevel.Read)), B(ctx, "🔗 Participate", "code", nameof(TelegramAccessLevel.Participate)),
             B(ctx, "🔗 Control", "code", nameof(TelegramAccessLevel.Control))],
            [B(ctx, "⬅️ Book", "view", "main")],
        ]);
    }

    // ── Story Bible ───────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderBibleAsync(Ctx ctx)
    {
        var s = ctx.State;
        var bookId = ctx.Book!.Id;
        var bible = await repo.GetStoryBibleAsync(bookId);
        var saved = bible is null ? new Dictionary<string, string>() : BookContentRender.Fields(bible);
        var locked = BookContentService.IsBibleLocked(ctx.Book);
        var title = $"<h3>🌍 Story Bible</h3><p><i>{H(Clip(ctx.Book.Title, 80))}</i>{(locked ? " · 🔒 locked since writing started" : "")}</p>";

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
            var (vtext, _) = Fields(ctx, CardFields.Bible, fields, canEdit: false);
            return VersionView(ctx, title, $"{snap.CreatedAt:dd MMM HH:mm} · {snap.Reason}", vtext);
        }

        var (text, clipped) = Fields(ctx, CardFields.Bible, saved, canEdit: !locked);
        var rows = FieldButtons(ctx, CardFields.Bible, clipped, canEdit: !locked);
        rows.Add([B(ctx, "🕘 Versions", "view", "versions"), B(ctx, "⬅️ Book", "nav", "book")]);
        return (title + text, rows);
    }

    private static (string, List<IReadOnlyList<TgButton>>) VersionList(Ctx ctx, string title, List<(int Id, string Label)> versions)
    {
        var pages = Math.Max(1, (versions.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var sb = new StringBuilder(title).Append("<h4>🕘 Versions · ").Append(versions.Count).Append("</h4>")
            .Append("<p><i>Tap one to see it and restore it. Restoring keeps everything else in the history.</i></p><ul>");
        foreach (var v in versions.Skip(page * ListPage).Take(ListPage))
            sb.Append("<li>").Append(L(ctx, Clip(v.Label, 70), "ver", v.Id)).Append("</li>");
        sb.Append("</ul>");
        List<IReadOnlyList<TgButton>> rows = [Pager(ctx, page, pages), new[] { B(ctx, "⬅️ Back", "view", "main") }];
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
    }

    private static (string, List<IReadOnlyList<TgButton>>) VersionView(Ctx ctx, string heading, string label, string body) =>
        ($"{heading}<h4>🕘 {H(label)}</h4>{body}",
            [ctx.CanControl
                ? [B(ctx, "♻️ Restore this version", "restore", style: "danger"), B(ctx, "⬅️ Versions", "view", "versions")]
                : [B(ctx, "⬅️ Versions", "view", "versions")]]);

    // ── Lists: characters, plot threads, chapters ─────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderListAsync(Ctx ctx, string kind)
    {
        var bookId = ctx.Book!.Id;
        var archived = ctx.State.View == "archived";
        List<(int Id, string Name, string Detail)> items;
        string title;
        string itemKind;
        switch (kind)
        {
            case "chars":
                items = (await repo.GetAllCharacterCardsAsync(bookId)).Where(c => c.IsArchived == archived)
                    .OrderBy(c => c.Role).ThenBy(c => c.Name).Select(c => (c.Id, c.Name, c.Role.ToString())).ToList();
                title = "👤 Characters";
                itemKind = "char";
                break;
            case "threads":
                items = (await repo.GetAllPlotThreadsAsync(bookId)).Where(t => t.IsArchived == archived)
                    .OrderBy(t => t.Type).ThenBy(t => t.Name).Select(t => (t.Id, t.Name, $"{t.Type} · {t.Status}")).ToList();
                title = "🧵 Plot threads";
                itemKind = "thread";
                break;
            default:
                // Already in book order; an archived chapter shows the number it last had.
                items = (await repo.GetChaptersAsync(bookId, includeArchived: true)).Where(c => c.IsArchived == archived)
                    .Select(c => (c.Id, archived ? $"(was {c.Number}) {c.Title}" : $"{c.Number}. {c.Title}", c.Status.ToString())).ToList();
                title = "📚 Chapters";
                itemKind = "chapter";
                break;
        }
        var pages = Math.Max(1, (items.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var sb = new StringBuilder($"<h3>{title}{(archived ? " · 🗄 archived" : "")}</h3><p><i>{H(Clip(ctx.Book.Title, 80))}</i></p>");
        if (items.Count == 0) sb.Append("<p><i>None.</i></p>");
        else
        {
            sb.Append("<ul>");
            foreach (var i in items.Skip(page * ListPage).Take(ListPage))
                sb.Append("<li>").Append(L(ctx, Clip(i.Name, 60), "nav", $"{itemKind}.{i.Id}")).Append(" · <i>").Append(H(i.Detail)).Append("</i></li>");
            sb.Append("</ul>");
        }
        var last = new List<TgButton>();
        if (!archived) last.Add(B(ctx, "➕ Add", "add", style: "primary"));
        last.Add(archived ? B(ctx, "⬅️ Active", "view", "main") : B(ctx, "🗄 Archived", "view", "archived"));
        last.Add(B(ctx, "⬅️ Book", "nav", "book"));
        var rows = new List<IReadOnlyList<TgButton>> { Pager(ctx, page, pages), last };
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
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
                heading = card is null ? "<h3>👤 New character</h3>" : $"<h3>👤 {H(card.Name)}</h3>";
                break;
            }
            case "thread":
            {
                var thread = s.ItemId is int id ? (await repo.GetAllPlotThreadsAsync(bookId)).FirstOrDefault(t => t.Id == id) : null;
                if (thread is not null) { saved = BookContentRender.Fields(thread); archived = thread.IsArchived; }
                heading = thread is null ? "<h3>🧵 New plot thread</h3>" : $"<h3>🧵 {H(thread.Name)}</h3>";
                break;
            }
            default:
            {
                chapter = s.ItemId is int id ? await repo.GetChapterAsync(bookId, id) : null;
                if (chapter is not null) { saved = BookContentRender.Fields(chapter); archived = chapter.IsArchived; }
                var target = chapter is null && s.SectionId is int sid ? await repo.GetSectionAsync(bookId, sid) : null;
                heading = chapter is null
                    ? "<h3>📚 New chapter</h3><p><i>" +
                      (target is null ? "Added after the last one, as a section of its own." : $"Added at the end of Section {target.Number}: {H(Clip(target.Title, 80))}.") +
                      "</i></p>"
                    : $"<h3>📚 Chapter {chapter.Number}: {H(chapter.Title)}</h3><p>{chapter.Status}" +
                      (string.IsNullOrWhiteSpace(chapter.Content) ? " · not written yet" : $" · {N(CountWords(chapter.Content))} words") +
                      await SectionLineAsync(ctx, chapter) + "</p>";
                break;
            }
        }
        if (archived) heading += "<p>🗄 <i>Archived</i></p>";

        if (s.View == "full" && s.Focus is { } focus)
            return RenderFull(ctx, heading, fields.First(f => f.Key == focus).Label, s.Draft.GetValueOrDefault(focus, saved.GetValueOrDefault(focus, "")), "main");
        if (s.View is "versions" or "version" && s.ItemId is int itemId)
            return await RenderItemVersionsAsync(ctx, heading, itemId);

        var (text, clipped) = Fields(ctx, fields, saved, canEdit: !archived);
        var rows = FieldButtons(ctx, fields, clipped, canEdit: !archived);
        if (chapter is not null && !archived)
        {
            var actions = new List<TgButton>();
            if (!string.IsNullOrWhiteSpace(chapter.Content)) actions.Add(B(ctx, "📄 File", "file"));
            if (ctx.CanControl)
            {
                actions.Add(B(ctx, string.IsNullOrWhiteSpace(chapter.Content) ? "✍️ Write" : "✍️ Rewrite", "write"));
                if (!string.IsNullOrWhiteSpace(chapter.Content)) actions.Add(B(ctx, "🔍 Check", "check"));
            }
            if (actions.Count > 0) rows.Add(actions);
            if (ctx.CanControl)
                rows.Add([B(ctx, "⬆️ Move up", "mv", "up"), B(ctx, "⬇️ Move down", "mv", "down")]);
        }
        var last = new List<TgButton>();
        if (s.ItemId is not null)
        {
            last.Add(B(ctx, "🕘 Versions", "view", "versions"));
            if (ctx.CanControl) last.Add(archived ? B(ctx, "♻️ Bring back", "unarch") : B(ctx, "🗄 Archive", "arch"));
        }
        last.Add(B(ctx, "⬅️ List", "nav", listKind));
        rows.Add(last);
        return (heading + text, rows);
    }

    /// <summary>"· in Section 3 (part 2 of 3)" with a link to the section card — empty for a chapter without one.</summary>
    private async Task<string> SectionLineAsync(Ctx ctx, Chapter chapter)
    {
        if (chapter.SectionId is not int sid || await repo.GetSectionAsync(ctx.Book!.Id, sid) is not { } section) return "";
        var siblings = (await repo.GetChaptersAsync(ctx.Book.Id)).Where(c => c.SectionId == sid).ToList();
        var part = siblings.Count > 1 && siblings.FindIndex(c => c.Id == chapter.Id) is var i and >= 0
            ? $" (part {i + 1} of {siblings.Count})" : "";
        return "<br>🧩 " + L(ctx, $"Section {section.Number}", "nav", $"section.{sid}") + part;
    }

    // ── Sections ──────────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderSectionsAsync(Ctx ctx)
    {
        var bookId = ctx.Book!.Id;
        var archived = ctx.State.View == "archived";
        var chapters = (await repo.GetChaptersAsync(bookId, includeArchived: true)).ToList();
        var sections = (await repo.GetSectionsAsync(bookId, includeArchived: true)).Where(x => x.IsArchived == archived).ToList();
        var pages = Math.Max(1, (sections.Count + ListPage - 1) / ListPage);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var sb = new StringBuilder($"<h3>🧩 Sections{(archived ? " · 🗄 archived" : "")}</h3><p><i>{H(Clip(ctx.Book.Title, 80))}</i></p>");
        if (!archived) sb.Append("<p><i>The parts of the plan, in order; each has one chapter or several.</i></p>");
        if (sections.Count == 0) sb.Append("<p><i>None.</i></p>");
        else
        {
            sb.Append("<ul>");
            foreach (var x in sections.Skip(page * ListPage).Take(ListPage))
            {
                var own = chapters.Where(c => c.SectionId == x.Id && c.IsArchived == archived).ToList();
                var written = own.Count(c => !string.IsNullOrWhiteSpace(c.Content));
                var label = archived ? $"(was {x.Number}) {x.Title}" : $"{x.Number}. {x.Title}";
                sb.Append("<li>").Append(L(ctx, Clip(label, 60), "nav", $"section.{x.Id}")).Append(" · <i>")
                  .Append(own.Count == 1 ? "1 chapter" : $"{own.Count} chapters")
                  .Append(written > 0 ? $", {written} written" : "").Append("</i></li>");
            }
            sb.Append("</ul>");
        }
        var last = new List<TgButton>();
        if (!archived) last.Add(B(ctx, "➕ Add", "add", style: "primary"));
        last.Add(archived ? B(ctx, "⬅️ Active", "view", "main") : B(ctx, "🗄 Archived", "view", "archived"));
        last.Add(B(ctx, "⬅️ Book", "nav", "book"));
        var rows = new List<IReadOnlyList<TgButton>> { Pager(ctx, page, pages), last };
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
    }

    /// <summary>
    /// One section: its plan (editable — for a section with one chapter that is the chapter's plan), its chapters as
    /// links, and for Control: move, break down, archive or bring back.
    /// </summary>
    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderSectionAsync(Ctx ctx)
    {
        var s = ctx.State;
        var bookId = ctx.Book!.Id;
        var section = s.ItemId is int id ? await repo.GetSectionAsync(bookId, id) : null;
        if (section is null) return ("<p>This section is gone.</p>", [[B(ctx, "⬅️ Sections", "nav", "sections")]]);
        var archived = section.IsArchived;
        var chapters = (await repo.GetChaptersAsync(bookId, includeArchived: true)).Where(c => c.SectionId == section.Id).ToList();
        var active = chapters.Where(c => !c.IsArchived).ToList();
        var saved = BookContentRender.Fields(section);
        var heading = $"<h3>🧩 Section {section.Number}: {H(section.Title)}</h3>" +
                      (archived ? "<p>🗄 <i>Archived</i></p>" : "") +
                      (active.Count == 1 ? "<p><i>One chapter: the section's plan is the chapter's plan.</i></p>"
                       : active.Count > 1 ? "<p><i>The overall plan; each chapter has its own detailed outline.</i></p>" : "");
        if (s.View == "full" && s.Focus is { } focus)
            return RenderFull(ctx, heading, CardFields.Section.First(f => f.Key == focus).Label, s.Draft.GetValueOrDefault(focus, saved.GetValueOrDefault(focus, "")), "main");

        var list = new StringBuilder("<h4>📚 Chapters</h4><ul>");
        foreach (var c in chapters.Where(c => c.IsArchived == archived))
            list.Append("<li>").Append(L(ctx, Clip($"{c.Number}. {c.Title}", 60), "nav", $"chapter.{c.Id}")).Append(" · <i>")
                .Append(string.IsNullOrWhiteSpace(c.Content) ? "not written" : $"{N(CountWords(c.Content))} words").Append("</i></li>");
        list.Append("</ul>");

        var (text, clipped) = Fields(ctx, CardFields.Section, saved, canEdit: !archived);
        var rows = FieldButtons(ctx, CardFields.Section, clipped, canEdit: !archived);
        if (!archived)
        {
            rows.Add([B(ctx, "➕ Chapter in this section", "addin")]);
            if (ctx.CanControl)
            {
                var controls = new List<TgButton> { B(ctx, "⬆️ Move up", "mv", "up"), B(ctx, "⬇️ Move down", "mv", "down") };
                rows.Add(controls);
                if (active.Count > 0 && active.All(c => string.IsNullOrWhiteSpace(c.Content)))
                    rows.Add([B(ctx, "⋔ Break down (planner decides)", "brk"), B(ctx, "⋔ into 2", "brk", 2), B(ctx, "3", "brk", 3)]);
                // Autonomous books: the section as Write Book writes it, or a check of each written chapter.
                if (ctx.Book!.Mode == BookMode.Autonomous)
                {
                    var runs = new List<TgButton>();
                    if (active.Any(c => c.Status != ChapterStatus.Done)) runs.Add(B(ctx, "✍️ Write section", "wsec"));
                    if (active.Any(c => c.Status is ChapterStatus.Done or ChapterStatus.Review)) runs.Add(B(ctx, "🔍 Check section", "csec"));
                    if (runs.Count > 0) rows.Add(runs);
                }
            }
        }
        var last = new List<TgButton>();
        if (ctx.CanControl) last.Add(archived ? B(ctx, "♻️ Bring back", "unarch") : B(ctx, "🗄 Archive", "arch"));
        last.Add(B(ctx, "⬅️ Sections", "nav", "sections"));
        rows.Add(last);
        return (heading + text + list, rows);
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
                    var (vt, _) = Fields(ctx, CardFields.Character, new Dictionary<string, string>
                    {
                        ["name"] = v.Name, ["role"] = v.Role.ToString(), ["physicalDescription"] = v.PhysicalDescription,
                        ["personality"] = v.Personality, ["backstory"] = v.Backstory, ["goalMotivation"] = v.GoalMotivation,
                        ["arc"] = v.Arc, ["firstAppearanceChapterNumber"] = v.FirstAppearanceChapterNumber?.ToString() ?? "", ["notes"] = v.Notes,
                    }, canEdit: false);
                    return VersionView(ctx, heading, $"v{v.VersionNumber} · {v.CreatedAt:dd MMM HH:mm} · {v.CreatedBy}", vt);
                }
                return VersionList(ctx, heading, versions.Select(x => (x.Id, $"v{x.VersionNumber} · {x.CreatedAt:dd MMM HH:mm} · {x.CreatedBy}")).ToList());
            }
            case "thread":
            {
                var versions = (await repo.GetPlotThreadVersionsAsync(bookId, itemId)).OrderByDescending(v => v.VersionNumber).ToList();
                if (s.View == "version" && versions.FirstOrDefault(v => v.Id == s.VersionId) is { } v)
                {
                    var (vt, _) = Fields(ctx, CardFields.Thread, new Dictionary<string, string>
                    {
                        ["name"] = v.Name, ["description"] = v.Description, ["type"] = v.Type.ToString(), ["status"] = v.Status.ToString(),
                        ["introducedChapterNumber"] = v.IntroducedChapterNumber?.ToString() ?? "",
                        ["resolvedChapterNumber"] = v.ResolvedChapterNumber?.ToString() ?? "",
                    }, canEdit: false);
                    return VersionView(ctx, heading, $"v{v.VersionNumber} · {v.CreatedAt:dd MMM HH:mm} · {v.CreatedBy}", vt);
                }
                return VersionList(ctx, heading, versions.Select(x => (x.Id, $"v{x.VersionNumber} · {x.CreatedAt:dd MMM HH:mm} · {x.CreatedBy}")).ToList());
            }
            default:
            {
                var versions = (await repo.GetChapterVersionsAsync(itemId)).OrderByDescending(v => v.VersionNumber).ToList();
                if (s.View == "version" && versions.FirstOrDefault(v => v.Id == s.VersionId) is { } v)
                {
                    var body = $"<p><b>Title:</b> {H(v.Title)}<br><b>Status:</b> {v.Status} · {N(CountWords(v.Content))} words</p>" +
                               (v.Content.Length > 0 ? $"<details><summary><b>Beginning</b></summary><p>{Lines(Clip(v.Content, 1500))}</p></details>" : "") +
                               (v.Outline.Length > 0 ? $"<details><summary><b>Outline</b></summary><p>{Lines(Clip(v.Outline, 3000))}</p></details>" : "");
                    return VersionView(ctx, heading, $"v{v.VersionNumber} · {v.CreatedAt:dd MMM HH:mm} · {v.CreatedBy}{(v.IsActive ? " · current" : "")}", body);
                }
                return VersionList(ctx, heading, versions.Select(x => (x.Id,
                    $"v{x.VersionNumber}{(x.IsActive ? " ●" : "")} · {x.CreatedAt:dd MMM HH:mm} · {x.CreatedBy} · {N(CountWords(x.Content))} w")).ToList());
            }
        }
    }

    // ── Clarifications ────────────────────────────────────────────────────────

    private async Task<(string, List<IReadOnlyList<TgButton>>)> RenderClarificationsAsync(Ctx ctx)
    {
        var all = (await repo.GetClarificationsAsync(ctx.Book!.Id)).OrderBy(c => c.IsOpen ? 0 : 1).ThenByDescending(c => c.Id).ToList();
        var pages = Math.Max(1, (all.Count + 5) / 6);
        var page = Math.Clamp(ctx.State.Page, 0, pages - 1);
        var sb = new StringBuilder($"<h3>❓ Clarifications</h3><p><i>{H(Clip(ctx.Book.Title, 80))} — the agents follow these answers and notes.</i></p>");
        if (all.Count == 0) sb.Append("<p><i>None yet.</i></p>");
        var i = page * 6;
        foreach (var c in all.Skip(page * 6).Take(6))
        {
            i++;
            sb.Append(c.IsOpen ? "<details open>" : "<details>").Append("<summary><b>").Append(i).Append(". ").Append(H(Clip(c.Question, 200))).Append("</b>")
              .Append(c.IsOpen ? " · <i>open</i>" : "").Append("</summary>");
            if (!c.IsOpen) sb.Append("<p>").Append(Lines(Clip(c.Answer, 2000))).Append("</p>");
            sb.Append("<p>").Append(L(ctx, c.IsOpen ? "answer" : "change the answer", "ans", c.Id)).Append(" · ").Append(L(ctx, "archive", "skip", c.Id)).Append("</p></details>");
        }
        List<IReadOnlyList<TgButton>> rows =
        [
            Pager(ctx, page, pages),
            new[] { B(ctx, "📝 Add a note", "note", style: "primary"), B(ctx, "⬅️ Book", "nav", "book") },
        ];
        return (sb.ToString(), rows.Where(r => r.Count > 0).ToList());
    }
}
