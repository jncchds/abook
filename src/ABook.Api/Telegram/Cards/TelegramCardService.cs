using System.Text;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram.Cards;

/// <summary>
/// Book management as cards: one message per card, edited in place as its state changes. Buttons choose; replying to
/// the card fills the field it asks for. Changes collect in a draft until 💾 Save (single answers save at once).
/// The owner, through their linked account, uses them anywhere (/books and /newbook in the private chat, /book in any
/// chat that follows the book); others only through /book in a chat that follows the book, with the access that chat
/// gives them (<see cref="CardAccess"/>, checked again on every press): Participate edits the content (Story Bible,
/// characters, plot threads, chapter outlines, clarifications) and downloads; Control also the book's settings,
/// archiving, restoring versions and runs. Only the owner changes the LLM preset and connects chats (one-time codes).
/// Only the one who opened a card can use it.
/// <para>
/// Button data: <c>k{cardId}:{op}:{arg}</c>. Cards: newbook (wizard), books (list), book (settings, sections,
/// downloads), bible, chars/char, threads/thread, chapters/chapter (outline, file, write, check, versions), clars.
/// </para>
/// </summary>
public sealed partial class TelegramCardService(
    ITelegramRepository tg,
    IBookRepository repo,
    IUserRepository users,
    BookContentService content,
    TelegramApi api,
    TelegramAccountService accounts,
    TelegramBindingService bindings,
    TelegramBotState bot,
    TelegramDispatcher dispatcher,
    AgentRunnerService runner,
    AgentRunStateService runState,
    BookChatService chat,
    ClarificationService clarifications,
    BookExportService exporter,
    IChapterIndexQueue indexQueue,
    ILogger<TelegramCardService> logger)
{
    private const int ListPage = 8;

    /// <summary>What <c>user</c> may do with the book through the chat <c>binding</c>: Control, Participate, or null (no card).</summary>
    public delegate Task<TelegramAccessLevel?> CardAccess(TelegramBinding binding, TgUser user);

    /// <summary>Everything a card is drawn from, loaded per update.</summary>
    private sealed class Ctx
    {
        public required TelegramCard Card { get; init; }
        public required CardState State { get; init; }
        /// <summary>The book's owner (the account whose presets and books the card uses).</summary>
        public required AppUser Owner { get; init; }
        public required TgUser From { get; init; }
        public Book? Book { get; set; }
        /// <summary>The card is used by the owner themself (not by someone the chat lets control the book).</summary>
        public bool IsOwner => Card.OpenedByTelegramUserId == Owner.TelegramUserId;
        public bool InPrivateChat => Card.ChatId > 0;
        /// <summary>What the user may do here: Control for the owner, else what the chat gives them.</summary>
        public TelegramAccessLevel Level { get; init; } = TelegramAccessLevel.Control;
        public bool CanControl => Level >= TelegramAccessLevel.Control;
    }

    private const string NeedsControl = "🔒 That needs Control access in this chat.";

    // ── Opening ───────────────────────────────────────────────────────────────

    /// <summary>Posts a new card in the private chat (and topic) and remembers it.</summary>
    public async Task OpenAsync(long chatId, int? threadId, TgUser from, AppUser owner, CardState state, int? bookId, int? replyTo = null)
    {
        // Saved first, so its buttons can carry its id; a placeholder message id (negative, so it never matches a
        // reply) until Telegram returns the real one.
        var card = await tg.AddCardAsync(new TelegramCard
        {
            ChatId = chatId, ThreadId = threadId, BookId = bookId, OpenedByTelegramUserId = from.Id,
            MessageId = -Random.Shared.Next(1, int.MaxValue), StateJson = state.Serialize(),
        });
        var ctx = new Ctx { Card = card, State = state, Owner = owner, From = from };
        if (bookId is int id) ctx.Book = await repo.GetByIdAsync(id);
        var (html, keyboard) = await RenderAsync(ctx);
        var sent = await TelegramRich.SendAsync(api, chatId, threadId, html, keyboard, replyTo);
        card.MessageId = sent.MessageId;
        card.StateJson = state.Serialize();
        await tg.UpdateCardAsync(card);
    }

    /// <summary>
    /// The card a reply or button belongs to, with the sender checked: only the one who opened it, and they must still
    /// own the book — or, for a card opened through a chat that follows the book, still have (at least Participate)
    /// access there.
    /// </summary>
    private async Task<Ctx?> LoadAsync(TelegramCard? card, TgUser from, CardAccess access)
    {
        if (card is null || card.OpenedByTelegramUserId != from.Id) return null;
        var state = CardState.Parse(card.StateJson);
        if (state.BindingId is int bindingId)
        {
            var binding = await tg.GetBindingAsync(bindingId);
            if (binding is not { IsActive: true } || binding.BookId != card.BookId) return null;
            if (await access(binding, from) is not { } level || level < TelegramAccessLevel.Participate) return null;
            var book = await repo.GetByIdAsync(binding.BookId);
            var bookOwner = book?.UserId is int ownerId ? await users.GetByIdAsync(ownerId) : null;
            if (book is null || bookOwner is null) return null;
            return new Ctx { Card = card, State = state, Owner = bookOwner, From = from, Book = book, Level = level };
        }
        var owner = await accounts.LinkedUserAsync(from.Id);
        if (owner is null) return null;
        var ctx = new Ctx { Card = card, State = state, Owner = owner, From = from };
        if (card.BookId is int id)
        {
            ctx.Book = await repo.GetByIdAsync(id);
            if (ctx.Book is null || ctx.Book.UserId != owner.Id) return null;
        }
        return ctx;
    }

    private async Task SaveAndRedrawAsync(Ctx ctx)
    {
        ctx.Card.StateJson = ctx.State.Serialize();
        await tg.UpdateCardAsync(ctx.Card);
        var (html, keyboard) = await RenderAsync(ctx);
        await TelegramRich.EditAsync(api, ctx.Card.ChatId, ctx.Card.MessageId, html, keyboard);
    }

    // ── Replies ───────────────────────────────────────────────────────────────

    /// <summary>A reply to one of the bot's messages: true when it was a card (handled), false otherwise.</summary>
    public async Task<bool> OnReplyAsync(TgMessage message, string text, CardAccess access)
    {
        if (message.ReplyToMessage is not { } replied || message.From is null) return false;
        var card = await tg.FindCardAsync(message.Chat.Id, replied.MessageId);
        if (card is null) return false;
        var ctx = await LoadAsync(card, message.From, access);
        if (ctx is null) return true;   // someone else's card: ignore

        var s = ctx.State;
        if (s.Awaiting is null)
        {
            s.Note = "Press ✏️ on a field first, then reply with its new text.";
            await SaveAndRedrawAsync(ctx);
            return true;
        }
        await ApplyReplyAsync(ctx, s.Awaiting, text);
        await ReactAsync(message, "👍");
        await SaveAndRedrawAsync(ctx);
        return true;
    }

    private async Task ApplyReplyAsync(Ctx ctx, string key, string text)
    {
        var s = ctx.State;
        s.Note = null;
        switch (s.Kind)
        {
            case "clars" when key == "note":
                if (await RefuseWhileBusyAsync(ctx)) return;
                await repo.AddClarificationAsync(new Clarification
                {
                    BookId = ctx.Book!.Id, Source = ClarificationSource.Author, Question = "Note from Telegram",
                    Answer = Clip(text, 4000), Origin = OriginOf(ctx),
                });
                s.Awaiting = null;
                s.Note = "📝 Note added.";
                return;
            case "clars" when key.StartsWith("answer.", StringComparison.Ordinal):
                if (await RefuseWhileBusyAsync(ctx)) return;
                var clarification = await repo.GetClarificationAsync(ctx.Book!.Id, int.Parse(key[7..]));
                if (clarification is null) { s.Note = "That clarification is gone."; s.Awaiting = null; return; }
                if (string.IsNullOrWhiteSpace(text)) { s.Note = "The answer can't be empty."; return; }
                await clarifications.UpdateAsync(clarification, null, text, null, OriginOf(ctx));
                s.Awaiting = null;
                s.Note = "✅ Answer saved.";
                return;
        }

        var field = CardFields.For(s.Kind).FirstOrDefault(f => f.Key == key);
        if (field is null) { s.Awaiting = null; return; }
        var (value, error) = CardFields.Validate(field, text);
        if (error is not null) { s.Note = "⚠️ " + error; return; }
        if (s.BaseHash is null) s.BaseHash = await CurrentHashAsync(ctx);
        s.Draft[key] = value!;
        s.Awaiting = s.Kind == "newbook" ? NextMissing(s) : null;
    }

    /// <summary>The new-book wizard asks the next field nobody filled yet.</summary>
    private static string? NextMissing(CardState s)
    {
        foreach (var f in CardFields.NewBook)
        {
            if (f.Key == "chapters" && s.Draft.GetValueOrDefault("mode") == nameof(BookMode.Interactive)) continue;
            if (!s.Draft.ContainsKey(f.Key)) return f.Key;
        }
        return null;
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    public static bool IsCardCallback(string? data) => data is { Length: > 1 } && data[0] == 'k' && char.IsDigit(data[1]);

    /// <summary>A card button. Returns the short answer for the button.</summary>
    public async Task<(string? Answer, bool Alert)> OnButtonAsync(TgCallbackQuery callback, CardAccess access)
    {
        var parts = callback.Data!.Split(':', 3);
        if (parts.Length < 2 || !int.TryParse(parts[0][1..], out var cardId)) return ("This button is no longer valid.", false);
        var ctx = await LoadAsync(await tg.GetCardAsync(cardId), callback.From, access);
        if (ctx is null) return ("This card isn't yours (send /book to open your own), or the book is gone.", true);
        var op = parts[1];
        var arg = parts.Length > 2 ? parts[2] : "";
        string? answer;
        try { answer = await ActAsync(ctx, op, arg); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Telegram: card {CardId} action {Op} failed.", cardId, op);
            ctx.State.Note = "⚠️ That didn't work: " + ex.Message;
            answer = null;
        }
        await SaveAndRedrawAsync(ctx);
        return (answer, false);
    }

    private async Task<string?> ActAsync(Ctx ctx, string op, string arg)
    {
        var s = ctx.State;
        s.Note = null;
        if (NeedsControlFor(s.Kind, op) && !ctx.CanControl) return NeedsControl;
        switch (op)
        {
            case "ed":   // ✏️ a field: the next reply fills it (choices also offer buttons)
                s.Awaiting = arg;
                s.View = "main";
                return "Reply to the card with the new value.";
            case "set":  // a choice button: "field=value"
            {
                var eq = arg.IndexOf('=');
                if (eq < 0) return null;
                await ApplyReplyAsync(ctx, arg[..eq], arg[(eq + 1)..]);
                return null;
            }
            case "x":    // stop waiting for a reply
                s.Awaiting = null;
                return null;
            case "undo":
                s.Draft.Clear();
                s.BaseHash = null;
                s.Awaiting = null;
                s.Note = "Changes discarded.";
                return null;
            case "save":
            case "save!":
                return await SaveAsync(ctx, force: op == "save!");
            case "nav":
            {
                if (s.Draft.Count > 0) { s.Note = "💾 Save or ↩️ discard your changes first."; return "Unsaved changes"; }
                if (arg is "books" or "newbook" && !ctx.IsOwner) return "Only the book's owner can do that.";
                var dot = arg.IndexOf('.');
                s.GoTo(dot < 0 ? arg : arg[..dot], dot < 0 ? null : int.Parse(arg[(dot + 1)..]));
                return null;
            }
            case "view":
                if (arg == "presets" && !ctx.IsOwner) return "Only the book's owner can change its LLM settings.";
                if (arg == "chats" && !ctx.IsOwner) return "Only the book's owner can connect chats.";
                s.View = arg;
                s.Page = 0;
                s.Awaiting = null;
                return null;
            case "pg":
                s.Page = Math.Max(0, int.Parse(arg));
                return null;
            case "full":
                s.View = "full";
                s.Focus = arg;
                s.Page = 0;
                return null;
            case "ver":
                s.View = "version";
                s.VersionId = int.Parse(arg);
                return null;
            case "restore":
                return await RestoreVersionAsync(ctx);
            case "add":
                if (s.Draft.Count > 0) { s.Note = "💾 Save or ↩️ discard your changes first."; return null; }
                s.GoTo(s.Kind == "chars" ? "char" : s.Kind == "threads" ? "thread" : "chapter");
                s.Awaiting = s.Kind == "chapter" ? "title" : "name";
                s.BaseHash = "new";
                return "Reply to the card with the name.";
            case "addin":   // ➕ a chapter at the end of the section shown
            {
                if (s.Kind != "section" || s.ItemId is not int sectionId) return null;
                if (s.Draft.Count > 0) { s.Note = "💾 Save or ↩️ discard your changes first."; return null; }
                s.GoTo("chapter");
                s.SectionId = sectionId;
                s.Awaiting = "title";
                s.BaseHash = "new";
                return "Reply to the card with the title.";
            }
            case "arch":
            case "unarch":
                return await ArchiveAsync(ctx, archive: op == "arch");
            case "mv":   // ⬆️ / ⬇️ a chapter or a section one place
                return s.Kind == "section" ? await MoveSectionAsync(ctx, up: arg == "up") : await MoveChapterAsync(ctx, up: arg == "up");
            case "brk":  // ⋔ break the section down into chapters (a run); arg = how many, empty = the planner decides
                return await StartBreakdownAsync(ctx, int.TryParse(arg, out var count) ? count : null);
            case "create":
                return await CreateBookAsync(ctx);
            case "open":
                return await OpenBookAsync(ctx, int.Parse(arg));
            case "write":
                return await StartRunAsync(ctx, "write");
            case "check":
                return await StartRunAsync(ctx, "check");
            case "wsec":   // ✍️ the section as Write Book writes it (Autonomous books)
            case "csec":   // 🔍 each written chapter of the section
                return StartSectionRun(ctx, write: op == "wsec");
            case "lock":   // 🔒 Interactive books: premise and Story Bible become canon, writing starts
            {
                var outcome = await chat.LockBibleAsync(ctx.Book!.Id);
                s.Note = outcome.Result is BookChatResult.Done or BookChatResult.Started
                    ? "🔒 Premise and Story Bible locked — the book chat now writes chapters. Only the owner can unlock them, in Book Settings."
                    : "⚠️ " + outcome.Message;
                return null;
            }
            case "file":
                return await SendChapterFileAsync(ctx);
            case "exp":   // ⬇️ the book (epub/fb2/html) or its metadata, as a file under the card
                return await SendBookFileAsync(ctx, arg);
            case "kick":
            {
                var outcome = await chat.KickoffAsync(ctx.Book!.Id);
                s.Note = outcome.Result == BookChatResult.Started ? "💬 The book chat is starting — its first reply comes in the book's topic." : "⚠️ " + outcome.Message;
                return null;
            }
            case "preset":
                if (!ctx.IsOwner) return "Only the book's owner can change its LLM settings.";
                return await ApplyPresetAsync(ctx, int.Parse(arg));
            case "ans":
                s.Awaiting = $"answer.{arg}";
                return "Reply to the card with the answer.";
            case "note":
                s.Awaiting = "note";
                return "Reply to the card with the note.";
            case "skip":
            {
                var c = await repo.GetClarificationAsync(ctx.Book!.Id, int.Parse(arg));
                if (c is not null) await clarifications.UpdateAsync(c, null, null, archived: true, OriginOf(ctx));
                s.Note = "Archived.";
                return null;
            }
            case "code":   // 🔗 a one-time code that connects a chat at this level
                return await SendConnectCodeAsync(ctx, arg);
            case "unbind":
                return await DisconnectAsync(ctx, int.Parse(arg));
        }
        return null;
    }

    /// <summary>
    /// What only Control may do from a card: the book's own settings, archiving and bringing back, restoring a version,
    /// starting a chapter run. Everything else a card does (content fields, adding items, clarifications, downloads)
    /// is Participate — like writing in the book chat and applying what it suggests.
    /// </summary>
    public static bool NeedsControlFor(string kind, string op) =>
        op is "restore" or "arch" or "unarch" or "write" or "check" or "mv" or "brk" or "wsec" or "csec" or "lock"
        || (kind == "book" && op is "ed" or "set" or "save" or "save!" or "undo");

    // ── Saving ────────────────────────────────────────────────────────────────

    private async Task<string?> SaveAsync(Ctx ctx, bool force)
    {
        var s = ctx.State;
        if (s.Draft.Count == 0) return "Nothing to save.";
        if (s.Kind != "newbook" && await RefuseWhileBusyAsync(ctx)) return null;
        if (!force && s.BaseHash is { } baseHash && baseHash != "new" && baseHash != await CurrentHashAsync(ctx))
        {
            s.Note = "⚠️ It changed since you started editing. 💾 Save anyway to overwrite, or ↩️ discard.";
            return null;
        }
        try
        {
            var note = s.Kind switch
            {
                "book" => await SaveBookAsync(ctx),
                "bible" => (await content.UpdateStoryBibleAsync(ctx.Book!.Id, s.Draft, source: EditSources.TelegramEdit)).Note,
                "char" => await SaveCharacterAsync(ctx),
                "thread" => await SaveThreadAsync(ctx),
                "chapter" => await SaveChapterAsync(ctx),
                "section" when s.ItemId is int sectionId =>
                    (await content.UpdateSectionPlanAsync(ctx.Book!.Id, sectionId, s.Draft, AgentCreatedBy.Telegram)).Note,
                _ => null,
            };
            s.Draft.Clear();
            s.BaseHash = null;
            s.Awaiting = null;
            s.Note = "✅ " + (note ?? "Saved.");
            return "Saved.";
        }
        catch (StoryBibleLockedException)
        {
            s.Note = "🔒 The premise and Story Bible are locked since writing started. Unlock them in abook's Book Settings.";
        }
        catch (InvalidOperationException ex)
        {
            s.Note = "⚠️ " + ex.Message;
        }
        return null;
    }

    private async Task<string> SaveBookAsync(Ctx ctx)
    {
        var book = (await repo.GetByIdAsync(ctx.Book!.Id))!;
        var d = ctx.State.Draft;
        var mode = d.TryGetValue("mode", out var m) && Enum.TryParse<BookMode>(m, out var parsed) ? parsed : book.Mode;
        var target = d.TryGetValue("chapters", out var c) ? (int.TryParse(c, out var n) ? n : 0) : book.TargetChapterCount;
        if (mode == BookMode.Autonomous && target < 1)
            throw new InvalidOperationException("An Autonomous book needs a target chapter count.");
        if (d.ContainsKey("premise") && d["premise"].Trim() != book.Premise.Trim() && BookContentService.IsBibleLocked(book))
            throw new StoryBibleLockedException();

        var before = BookContentService.SnapshotOf(book, EditSources.TelegramEdit);
        if (d.TryGetValue("title", out var title)) book.Title = title;
        if (d.TryGetValue("premise", out var premise)) book.Premise = premise;
        if (d.TryGetValue("genre", out var genre)) book.Genre = genre;
        if (d.TryGetValue("language", out var language)) book.Language = language;
        if (d.TryGetValue("illustrations", out var ill)) book.IllustrationsEnabled = ill == "on";
        book.Mode = mode;
        book.TargetChapterCount = Math.Max(0, target);
        await repo.UpdateAsync(book);
        await content.RecordBookEditAsync(before, book, EditSources.TelegramEdit);
        ctx.Book = book;
        return "Book settings saved.";
    }

    private async Task<string> SaveCharacterAsync(Ctx ctx)
    {
        var d = new Dictionary<string, string>(ctx.State.Draft);
        string name;
        if (ctx.State.ItemId is int id)
        {
            var card = (await repo.GetAllCharacterCardsAsync(ctx.Book!.Id)).FirstOrDefault(c => c.Id == id)
                ?? throw new InvalidOperationException("That character is gone.");
            name = card.Name;
            if (d.Remove("name", out var newName) && newName != card.Name)
            {
                if ((await repo.GetCharacterCardsAsync(ctx.Book.Id)).Any(c => c.Id != id && c.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"There is already a character called {newName}.");
                card.Name = name = newName;
                await repo.UpdateCharacterCardAsync(card);
            }
        }
        else if (!d.Remove("name", out name!)) throw new InvalidOperationException("The character needs a name.");
        var change = await content.UpsertCharacterAsync(ctx.Book!.Id, name, d, AgentCreatedBy.Telegram);
        ctx.State.ItemId ??= (await content.FindCharacterAsync(ctx.Book.Id, name))?.Id;
        return change.Note;
    }

    private async Task<string> SaveThreadAsync(Ctx ctx)
    {
        var d = new Dictionary<string, string>(ctx.State.Draft);
        string name;
        if (ctx.State.ItemId is int id)
        {
            var thread = (await repo.GetAllPlotThreadsAsync(ctx.Book!.Id)).FirstOrDefault(t => t.Id == id)
                ?? throw new InvalidOperationException("That plot thread is gone.");
            name = thread.Name;
            if (d.Remove("name", out var newName) && newName != thread.Name)
            {
                if ((await repo.GetPlotThreadsAsync(ctx.Book.Id)).Any(t => t.Id != id && t.Name.Equals(newName, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException($"There is already a plot thread called {newName}.");
                thread.Name = name = newName;
                await repo.UpdatePlotThreadAsync(thread);
            }
        }
        else if (!d.Remove("name", out name!)) throw new InvalidOperationException("The plot thread needs a name.");
        var change = await content.UpsertPlotThreadAsync(ctx.Book!.Id, name, d, AgentCreatedBy.Telegram);
        ctx.State.ItemId ??= (await content.FindThreadAsync(ctx.Book.Id, name))?.Id;
        return change.Note;
    }

    private async Task<string> SaveChapterAsync(Ctx ctx)
    {
        if (ctx.State.ItemId is int id)
            return (await content.UpdateChapterOutlineAsync(ctx.Book!.Id, id, ctx.State.Draft, AgentCreatedBy.Telegram)).Note;
        // A new chapter goes at the end of the section it was added from, or at the end of the book as a section of its own.
        var change = await content.AddChapterAsync(ctx.Book!.Id, ctx.State.Draft, ctx.State.SectionId);
        ctx.State.ItemId = change.ChapterId;
        ctx.State.SectionId = null;
        return change.Note;
    }

    /// <summary>Editing is refused while an agent works on the book (except while it waits for an answer).</summary>
    private Task<bool> RefuseWhileBusyAsync(Ctx ctx)
    {
        if (ctx.Book is null || runState.GetStatus(ctx.Book.Id) is not { IsActive: true, State: not RunStates.WaitingForInput })
            return Task.FromResult(false);
        ctx.State.Note = "⏳ An agent is working on the book — wait for it to finish (or stop it), then save.";
        return Task.FromResult(true);
    }

    // ── Archive, versions, presets ────────────────────────────────────────────

    /// <summary>
    /// Moves the card's chapter one place among the active chapters (a chapter alone in its section moves with it;
    /// otherwise it joins the neighbouring chapter's section).
    /// </summary>
    private async Task<string?> MoveChapterAsync(Ctx ctx, bool up)
    {
        var s = ctx.State;
        if (s.Kind != "chapter" || s.ItemId is not int id || await RefuseWhileBusyAsync(ctx)) return null;
        var bookId = ctx.Book!.Id;
        var chapters = (await repo.GetChaptersAsync(bookId)).ToList();
        var i = chapters.FindIndex(c => c.Id == id);
        if (i < 0 || (up && i == 0) || (!up && i == chapters.Count - 1)) return null;
        int? after = up ? (i >= 2 ? chapters[i - 2].Id : null) : chapters[i + 1].Id;
        try { s.Note = "✅ " + (await content.MoveChapterAsync(bookId, id, after)).Note; }
        catch (InvalidOperationException ex) { s.Note = "⚠️ " + ex.Message; }
        return null;
    }

    /// <summary>Moves the card's section, with its chapters, one place among the active sections.</summary>
    private async Task<string?> MoveSectionAsync(Ctx ctx, bool up)
    {
        var s = ctx.State;
        if (s.ItemId is not int id || await RefuseWhileBusyAsync(ctx)) return null;
        var bookId = ctx.Book!.Id;
        var sections = (await repo.GetSectionsAsync(bookId)).ToList();
        var i = sections.FindIndex(x => x.Id == id);
        if (i < 0 || (up && i == 0) || (!up && i == sections.Count - 1)) return null;
        int? after = up ? (i >= 2 ? sections[i - 2].Id : null) : sections[i + 1].Id;
        try { s.Note = "✅ " + (await content.MoveSectionAsync(bookId, id, after)).Note; }
        catch (InvalidOperationException ex) { s.Note = "⚠️ " + ex.Message; }
        return null;
    }

    /// <summary>Starts the run that divides the card's section into chapters (none of them written yet).</summary>
    private async Task<string?> StartBreakdownAsync(Ctx ctx, int? count)
    {
        if (ctx.State.Kind != "section" || ctx.State.ItemId is not int sectionId) return null;
        var bookId = ctx.Book!.Id;
        if ((await repo.GetChaptersAsync(bookId)).Any(c => c.SectionId == sectionId && !string.IsNullOrWhiteSpace(c.Content)))
        {
            ctx.State.Note = "⚠️ This section already has written chapters — split a chapter in abook instead.";
            return null;
        }
        var result = runner.TryStart(bookId, AgentRole.ChaptersAgent, null,
            (o, c) => o.StartSectionBreakdownAsync(bookId, sectionId, count, c));
        ctx.State.Note = result == RunStartResult.Success
            ? "⋔ Breaking the section down into chapters — they appear here when it is done."
            : "⏳ " + result.ToMessage();
        return null;
    }

    private async Task<string?> ArchiveAsync(Ctx ctx, bool archive)
    {
        var s = ctx.State;
        if (s.ItemId is not int id || await RefuseWhileBusyAsync(ctx)) return null;
        var bookId = ctx.Book!.Id;
        try
        {
            switch (s.Kind)
            {
                case "char":
                {
                    var card = (await repo.GetAllCharacterCardsAsync(bookId)).First(c => c.Id == id);
                    s.Note = "✅ " + (archive ? (await content.ArchiveCharacterAsync(bookId, card.Name)).Note
                                              : (await content.UpsertCharacterAsync(bookId, card.Name, new Dictionary<string, string>(), AgentCreatedBy.Telegram)).Note);
                    break;
                }
                case "thread":
                {
                    var thread = (await repo.GetAllPlotThreadsAsync(bookId)).First(t => t.Id == id);
                    s.Note = "✅ " + (archive ? (await content.ArchivePlotThreadAsync(bookId, thread.Name)).Note
                                              : (await content.UpsertPlotThreadAsync(bookId, thread.Name, new Dictionary<string, string>(), AgentCreatedBy.Telegram)).Note);
                    break;
                }
                case "chapter":
                    s.Note = "✅ " + (archive ? (await content.ArchiveLastChapterAsync(bookId, id)).Note
                                              : (await content.RestoreLastChapterAsync(bookId, id)).Note);
                    break;
                case "section":
                    s.Note = "✅ " + (archive ? (await content.ArchiveSectionAsync(bookId, id)).Note
                                              : (await content.RestoreSectionAsync(bookId, id)).Note);
                    break;
            }
        }
        catch (InvalidOperationException ex) { s.Note = "⚠️ " + ex.Message; }
        return null;
    }

    private async Task<string?> RestoreVersionAsync(Ctx ctx)
    {
        var s = ctx.State;
        if (s.VersionId is not int versionId || await RefuseWhileBusyAsync(ctx)) return null;
        var bookId = ctx.Book!.Id;
        try
        {
            switch (s.Kind)
            {
                case "bible":
                    if (BookContentService.IsBibleLocked(ctx.Book)) throw new StoryBibleLockedException();
                    await repo.RestoreStoryBibleSnapshotAsync(bookId, versionId);
                    break;
                case "char":
                    await repo.RestoreCharacterVersionAsync(bookId, s.ItemId!.Value, versionId);
                    break;
                case "thread":
                    await repo.RestorePlotThreadVersionAsync(bookId, s.ItemId!.Value, versionId);
                    break;
                case "chapter":
                {
                    var version = await repo.ActivateChapterVersionAsync(bookId, s.ItemId!.Value, versionId);
                    if (!version.HasEmbeddings && !string.IsNullOrEmpty(version.Content))
                        indexQueue.QueueIndex(bookId, s.ItemId.Value, version.Id);
                    dispatcher.Sink.Post(new TgChapterEvent(bookId, s.ItemId.Value));
                    break;
                }
            }
            s.View = "main";
            s.VersionId = null;
            s.Note = "♻️ Restored — the restored state is saved as the newest version.";
        }
        catch (StoryBibleLockedException) { s.Note = "🔒 The Story Bible is locked since writing started."; }
        catch (InvalidOperationException ex) { s.Note = "⚠️ " + ex.Message; }
        return null;
    }

    private async Task<string?> ApplyPresetAsync(Ctx ctx, int presetId)
    {
        var preset = await repo.GetPresetAsync(presetId);
        if (preset is null || (preset.UserId is not null && preset.UserId != ctx.Owner.Id)) return "That preset is gone.";
        var config = new LlmConfiguration { BookId = ctx.Book!.Id };
        config.CopyFrom(preset);
        await repo.UpsertLlmConfigAsync(config);
        ctx.State.View = "main";
        ctx.State.Note = $"🧠 LLM settings copied from “{preset.Name}”.";
        return null;
    }

    // ── New book, opening books ───────────────────────────────────────────────

    private async Task<string?> CreateBookAsync(Ctx ctx)
    {
        var s = ctx.State;
        var d = s.Draft;
        var mode = d.TryGetValue("mode", out var m) && Enum.TryParse<BookMode>(m, out var parsed) ? parsed : BookMode.Autonomous;
        var chapters = d.TryGetValue("chapters", out var c) && int.TryParse(c, out var n) ? n : 0;
        if (string.IsNullOrWhiteSpace(d.GetValueOrDefault("title")) || string.IsNullOrWhiteSpace(d.GetValueOrDefault("premise")))
        { s.Note = "⚠️ A book needs a title and a premise."; return null; }
        if (mode == BookMode.Autonomous && chapters < 1)
        { s.Note = "⚠️ An Autonomous book needs a number of chapters."; s.Awaiting = "chapters"; return null; }
        if (await repo.GetDefaultPresetAsync(ctx.Owner.Id) is null)
        { s.Note = "⚠️ You have no default LLM preset, so the book would have no model to write with. Choose one in abook: Settings → Default LLM Preset."; return null; }

        var book = await repo.AddAsync(new Book
        {
            Title = d["title"].Trim(), Premise = d["premise"].Trim(), Genre = d.GetValueOrDefault("genre", ""),
            Language = d.GetValueOrDefault("language", "English"), Mode = mode,
            TargetChapterCount = mode == BookMode.Autonomous ? chapters : 0, UserId = ctx.Owner.Id,
        });
        await repo.AddMessageAsync(new AgentMessage
        {
            BookId = book.Id, AgentRole = AgentRole.Planner, MessageType = MessageType.SystemNote,
            Content = "📘 Book created from Telegram.", IsResolved = true, Origin = OriginOf(ctx),
        });

        // The card becomes the new book's card; following it in a chat is a separate, explicit step (🔗 Connect a chat).
        ShowBook(ctx, book);
        s.Note = $"✅ “{book.Title}” is created. To get its updates in a chat, use 🔗 Connect a chat.";
        return "Created.";
    }

    /// <summary>A book of the list, opened in this same card — nothing is followed or subscribed.</summary>
    private async Task<string?> OpenBookAsync(Ctx ctx, int bookId)
    {
        if (!ctx.IsOwner) return "Only the book's owner can do that.";
        var book = await repo.GetByIdAsync(bookId);
        if (book is null || book.UserId != ctx.Owner.Id) return "That book isn't yours.";
        ShowBook(ctx, book);
        return null;
    }

    private static void ShowBook(Ctx ctx, Book book)
    {
        ctx.Card.BookId = book.Id;
        ctx.Book = book;
        ctx.State.GoTo("book");
    }

    // ── Runs and files ────────────────────────────────────────────────────────

    private async Task<string?> StartRunAsync(Ctx ctx, string what)
    {
        if (ctx.State.ItemId is not int chapterId) return null;
        var bookId = ctx.Book!.Id;
        var result = what == "write"
            ? runner.TryStart(bookId, AgentRole.Writer, chapterId, (o, c) => o.StartWritingAsync(bookId, chapterId, c))
            : runner.TryStart(bookId, AgentRole.ContinuityChecker, chapterId, (o, c) => o.StartContinuityCheckAsync(bookId, chapterId, c));
        ctx.State.Note = result == RunStartResult.Success
            ? (what == "write" ? "✍️ Writing the chapter — follow it on the status card." : "🔍 Checking the chapter — findings arrive in the book's messages.")
            : "⏳ " + result.ToMessage();
        return null;
    }

    /// <summary>
    /// The section card's runs: write it as Write Book does (broken down first when planned; Autonomous books only), or
    /// check each written chapter of it.
    /// </summary>
    private string? StartSectionRun(Ctx ctx, bool write)
    {
        if (ctx.State.Kind != "section" || ctx.State.ItemId is not int sectionId) return null;
        var book = ctx.Book!;
        if (write && book.Mode == BookMode.Interactive)
        {
            ctx.State.Note = "💬 This is an Interactive book: its chapters are written in the book chat.";
            return null;
        }
        var bookId = book.Id;
        var result = write
            ? runner.TryStart(bookId, AgentRole.Writer, null, (o, c) => o.StartSectionWritingAsync(bookId, sectionId, c))
            : runner.TryStart(bookId, AgentRole.ContinuityChecker, null, (o, c) => o.StartSectionCheckAsync(bookId, sectionId, c));
        ctx.State.Note = result == RunStartResult.Success
            ? (write ? "✍️ Writing the section — follow it on the status card." : "🔍 Checking the section's chapters — findings arrive in the book's messages.")
            : "⏳ " + result.ToMessage();
        return null;
    }

    private async Task<string?> SendChapterFileAsync(Ctx ctx)
    {
        if (ctx.State.ItemId is not int chapterId) return null;
        var chapter = await repo.GetChapterAsync(ctx.Book!.Id, chapterId);
        if (chapter is null || string.IsNullOrWhiteSpace(chapter.Content)) { ctx.State.Note = "This chapter has no text yet."; return null; }
        var binding = (await tg.GetActiveBindingsInChatAsync(ctx.Card.ChatId)).FirstOrDefault(b => b.BookId == ctx.Book.Id);
        var (bytes, contentType, fileName) = await exporter.ExportChapterAsync(ctx.Book.Id, chapterId, binding?.FileFormat ?? "epub");
        await api.SendDocumentAsync(ctx.Card.ChatId, ctx.Card.ThreadId, fileName, bytes, contentType,
            $"📄 <b>Chapter {chapter.Number}: {H(Clip(chapter.Title, 150))}</b>\n{N(CountWords(chapter.Content))} words", ctx.Card.MessageId);
        return "Sent.";
    }

    /// <summary>
    /// Connecting a chat from the card: the same one-time code as Book Settings → Telegram (15 minutes, used once, only
    /// its hash stored), sent as its own message under the card with the ways to use it. The chat it is used in follows
    /// the book at <paramref name="levelName"/>. Owner only.
    /// </summary>
    private async Task<string?> SendConnectCodeAsync(Ctx ctx, string levelName)
    {
        if (!ctx.IsOwner) return "Only the book's owner can connect chats.";
        if (!Enum.TryParse<TelegramAccessLevel>(levelName, out var level) || !Enum.IsDefined(level)) return null;
        var code = await bindings.CreateCodeAsync(ctx.Book!.Id, level, ctx.Owner.Id);
        var sb = new StringBuilder("<h4>🔗 Connect a chat to “").Append(H(Clip(ctx.Book.Title, 80))).Append("” · ").Append(level).Append("</h4>")
            .Append("<p>Works once, for 15 minutes. Everyone who can post in that chat gets ").Append(H(LevelText(level))).Append(".</p><ul>");
        if (bot.PrivateLink(code.Code) is { } privateLink)
            sb.Append("<li><a href=\"").Append(H(privateLink)).Append("\">This private chat</a> — a private chat follows one book</li>");
        if (bot.GroupLink(code.Code) is { } groupLink)
            sb.Append("<li><a href=\"").Append(H(groupLink)).Append("\">Add the bot to a group</a> — it follows the book there</li>");
        sb.Append("<li>A forum topic or a channel: send <code>/bind ").Append(H(code.Code)).Append("</code> there</li></ul>");
        await TelegramRich.SendAsync(api, ctx.Card.ChatId, ctx.Card.ThreadId, sb.ToString(), replyTo: ctx.Card.MessageId);
        ctx.State.Note = "🔗 The code is in the message below.";
        return "Code sent.";
    }

    /// <summary>Disconnects one of the chats that follow the book (owner only), like /unbind there.</summary>
    private async Task<string?> DisconnectAsync(Ctx ctx, int bindingId)
    {
        if (!ctx.IsOwner) return "Only the book's owner can disconnect chats.";
        var binding = await tg.GetBindingAsync(bindingId);
        if (binding is not { IsActive: true } || binding.BookId != ctx.Book!.Id) return "That chat isn't connected any more.";
        await bindings.RevokeAsync(binding, $"disconnected by {ctx.From.Display} from the book card", tellChat: true, OriginOf(ctx));
        ctx.State.Note = $"🔌 Disconnected: {binding.ChatTitle}.";
        return null;
    }

    /// <summary>The whole book (epub, fb2, html) or its metadata document, sent as a file under the card.</summary>
    private async Task<string?> SendBookFileAsync(Ctx ctx, string format)
    {
        if (format is not ("epub" or "fb2" or "html" or "metadata")) return null;
        var book = ctx.Book!;
        if (format != "metadata" && !(await repo.GetChaptersAsync(book.Id)).Any(c => !string.IsNullOrWhiteSpace(c.Content)))
        {
            ctx.State.Note = "No chapter has text yet — the 🗂 metadata download already shows the plan.";
            return null;
        }
        var (bytes, contentType, fileName) = await exporter.ExportAsync(book.Id, format);
        var caption = format == "metadata"
            ? $"🗂 <b>{H(Clip(book.Title, 150))}</b> — metadata: each section as it is now, then the full timeline"
            : $"📕 <b>{H(Clip(book.Title, 150))}</b> — the book as it stands";
        await api.SendDocumentAsync(ctx.Card.ChatId, ctx.Card.ThreadId, fileName, bytes, contentType, caption, ctx.Card.MessageId);
        return "Sent.";
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string OriginOf(Ctx ctx) => $"Telegram · {ctx.From.Display} · {(ctx.InPrivateChat ? "private chat" : "group card")}";

    private async Task ReactAsync(TgMessage message, string emoji)
    {
        try { await api.SetReactionAsync(message.Chat.Id, message.MessageId, emoji); }
        catch (TelegramApiException) { }
    }

    /// <summary>The item's fields as they are now — compared with the draft's start to spot changes made meanwhile.</summary>
    private async Task<string?> CurrentHashAsync(Ctx ctx) => (await CurrentFieldsAsync(ctx)) is { } f ? Hash(BookContentRender.Json(f)) : null;

    private async Task<Dictionary<string, string>?> CurrentFieldsAsync(Ctx ctx)
    {
        var s = ctx.State;
        var bookId = ctx.Book?.Id ?? 0;
        switch (s.Kind)
        {
            case "book":
                return ctx.Book is null ? null : CardFields.Of((await repo.GetByIdAsync(bookId))!);
            case "bible":
                return await repo.GetStoryBibleAsync(bookId) is { } bible ? BookContentRender.Fields(bible) : new Dictionary<string, string>();
            case "char" when s.ItemId is int id:
                return (await repo.GetAllCharacterCardsAsync(bookId)).FirstOrDefault(c => c.Id == id) is { } card ? BookContentRender.Fields(card) : null;
            case "thread" when s.ItemId is int id:
                return (await repo.GetAllPlotThreadsAsync(bookId)).FirstOrDefault(t => t.Id == id) is { } thread ? BookContentRender.Fields(thread) : null;
            case "chapter" when s.ItemId is int id:
                return await repo.GetChapterAsync(bookId, id) is { } chapter ? BookContentRender.Fields(chapter) : null;
            case "section" when s.ItemId is int id:
                return await repo.GetSectionAsync(bookId, id) is { } section ? BookContentRender.Fields(section) : null;
            default:
                return null;
        }
    }
}
