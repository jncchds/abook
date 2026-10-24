using System.Collections.Concurrent;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram;

/// <summary>
/// Handles what arrives from Telegram: commands, replies to the bot's questions, book-chat messages, button presses,
/// stops from a live draft, and the bot being removed from a chat. Access is the binding of the chat (and topic) the
/// update came from — whoever can post there has the binding's level; nobody signs in.
/// <para>
/// Plain messages are never taken as answers to whatever is pending: in an Autonomous book the bot acts on commands,
/// buttons and explicit replies to one of its question messages; in an Interactive book a plain message is a
/// book-chat message, like the chat box in the web UI.
/// </para>
/// </summary>
public sealed class TelegramUpdateHandler(
    TelegramApi api,
    TelegramBotState bot,
    TelegramDispatcher dispatcher,
    AgentRunnerService runner,
    AgentRunStateService runState,
    BookChatService chat,
    RewindService rewinds,
    IServiceScopeFactory scopes,
    ILogger<TelegramUpdateHandler> logger)
{
    /// <summary>A rewind preview waiting for its ⏪ button (kept in memory: a restart just makes it expire).</summary>
    private sealed record RewindOffer(int BindingId, DateTime At, string Fingerprint, DateTime Expires);
    private readonly ConcurrentDictionary<int, RewindOffer> _rewindOffers = new();
    private int _nextOffer;
    /// <summary>On a ChatMessage link: the Telegram message is the user's own (the bot can't edit it).</summary>
    public const int UserMessageVersion = -1;
    private static readonly TimeSpan RewindPreviewLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AdminCacheTime = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan HintInterval = TimeSpan.FromMinutes(10);
    private readonly ConcurrentDictionary<(long Chat, long User), (bool IsAdmin, DateTime Until)> _admins = new();

    public static readonly (string Command, string Description)[] Commands =
    [
        ("status", "Post a fresh status card"),
        ("run", "Start the book (plan and write)"),
        ("continue", "Continue where the book stopped"),
        ("stop", "Stop the running agent"),
        ("export", "Send the book as a file (epub, fb2, html or metadata)"),
        ("note", "Add a note the agents follow: /note text"),
        ("questions", "Show the planner's open question"),
        ("rewind", "Rewind the book to an earlier moment (reply to a message, or pick one)"),
        ("unbind", "Disconnect this chat from the book"),
        ("help", "What the bot does here"),
    ];

    public async Task HandleAsync(TgUpdate update, CancellationToken ct)
    {
        try
        {
            if (update.Message is { } message) await OnMessageAsync(message, isChannel: false, ct);
            else if (update.ChannelPost is { } post) await OnMessageAsync(post, isChannel: true, ct);
            else if (update.CallbackQuery is { } callback) await OnCallbackAsync(callback, ct);
            else if (update.MyChatMember is { } member) await OnMembershipAsync(member);
            else if (update.StoppedMessageGeneration is { } stopped) await OnDraftStoppedAsync(stopped);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Telegram: update {UpdateId} failed — {Message}", update.UpdateId, ex.Message);
        }
    }

    // ── Messages ──────────────────────────────────────────────────────────────

    private async Task OnMessageAsync(TgMessage message, bool isChannel, CancellationToken ct)
    {
        if (message.MigrateToChatId is long newChatId)
        {
            await WithScopeAsync(async sp =>
            {
                var tg = sp.GetRequiredService<ITelegramRepository>();
                if (await tg.MoveChatAsync(message.Chat.Id, newChatId) > 0)
                    foreach (var b in await tg.GetActiveBindingsInChatAsync(newChatId)) dispatcher.Reload(b.Id);
            });
            return;
        }
        if (message.From is { IsBot: true } && message.From.Id != 1087968824) return;   // other bots (1087968824 = GroupAnonymousBot)

        var text = (message.Text ?? message.Caption ?? string.Empty).Trim();
        if (text.Length == 0) return;

        var (command, args) = ParseCommand(text);
        if (command is "start" or "bind" && BindCodeFrom(args) is { } code)
        {
            await WithScopeAsync(async sp =>
            {
                var error = await sp.GetRequiredService<TelegramBindingService>().BindAsync(message, code);
                if (error is not null) await ReplyAsync(message, error);
            });
            return;
        }

        await WithScopeAsync(async sp =>
        {
            var binding = await sp.GetRequiredService<ITelegramRepository>().FindActiveBindingAsync(message.Chat.Id, message.TopicId);
            if (binding is null)
            {
                await OnUnboundAsync(sp, message, command);
                return;
            }
            if (isChannel) return;   // channels only receive; /bind is handled above
            if (command is not null)
            {
                await OnCommandAsync(sp, binding, message, command, args);
                return;
            }
            if (message.ReplyToMessage is { } replied
                && await sp.GetRequiredService<ITelegramRepository>().GetLinkByMessageAsync(binding.Id, replied.MessageId) is { } link
                && link.Kind is TelegramLinkKind.Clarification or TelegramLinkKind.Question)
            {
                await OnAnswerAsync(sp, binding, message, link, text);
                return;
            }
            await OnPlainMessageAsync(sp, binding, message, text);
        });
    }

    /// <summary>"/cmd@bot args" → ("cmd", "args"); null command for a plain message or a command for another bot.</summary>
    private (string? Command, string Args) ParseCommand(string text)
    {
        if (!text.StartsWith('/')) return (null, text);
        var space = text.IndexOfAny([' ', '\n']);
        var head = space < 0 ? text[1..] : text[1..space];
        var args = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        var at = head.IndexOf('@');
        if (at >= 0)
        {
            if (!string.Equals(head[(at + 1)..], bot.Username, StringComparison.OrdinalIgnoreCase)) return (null, text);
            head = head[..at];
        }
        return (head.ToLowerInvariant(), args);
    }

    private static string? BindCodeFrom(string args)
    {
        var first = args.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return TelegramBindingService.LooksLikeCode(first) ? first : null;
    }

    private async Task OnUnboundAsync(IServiceProvider sp, TgMessage message, string? command)
    {
        // In groups, chatter in topics that are not connected is not the bot's business — only commands get an answer.
        if (message.Chat.Type != "private" && command is null) return;
        if (message.Chat.Type == "channel") return;

        var elsewhere = await sp.GetRequiredService<ITelegramRepository>().GetActiveBindingsInChatAsync(message.Chat.Id);
        if (elsewhere.Count > 0)
        {
            var titles = new List<string>();
            foreach (var b in elsewhere)
                titles.Add((await sp.GetRequiredService<IBookRepository>().GetByIdAsync(b.BookId))?.Title ?? "a book");
            await ReplyAsync(message,
                $"📚 Books here have their own {(elsewhere.Any(b => b.ThreadId is not null) ? "topics" : "chats")}: " +
                string.Join(", ", titles.Select(t => $"<b>{H(Clip(t, 80))}</b>")) + ". Write in the book's topic.");
            return;
        }
        await ReplyAsync(message,
            "👋 This is the abook bot. It isn't connected to a book here yet.\n\n" +
            "To connect one, the book's owner opens <b>Book Settings → Telegram</b> in abook, makes a code and sends it here " +
            "(<code>/bind CODE</code>), or uses the links shown there.");
    }

    private async Task OnCommandAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message, string command, string args)
    {
        var worker = dispatcher.Worker(binding.Id);
        var origin = Origin(message.From, binding);
        switch (command)
        {
            case "help":
            case "start":
                await ReplyAsync(message, await HelpAsync(sp, binding));
                break;
            case "status":
                if (worker is not null) dispatcher.Enqueue(binding.Id, w => WithScopeAsync(s => w.PostStatusCardAsync(s)));
                break;
            case "export":
                if (!await RequireAsync(binding, TelegramAccessLevel.Participate, message.From, message)) return;
                var format = args.Trim().ToLowerInvariant();
                dispatcher.Enqueue(binding.Id, w => WithScopeAsync(s => w.SendExportAsync(s, format.Length > 0 ? format : null, origin)));
                break;
            case "note":
                if (!await RequireAsync(binding, TelegramAccessLevel.Participate, message.From, message)) return;
                await AddNoteAsync(sp, binding, message, args, origin);
                break;
            case "questions":
                if (!await RequireAsync(binding, TelegramAccessLevel.Participate, message.From, message)) return;
                await ShowQuestionsAsync(sp, binding, message);
                break;
            case "run":
                if (!await RequireAsync(binding, TelegramAccessLevel.Control, message.From, message)) return;
                await ReplyAsync(message, await StartRunAsync(sp, binding, continueRun: false, origin));
                break;
            case "continue":
                if (!await RequireAsync(binding, TelegramAccessLevel.Control, message.From, message)) return;
                await ReplyAsync(message, await StartRunAsync(sp, binding, continueRun: true, origin));
                break;
            case "stop":
                if (!await RequireAsync(binding, TelegramAccessLevel.Control, message.From, message)) return;
                await ReplyAsync(message, await StopRunAsync(sp, binding, origin));
                break;
            case "rewind":
                // Throws work away: Control, and in groups only the chat's admins.
                if (!await RequireAsync(binding, TelegramAccessLevel.Control, message.From, message, adminsAlways: true)) return;
                await RewindCommandAsync(sp, binding, message);
                break;
            case "unbind":
                if (!await RequireAsync(binding, TelegramAccessLevel.Control, message.From, message, adminsAlways: true)) return;
                await sp.GetRequiredService<TelegramBindingService>()
                    .RevokeAsync(binding, $"disconnected by {message.From?.Display ?? "an admin"} with /unbind", tellChat: true, origin);
                break;
            default:
                await ReplyAsync(message, "🤔 Unknown command. /help lists what the bot can do here.");
                break;
        }
    }

    private async Task<string> HelpAsync(IServiceProvider sp, TelegramBinding binding)
    {
        var book = await sp.GetRequiredService<IBookRepository>().GetByIdAsync(binding.BookId);
        var interactive = book?.Mode == BookMode.Interactive;
        var lines = new List<string>
        {
            $"📖 This {(binding.ThreadId is null ? "chat" : "topic")} follows <b>{H(book?.Title ?? "a book")}</b>.",
            $"Everyone here can {H(LevelText(binding.Level))}.",
            "",
            "The pinned card shows what the agents are doing. Chapter files arrive after each chapter edit, and the whole book when it is finished.",
        };
        if (binding.Level >= TelegramAccessLevel.Participate)
        {
            lines.Add(interactive
                ? "💬 Write here to talk to the book chat; its suggested changes come with buttons."
                : "❓ When the planner asks something, <b>reply to its question message</b> to answer.");
            lines.Add("");
            lines.Add("/export [epub|fb2|html|metadata] — the book as it stands");
            lines.Add("/note text — a note the agents follow from now on");
            lines.Add("/questions — the planner's open question");
        }
        if (binding.Level == TelegramAccessLevel.Control)
        {
            if (!interactive) lines.Add("/run · /continue — start or continue the book");
            lines.Add("/stop — stop the running agent");
            lines.Add("/rewind — reply to a message to rewind the book to that moment (or pick one)");
            lines.Add("/unbind — disconnect this chat");
        }
        lines.Add("/status — post a fresh status card");
        if (binding.Level == TelegramAccessLevel.Control && binding.ControlAdminsOnly)
            lines.Add("\n🔒 Starting, stopping and applying are for chat admins only.");
        return string.Join("\n", lines);
    }

    private async Task AddNoteAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message, string text, string origin)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            await ReplyAsync(message, "✍️ Write the note after the command: <code>/note The heroine never lies.</code>");
            return;
        }
        // An author note: the agents read it with the premise from the next step on (Clarifications page in abook).
        await sp.GetRequiredService<IBookRepository>().AddClarificationAsync(new Clarification
        {
            BookId = binding.BookId, Source = ClarificationSource.Author,
            Question = "Note from Telegram", Answer = Clip(text, 4000), Origin = origin,
        });
        await ReplyAsync(message, "📝 Note added. The agents follow it from their next step; it is on the Clarifications page in abook.");
    }

    private async Task ShowQuestionsAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message)
    {
        var open = (await sp.GetRequiredService<IBookRepository>().GetClarificationsAsync(binding.BookId)).Count(c => c.IsOpen);
        if (open == 0)
        {
            await ReplyAsync(message, "✅ No open questions.");
            return;
        }
        if (runState.GetStatus(binding.BookId) is { State: RunStates.WaitingForInput })
            dispatcher.Worker(binding.Id)?.AskNextClarification(again: true);
        else
            await ReplyAsync(message, $"❓ {open} open question(s). They are asked here while planning waits for them; " +
                                      "you can also answer them on the Clarifications page in abook.");
    }

    private async Task OnAnswerAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message, TelegramMessageLink link, string text)
    {
        if (!await RequireAsync(binding, TelegramAccessLevel.Participate, message.From, message)) return;
        var origin = Origin(message.From, binding);
        var repo = sp.GetRequiredService<IBookRepository>();

        if (link.Kind == TelegramLinkKind.Clarification)
        {
            var clarification = await repo.GetClarificationAsync(binding.BookId, link.RefId);
            if (clarification is null || !clarification.IsOpen)
            {
                await ReplyAsync(message, $"ℹ️ This question was already answered{(clarification?.Origin is { } by ? $" ({H(by)})" : "")}.");
            }
            else
            {
                await sp.GetRequiredService<ClarificationService>().UpdateAsync(clarification, null, text, null, origin);
                await ReactAsync(message, "👍");
            }
            dispatcher.Worker(binding.Id)?.AskNextClarification();
            dispatcher.Worker(binding.Id)?.RefreshStatus();
            return;
        }

        var question = await repo.FindMessageByIdAsync(link.RefId);
        if (question is not { MessageType: MessageType.Question } || question.BookId != binding.BookId) return;
        if (question.IsResolved)
        {
            await ReplyAsync(message, "ℹ️ This question was already answered.");
            return;
        }
        await sp.GetRequiredService<IAgentOrchestrator>().ResumeWithAnswerAsync(question.Id, text, origin: origin);
        await ReactAsync(message, "👍");
    }

    private async Task OnPlainMessageAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message, string text)
    {
        var book = await sp.GetRequiredService<IBookRepository>().GetByIdAsync(binding.BookId);
        if (book?.Mode == BookMode.Interactive && binding.Level >= TelegramAccessLevel.Participate)
        {
            var outcome = await chat.SendAsync(binding.BookId, text, Origin(message.From, binding));
            if (outcome.MessageId is int chatMessageId)
                // Which Telegram message it was: /rewind as a reply to it rewinds to just before it.
                await sp.GetRequiredService<ITelegramRepository>().AddLinkAsync(new TelegramMessageLink
                {
                    BindingId = binding.Id, Kind = TelegramLinkKind.ChatMessage, RefId = chatMessageId,
                    MessageId = message.MessageId, Version = UserMessageVersion,
                    ContentHash = outcome.Result == BookChatResult.Started ? nameof(BookChatMessageStatus.Pending) : null,
                });
            if (outcome.Result == BookChatResult.Started) await ReactAsync(message, "👀");
            else await ReplyAsync(message, "⏳ " + H(outcome.Message ?? "The book chat can't take a message right now."));
            dispatcher.Worker(binding.Id)?.SyncChatSoon();
            return;
        }

        var worker = dispatcher.Worker(binding.Id);
        if (worker is null || DateTime.UtcNow - worker.LastHint < HintInterval) return;
        worker.LastHint = DateTime.UtcNow;
        await ReplyAsync(message, book?.Mode == BookMode.Interactive
            ? "ℹ️ This chat can only follow the book, not write in its chat."
            : "ℹ️ Messages here don't reach the agents. Use the buttons, <b>reply to a question</b> to answer it, " +
              "or /note to add a note for the book. /help lists the commands.");
    }

    // ── Runs ──────────────────────────────────────────────────────────────────

    private async Task<string> StartRunAsync(IServiceProvider sp, TelegramBinding binding, bool continueRun, string origin)
    {
        var repo = sp.GetRequiredService<IBookRepository>();
        var book = await repo.GetByIdAsync(binding.BookId);
        if (book is null) return "❌ The book no longer exists.";
        if (book.Mode == BookMode.Interactive)
            return "💬 This is an Interactive book: it is written in the book chat — just write here.";

        var bookId = book.Id;
        var result = continueRun
            ? runner.TryStart(bookId, AgentRole.Writer, null, (o, c) => o.ContinueWorkflowAsync(bookId, c))
            : runner.TryStart(bookId, AgentRole.Planner, null, (o, c) => o.StartWorkflowAsync(bookId, c));
        if (result != RunStartResult.Success) return "⏳ " + H(result.ToMessage());

        await repo.AddMessageAsync(new AgentMessage
        {
            BookId = bookId, AgentRole = continueRun ? AgentRole.Writer : AgentRole.Planner, MessageType = MessageType.SystemNote,
            Content = continueRun ? "⏩ Workflow continued from Telegram." : "▶️ Workflow started from Telegram.",
            IsResolved = true, Origin = origin,
        });
        await sp.GetRequiredService<IBookNotifier>().NotifyMessagesUpdatedAsync(bookId);
        dispatcher.Worker(binding.Id)?.RefreshStatus();
        return continueRun ? "⏩ Continuing the book." : "▶️ Starting the book.";
    }

    private async Task<string> StopRunAsync(IServiceProvider sp, TelegramBinding binding, string origin)
    {
        if (runState.GetStatus(binding.BookId) is not { IsActive: true }) return "ℹ️ Nothing is running.";
        runState.CancelRun(binding.BookId);
        await sp.GetRequiredService<IBookRepository>().AddMessageAsync(new AgentMessage
        {
            BookId = binding.BookId, AgentRole = runState.GetStatus(binding.BookId)?.Role ?? AgentRole.Writer,
            MessageType = MessageType.SystemNote, Content = "⏹ Stopped from Telegram.", IsResolved = true, Origin = origin,
        });
        await sp.GetRequiredService<IBookNotifier>().NotifyMessagesUpdatedAsync(binding.BookId);
        return "⏹ Stopping…";
    }

    // ── Buttons ───────────────────────────────────────────────────────────────

    private async Task OnCallbackAsync(TgCallbackQuery callback, CancellationToken ct)
    {
        string? answer = null;
        var alert = false;
        try
        {
            if (!TryParseCallback(callback.Data, out var bindingId, out var action, out var arg) || callback.Message is not { } message)
            {
                answer = "This button is no longer valid.";
                return;
            }
            await WithScopeAsync(async sp =>
            {
                var binding = await sp.GetRequiredService<ITelegramRepository>().GetBindingAsync(bindingId);
                // The press must come from the binding's own chat (and topic, when Telegram still tells which).
                if (binding is not { IsActive: true } || binding.ChatId != message.Chat.Id
                    || (message.Date != 0 && message.TopicId != binding.ThreadId))
                {
                    answer = "This chat is no longer connected to the book.";
                    return;
                }
                (answer, alert) = await OnButtonAsync(sp, binding, callback, message, action, arg);
            });
        }
        finally
        {
            try { await api.AnswerCallbackQueryAsync(callback.Id, answer, alert, ct); }
            catch (TelegramApiException) { /* too old to answer */ }
        }
    }

    private async Task<(string? Answer, bool Alert)> OnButtonAsync(
        IServiceProvider sp, TelegramBinding binding, TgCallbackQuery callback, TgMessage message, string action, long longArg)
    {
        // Ids fit an int; rewind moments (microseconds since 1970) need the long.
        var arg = (int)Math.Clamp(longArg, int.MinValue, int.MaxValue);
        var worker = dispatcher.Worker(binding.Id);
        var origin = Origin(callback.From, binding);
        var need = action switch
        {
            "refresh" => TelegramAccessLevel.Read,
            "export" or "skipc" or "retry" => TelegramAccessLevel.Participate,
            _ => TelegramAccessLevel.Control,
        };
        var isRewind = action is "rwat" or "rwgo" or "rwno";
        if (await DenialAsync(binding, need, callback.From, null, adminsAlways: isRewind) is { } denied) return (denied, true);
        if (isRewind) return await OnRewindButtonAsync(sp, binding, callback, message, action, longArg);

        switch (action)
        {
            case "refresh":
                worker?.RefreshStatus();
                return ("Refreshed.", false);
            case "export":
                dispatcher.Enqueue(binding.Id, w => WithScopeAsync(s => w.SendExportAsync(s, null, origin)));
                return ("Preparing the file…", false);
            case "start":
            case "cont":
                return (Plain(await StartRunAsync(sp, binding, continueRun: action == "cont", origin)), false);
            case "stop":
                return (Plain(await StopRunAsync(sp, binding, origin)), false);
            case "skipc":
                return (await SkipClarificationAsync(sp, binding, message, arg, origin), false);
            case "apply":
                return await ChatOutcomeAsync(binding, message, await chat.ApplyAsync(binding.BookId, [arg], force: false), "applyf", arg);
            case "applyf":
                return await ChatOutcomeAsync(binding, message, await chat.ApplyAsync(binding.BookId, [arg], force: true), null, arg);
            case "applyall":
                var pending = (await sp.GetRequiredService<IBookRepository>().GetChatMessageAsync(binding.BookId, arg))?.Proposals
                    .Where(p => p.Status == ChatProposalStatus.Pending).OrderBy(p => p.Ordinal).Select(p => p.Id).ToList() ?? [];
                if (pending.Count == 0) return ("Nothing left to apply.", false);
                return await ChatOutcomeAsync(binding, message, await chat.ApplyAsync(binding.BookId, pending, force: false), null, arg);
            case "reject":
                return await ChatOutcomeAsync(binding, message, await chat.RejectAsync(binding.BookId, [arg]), null, arg);
            case "undo":
                return await ChatOutcomeAsync(binding, message, await chat.UndoAsync(binding.BookId, arg, force: false), "undof", arg);
            case "undof":
                return await ChatOutcomeAsync(binding, message, await chat.UndoAsync(binding.BookId, arg, force: true), null, arg);
            case "retry":
                return await ChatOutcomeAsync(binding, message, await chat.RetryAsync(binding.BookId, arg), null, arg);
            default:
                return ("This button is no longer valid.", false);
        }
    }

    /// <summary>Turns a book-chat outcome into the button's answer; a stale change asks for confirmation with a new button.</summary>
    private async Task<(string? Answer, bool Alert)> ChatOutcomeAsync(
        TelegramBinding binding, TgMessage message, BookChatOutcome outcome, string? confirmAction, int arg)
    {
        dispatcher.Worker(binding.Id)?.SyncChatSoon();
        switch (outcome.Result)
        {
            case BookChatResult.Started:
            case BookChatResult.Done:
                return (outcome.Message ?? "Done.", false);
            case BookChatResult.Stale when confirmAction is not null:
                var keyboard = new List<IReadOnlyList<TgButton>>
                {
                    new[] { new TgButton(confirmAction == "applyf" ? "⚠️ Apply anyway" : "⚠️ Undo anyway", Callback(binding.Id, confirmAction, arg), Style: "danger") },
                };
                await api.SendMessageAsync(binding.ChatId, binding.ThreadId, "⚠️ " + H(outcome.Message ?? "It changed since."),
                    keyboard, replyTo: message.Date != 0 ? message.MessageId : null);
                return ("It changed since — confirm below.", false);
            default:
                return (Clip(outcome.Message ?? "Not possible right now.", 190), true);
        }
    }

    private async Task<string> SkipClarificationAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message, int clarificationId, string origin)
    {
        var clarification = await sp.GetRequiredService<IBookRepository>().GetClarificationAsync(binding.BookId, clarificationId);
        if (clarification is { IsOpen: true })
            await sp.GetRequiredService<ClarificationService>().UpdateAsync(clarification, null, null, archived: true, origin);
        if (message.Date != 0)
        {
            try { await api.EditMessageKeyboardAsync(binding.ChatId, message.MessageId, null); }
            catch (TelegramApiException) { }
        }
        dispatcher.Worker(binding.Id)?.AskNextClarification();
        dispatcher.Worker(binding.Id)?.RefreshStatus();
        return clarification is { IsOpen: false } && clarification.IsArchived ? "Skipped." : "Already answered.";
    }

    // ── Rewind ────────────────────────────────────────────────────────────────

    /// <summary>/rewind as a reply: preview a rewind to that message's moment. Plain /rewind: pick from recent moments.</summary>
    private async Task RewindCommandAsync(IServiceProvider sp, TelegramBinding binding, TgMessage message)
    {
        var rewinder = sp.GetRequiredService<IBookRewinder>();
        if (await rewinder.EarliestAsync(binding.BookId) is not { } earliest)
        {
            await ReplyAsync(message, "⏪ The book has no history to rewind yet.");
            return;
        }
        if (message.ReplyToMessage is { } replied)
        {
            var at = await MomentOfAsync(sp, binding, replied);
            if (at < earliest)
            {
                await ReplyAsync(message, $"⏪ That's before the book's history begins ({MomentText(earliest)}).");
                return;
            }
            await SendRewindPreviewAsync(sp, binding, at, message.MessageId);
            return;
        }

        var points = await rewinder.PointsAsync(binding.BookId, null, 8);
        if (points.Count == 0)
        {
            await ReplyAsync(message, "⏪ No moments to rewind to yet.");
            return;
        }
        var keyboard = points
            .Select(p => (IReadOnlyList<TgButton>)[new TgButton($"{p.At:dd MMM HH:mm} · {Clip(p.Label, 48)}", Callback(binding.Id, "rwat", Micros(p.At)))])
            .ToList();
        await ReplyAsync(message, "⏪ <b>Rewind to which moment?</b>\n<i>Or reply /rewind to any message of this thread to rewind to when it was posted.</i>", keyboard);
    }

    /// <summary>
    /// The moment a replied-to message stands for: a message of the author's in the book chat → just before it; a
    /// message the bot posted → when it was posted; anything else → its Telegram date (a second's precision).
    /// </summary>
    private static async Task<DateTime> MomentOfAsync(IServiceProvider sp, TelegramBinding binding, TgMessage replied)
    {
        if (await sp.GetRequiredService<ITelegramRepository>().GetLinkByMessageAsync(binding.Id, replied.MessageId) is { } link)
        {
            if (link is { Kind: TelegramLinkKind.ChatMessage, Version: UserMessageVersion }
                && await sp.GetRequiredService<IBookRepository>().GetChatMessageAsync(binding.BookId, link.RefId) is { } chatMessage)
                return chatMessage.CreatedAt.AddTicks(-1);
            return link.CreatedAt;
        }
        var date = DateTimeOffset.FromUnixTimeSeconds(replied.Date).UtcDateTime;
        return replied.From is { IsBot: false } ? date.AddSeconds(-1) : date.AddSeconds(1);
    }

    private async Task SendRewindPreviewAsync(IServiceProvider sp, TelegramBinding binding, DateTime at, int? replyTo)
    {
        RewindPlan plan;
        try { plan = await sp.GetRequiredService<IBookRewinder>().PreviewAsync(binding.BookId, at); }
        catch (RewindTooEarlyException ex)
        {
            await api.SendMessageAsync(binding.ChatId, binding.ThreadId, "⏪ " + H(ex.Message), replyTo: replyTo);
            return;
        }
        if (plan.IsEmpty)
        {
            await api.SendMessageAsync(binding.ChatId, binding.ThreadId,
                $"⏪ The book is already as it was at {MomentText(at)} — nothing to rewind.", replyTo: replyTo);
            return;
        }

        foreach (var (key, old) in _rewindOffers)
            if (old.Expires < DateTime.UtcNow) _rewindOffers.TryRemove(key, out _);
        var id = Interlocked.Increment(ref _nextOffer);
        _rewindOffers[id] = new RewindOffer(binding.Id, at, plan.Fingerprint, DateTime.UtcNow + RewindPreviewLifetime);
        var keyboard = new List<IReadOnlyList<TgButton>>
        {
            new[] { new TgButton("⏪ Rewind", Callback(binding.Id, "rwgo", id), Style: "danger"), new TgButton("Cancel", Callback(binding.Id, "rwno", id)) },
        };
        await api.SendMessageAsync(binding.ChatId, binding.ThreadId, RewindPreview(plan), keyboard, replyTo: replyTo);
    }

    private async Task<(string? Answer, bool Alert)> OnRewindButtonAsync(
        IServiceProvider sp, TelegramBinding binding, TgCallbackQuery callback, TgMessage message, string action, long arg)
    {
        switch (action)
        {
            case "rwat":
                await SendRewindPreviewAsync(sp, binding, FromMicros(arg), message.Date != 0 ? message.MessageId : null);
                return ("Preview below.", false);
            case "rwno":
                _rewindOffers.TryRemove((int)arg, out _);
                await EditAsync(binding, message, "⏪ <i>Rewind cancelled.</i>");
                return ("Cancelled.", false);
        }

        // rwgo
        if (!_rewindOffers.TryRemove((int)arg, out var offer) || offer.BindingId != binding.Id || offer.Expires < DateTime.UtcNow)
        {
            await EditAsync(binding, message, "⏪ <i>This preview expired. Use /rewind again.</i>");
            return ("This preview expired — use /rewind again.", true);
        }
        var by = callback.From.Display;
        var outcome = await rewinds.StartAsync(binding.BookId, offer.At, offer.Fingerprint, Origin(callback.From, binding));
        switch (outcome.Result)
        {
            case RewindStartResult.Started:
                await EditAsync(binding, message, $"⏪ <b>Rewinding to {MomentText(offer.At)}</b> — confirmed by {H(by)}.");
                return ("Rewinding…", false);
            case RewindStartResult.Stale:
                await EditAsync(binding, message, "⚠️ <i>The book changed since this preview. Use /rewind again.</i>");
                return ("The book changed since — preview again.", true);
            default:
                // Busy and the like: the preview stays good for another try.
                _rewindOffers[(int)arg] = offer;
                return (Clip(outcome.Message, 190), true);
        }
    }

    private async Task EditAsync(TelegramBinding binding, TgMessage message, string html)
    {
        if (message.Date == 0) return;
        try { await api.EditMessageTextAsync(binding.ChatId, message.MessageId, html); }
        catch (TelegramApiException) { /* too old or unchanged */ }
    }

    // ── Membership and drafts ─────────────────────────────────────────────────

    private async Task OnMembershipAsync(TgChatMemberUpdated update)
    {
        if (update.NewChatMember.Status is not ("left" or "kicked")) return;
        await WithScopeAsync(async sp =>
        {
            var service = sp.GetRequiredService<TelegramBindingService>();
            foreach (var binding in await sp.GetRequiredService<ITelegramRepository>().GetActiveBindingsInChatAsync(update.Chat.Id))
                await service.RevokeAsync(binding, "the bot was removed from the chat", tellChat: false, Origin(update.From, binding));
        });
    }

    /// <summary>The stop button of a live draft: stops the run (Control only) and the drafts.</summary>
    private async Task OnDraftStoppedAsync(TgStoppedGeneration stopped)
    {
        await WithScopeAsync(async sp =>
        {
            var tg = sp.GetRequiredService<ITelegramRepository>();
            var binding = await tg.FindActiveBindingAsync(stopped.Chat.Id, stopped.MessageThreadId)
                          ?? (stopped.MessageThreadId is null ? null : await tg.FindActiveBindingAsync(stopped.Chat.Id, null));
            if (binding is null) return;
            dispatcher.Worker(binding.Id)?.StopDrafts();
            if (binding.Level != TelegramAccessLevel.Control) return;
            await StopRunAsync(sp, binding, $"Telegram · draft stop button · {binding.ChatTitle}");
        });
    }

    // ── Access ────────────────────────────────────────────────────────────────

    /// <summary>Null when allowed; otherwise why not.</summary>
    private async Task<string?> DenialAsync(TelegramBinding binding, TelegramAccessLevel need, TgUser? user, TgMessage? message, bool adminsAlways = false)
    {
        if (binding.IsChannel && need > TelegramAccessLevel.Read) return "This channel only receives updates.";
        if (binding.Level < need) return $"🔒 Here everyone can {LevelText(binding.Level)} — not more.";
        var adminsOnly = need == TelegramAccessLevel.Control && (binding.ControlAdminsOnly || adminsAlways);
        if (adminsOnly && !binding.IsPrivate && !await IsAdminAsync(binding.ChatId, user, message))
            return "🔒 Only chat admins can do that here.";
        return null;
    }

    private async Task<bool> RequireAsync(
        TelegramBinding binding, TelegramAccessLevel need, TgUser? user, TgMessage message, bool adminsAlways = false)
    {
        var denied = await DenialAsync(binding, need, user, message, adminsAlways);
        if (denied is null) return true;
        await ReplyAsync(message, H(denied));
        return false;
    }

    private async Task<bool> IsAdminAsync(long chatId, TgUser? user, TgMessage? message)
    {
        // An anonymous admin posts as the group itself.
        if (message?.SenderChat?.Id == chatId) return true;
        if (user is null) return false;
        var key = (chatId, user.Id);
        if (_admins.TryGetValue(key, out var cached) && cached.Until > DateTime.UtcNow) return cached.IsAdmin;
        bool isAdmin;
        try { isAdmin = (await api.GetChatMemberAsync(chatId, user.Id)).Status is "creator" or "administrator"; }
        catch (TelegramApiException) { isAdmin = false; }
        _admins[key] = (isAdmin, DateTime.UtcNow + AdminCacheTime);
        return isAdmin;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private async Task ReplyAsync(TgMessage message, string html, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null)
    {
        try { await api.SendMessageAsync(message.Chat.Id, message.TopicId, html, keyboard, replyTo: message.MessageId); }
        catch (TelegramApiException ex) { logger.LogInformation("Telegram: reply to {ChatId} failed — {Description}", message.Chat.Id, ex.Description); }
    }

    private async Task ReactAsync(TgMessage message, string emoji)
    {
        try { await api.SetReactionAsync(message.Chat.Id, message.MessageId, emoji); }
        catch (TelegramApiException) { /* reactions may be off in this chat */ }
    }

    /// <summary>Button answers are plain text (no HTML) and at most 200 characters.</summary>
    private static string Plain(string html) =>
        Clip(System.Net.WebUtility.HtmlDecode(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", "")), 190);

    private async Task WithScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = scopes.CreateScope();
        await work(scope.ServiceProvider);
    }
}
