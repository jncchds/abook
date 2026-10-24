using System.Text;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram.Cards;

/// <summary>
/// Book management in the owner's private chat with the bot, as cards: one message per card, edited in place as its
/// state changes. Buttons choose; replying to the card fills the field it asks for. Changes collect in a draft until
/// 💾 Save (single answers save at once). Only for the owner, through their linked account.
/// <para>
/// Button data: <c>k{cardId}:{op}:{arg}</c>. Cards: newbook (wizard), books (list), book (settings, sections), bible,
/// chars/char, threads/thread, chapters/chapter (outline, file, write, check, versions), clars (clarifications).
/// </para>
/// </summary>
public sealed partial class TelegramCardService(
    ITelegramRepository tg,
    IBookRepository repo,
    BookContentService content,
    TelegramApi api,
    TelegramAccountService accounts,
    TelegramBindingService bindings,
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

    /// <summary>Everything a card is drawn from, loaded per update.</summary>
    private sealed class Ctx
    {
        public required TelegramCard Card { get; init; }
        public required CardState State { get; init; }
        public required AppUser Owner { get; init; }
        public required TgUser From { get; init; }
        public Book? Book { get; set; }
    }

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

    /// <summary>The card a reply or button belongs to, with the sender checked: only its owner may use it.</summary>
    private async Task<Ctx?> LoadAsync(TelegramCard? card, TgUser from)
    {
        if (card is null || card.OpenedByTelegramUserId != from.Id) return null;
        var owner = await accounts.LinkedUserAsync(from.Id);
        if (owner is null) return null;
        var ctx = new Ctx { Card = card, State = CardState.Parse(card.StateJson), Owner = owner, From = from };
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
    public async Task<bool> OnReplyAsync(TgMessage message, string text)
    {
        if (message.ReplyToMessage is not { } replied || message.From is null) return false;
        var card = await tg.FindCardAsync(message.Chat.Id, replied.MessageId);
        if (card is null) return false;
        var ctx = await LoadAsync(card, message.From);
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
    public async Task<(string? Answer, bool Alert)> OnButtonAsync(TgCallbackQuery callback)
    {
        var parts = callback.Data!.Split(':', 3);
        if (parts.Length < 2 || !int.TryParse(parts[0][1..], out var cardId)) return ("This button is no longer valid.", false);
        var ctx = await LoadAsync(await tg.GetCardAsync(cardId), callback.From);
        if (ctx is null) return ("This card isn't yours, or the book is gone.", true);
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
                var dot = arg.IndexOf('.');
                s.GoTo(dot < 0 ? arg : arg[..dot], dot < 0 ? null : int.Parse(arg[(dot + 1)..]));
                return null;
            }
            case "view":
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
            case "arch":
            case "unarch":
                return await ArchiveAsync(ctx, archive: op == "arch");
            case "create":
                return await CreateBookAsync(ctx);
            case "open":
                return await OpenBookAsync(ctx, int.Parse(arg));
            case "write":
                return await StartRunAsync(ctx, "write");
            case "check":
                return await StartRunAsync(ctx, "check");
            case "file":
                return await SendChapterFileAsync(ctx);
            case "kick":
            {
                var outcome = await chat.KickoffAsync(ctx.Book!.Id);
                s.Note = outcome.Result == BookChatResult.Started ? "💬 The book chat is starting — its first reply comes in the book's topic." : "⚠️ " + outcome.Message;
                return null;
            }
            case "preset":
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
        }
        return null;
    }

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
                "bible" => (await content.UpdateStoryBibleAsync(ctx.Book!.Id, s.Draft)).Note,
                "char" => await SaveCharacterAsync(ctx),
                "thread" => await SaveThreadAsync(ctx),
                "chapter" => await SaveChapterAsync(ctx),
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

        await repo.AddBookSnapshotAsync(new BookSnapshot
        {
            BookId = book.Id, Title = book.Title, Premise = book.Premise, Genre = book.Genre,
            TargetChapterCount = book.TargetChapterCount, Language = book.Language, Mode = book.Mode, Reason = "telegram",
        });
        if (d.TryGetValue("title", out var title)) book.Title = title;
        if (d.TryGetValue("premise", out var premise)) book.Premise = premise;
        if (d.TryGetValue("genre", out var genre)) book.Genre = genre;
        if (d.TryGetValue("language", out var language)) book.Language = language;
        if (d.TryGetValue("illustrations", out var ill)) book.IllustrationsEnabled = ill == "on";
        book.Mode = mode;
        book.TargetChapterCount = Math.Max(0, target);
        await repo.UpdateAsync(book);
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
            return (await content.UpdateChapterOutlineAsync(ctx.Book!.Id, id, ctx.State.Draft)).Note;
        // A new chapter always goes at the end.
        var change = await content.AddChapterAsync(ctx.Book!.Id, ctx.State.Draft);
        ctx.State.ItemId = change.ChapterId;
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

        var chatInfo = new TgChat { Id = ctx.Card.ChatId, Type = "private" };
        var (binding, error) = await bindings.OpenForOwnerAsync(book, chatInfo, ctx.From, ctx.Owner.Id);
        d.Clear();
        s.Awaiting = null;
        s.Kind = "created";
        s.ItemId = book.Id;
        s.Note = binding is not null
            ? $"✅ “{book.Title}” is created{(binding.ThreadId is not null ? " — it has its own topic now" : "")}."
            : $"✅ “{book.Title}” is created, but couldn't open here: {error}";
        if (binding is not null)
            await OpenAsync(binding.ChatId, binding.ThreadId, ctx.From, ctx.Owner, new CardState { Kind = "book" }, book.Id);
        return "Created.";
    }

    private async Task<string?> OpenBookAsync(Ctx ctx, int bookId)
    {
        var book = await repo.GetByIdAsync(bookId);
        if (book is null || book.UserId != ctx.Owner.Id) return "That book isn't yours.";
        var (binding, error) = await bindings.OpenForOwnerAsync(book, new TgChat { Id = ctx.Card.ChatId, Type = "private" }, ctx.From, ctx.Owner.Id);
        if (binding is null) { ctx.State.Note = "⚠️ " + error; return null; }
        await OpenAsync(binding.ChatId, binding.ThreadId, ctx.From, ctx.Owner, new CardState { Kind = "book" }, book.Id);
        ctx.State.Note = $"📖 “{book.Title}” is open{(binding.ThreadId is not null ? " in its topic" : " here")}.";
        return null;
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

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static string OriginOf(Ctx ctx) => $"Telegram · {ctx.From.Display} · private chat";

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
            default:
                return null;
        }
    }
}
