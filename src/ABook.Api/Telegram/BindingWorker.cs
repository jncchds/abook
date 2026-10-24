using System.Text.Json;
using System.Threading.Channels;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using static ABook.Api.Telegram.TelegramFormat;

namespace ABook.Api.Telegram;

/// <summary>
/// Everything the bot sends to one bound chat or topic, one thing at a time and paced to Telegram's limits
/// (about one message a second in a private chat, twenty a minute in a group). Book events only set flags and
/// due times; the loop then does what is due: ask the next clarification, send changed chapter files, the planning
/// and whole-book files, rendered illustrations, book-chat replies, the status card and live drafts.
/// </summary>
public sealed class BindingWorker
{
    private static readonly TimeSpan IdleChapterDebounce = TimeSpan.FromSeconds(90);
    private static readonly TimeSpan RunChapterDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DraftInterval = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ClarificationRecheck = TimeSpan.FromSeconds(20);

    private readonly TelegramDispatcher _d;
    private readonly Channel<object> _inbox = Channel.CreateUnbounded<object>(new UnboundedChannelOptions { SingleReader = true });
    private readonly CancellationTokenSource _cts = new();
    private readonly bool _isNew;
    private TelegramBinding _b;

    // Due work
    private bool _statusDirty = true, _statusUrgent = true, _askClarification = true;
    private bool _planningRunSeen, _bookCheck, _askAgain;
    /// <summary>A rewind just happened: the next chapter files say so.</summary>
    private bool _rewound;
    private DateTime _lastStatusEdit = DateTime.MinValue, _clarifyCheckAt = DateTime.MinValue, _lastSend = DateTime.MinValue;
    private string? _lastStatusHash;
    /// <summary>The pinned card is a plain message (posted before rich messages): null until the first edit tells.</summary>
    private bool? _statusPlain;
    private DateTime? _chapterSyncAt, _illustrationSyncAt, _chatSyncAt;
    /// <summary>Last chapter text sent per chapter (loaded from the links once), so a sync costs one query.</summary>
    private Dictionary<int, string?>? _sentChapterHashes;

    // Run state seen through events
    private string? _lastStep;
    private bool _runEnded = true;
    private long _prompt, _completion;

    // Drafts
    private bool _draftStopped;
    private DateTime _lastDraft = DateTime.MinValue;
    private long _lastStreamSeen, _lastStatusStreamSeen;

    /// <summary>When the bot last explained to this thread that plain messages do nothing (to not repeat it).</summary>
    public DateTime LastHint { get; set; } = DateTime.MinValue;

    public BindingWorker(TelegramDispatcher dispatcher, TelegramBinding binding, bool isNew)
    {
        _d = dispatcher;
        _b = binding;
        _isNew = isNew;
        if (!isNew)
        {
            // After a restart: catch up on anything that changed while the server was down.
            var soon = DateTime.UtcNow.AddSeconds(10);
            _chapterSyncAt = _illustrationSyncAt = _chatSyncAt = soon;
        }
    }

    public int BindingId => _b.Id;
    public int BookId => _b.BookId;
    public TelegramBinding Binding => _b;

    public void Start(CancellationToken stopping)
    {
        stopping.Register(() => _cts.Cancel());
        _ = Task.Run(RunAsync);
    }

    public void Stop() => _cts.Cancel();
    public void Post(TgBookEvent ev) => _inbox.Writer.TryWrite(ev);
    public void Enqueue(Func<BindingWorker, Task> job) => _inbox.Writer.TryWrite(job);

    // Called from the update handler too: the state changes run on the worker's own loop, like everything else.

    /// <summary>The run was stopped from a draft: no more drafts until the next run.</summary>
    public void StopDrafts() => OnLoop(() => _draftStopped = true);
    public void RefreshStatus() => OnLoop(() => { _statusDirty = true; _statusUrgent = true; });
    /// <summary>Asks the next open clarification; <paramref name="again"/> posts the one still waiting anew (/questions).</summary>
    public void AskNextClarification(bool again = false) => OnLoop(() =>
    {
        _askClarification = true;
        if (again) _askAgain = true;
    });
    public void SyncChatSoon() => OnLoop(() => _chatSyncAt = DateTime.UtcNow);

    private void OnLoop(Action change) => Enqueue(_ =>
    {
        change();
        return Task.CompletedTask;
    });

    private async Task RunAsync()
    {
        var ct = _cts.Token;
        try
        {
            if (_isNew) await WithScopeAsync(Welcome);
            while (!ct.IsCancellationRequested)
            {
                using (var wait = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    wait.CancelAfter(TimeSpan.FromSeconds(1));
                    try { await _inbox.Reader.WaitToReadAsync(wait.Token); }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
                while (_inbox.Reader.TryRead(out var item))
                {
                    if (item is TgBookEvent ev) await GuardAsync(() => OnEventAsync(ev));
                    else if (item is Func<BindingWorker, Task> job) await GuardAsync(() => job(this));
                }
                await GuardAsync(DoDueWorkAsync);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (BindingGoneException) { }
        finally
        {
            _d.OnWorkerEnded(this);
        }
    }

    /// <summary>Runs one piece of work; a failure is logged and never stops the loop (only a gone chat does).</summary>
    private async Task GuardAsync(Func<Task> work)
    {
        try { await work(); }
        catch (BindingGoneException) { throw; }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _d.Logger.LogWarning(ex, "Telegram: binding {BindingId} (book {BookId}) — {Message}", _b.Id, _b.BookId, ex.Message);
        }
    }

    // ── Events ────────────────────────────────────────────────────────────────

    private async Task OnEventAsync(TgBookEvent ev)
    {
        var now = DateTime.UtcNow;
        switch (ev)
        {
            // Agents report "Done" for each sub-step (writer done, editor done…); only a progress event with
            // IsComplete marks the end of the whole run.
            case TgStatusEvent s:
                OnRunActivity();
                if (s.Role is AgentRole.Planner or AgentRole.StoryBibleAgent or AgentRole.CharactersAgent
                        or AgentRole.PlotThreadsAgent or AgentRole.ChaptersAgent)
                    _planningRunSeen = true;
                _statusDirty = true;
                if (s.State == RunStates.WaitingForInput) { _askClarification = true; _statusUrgent = true; }
                if (s.State == RunStates.Cancelled) await SendTextAsync("⏹ The run was stopped.", silent: true);
                break;
            case TgProgressEvent p:
                _lastStep = p.Step;
                _statusDirty = true;
                _chapterSyncAt = Min(_chapterSyncAt, now + RunChapterDelay);
                if (p.IsComplete)
                {
                    _runEnded = true;
                    _statusUrgent = true;
                    _illustrationSyncAt = now;
                    _bookCheck = true;
                }
                else OnRunActivity();
                break;
            case TgQuestionEvent q:
                await WithScopeAsync(sp => SendQuestionAsync(sp, q.MessageId));
                break;
            case TgChapterEvent:
                // During a run the pipeline marks a chapter Done once it is edited; outside a run (manual edits)
                // a burst of saves is sent as one file.
                _chapterSyncAt = _d.RunState.GetStatus(_b.BookId) is { IsActive: true }
                    ? Min(_chapterSyncAt, now + RunChapterDelay)
                    : now + IdleChapterDebounce;
                break;
            case TgTokensEvent t:
                _prompt += t.PromptTokens;
                _completion += t.CompletionTokens;
                _statusDirty = true;
                break;
            case TgErrorEvent e:
                await SendErrorAsync(e);
                break;
            case TgIllustrationEvent:
                _illustrationSyncAt = Min(_illustrationSyncAt, now + TimeSpan.FromSeconds(3));
                break;
            case TgChatEvent:
                _chatSyncAt = Min(_chatSyncAt, now + TimeSpan.FromMilliseconds(500));
                break;
            case TgRewoundEvent r:
                await WithScopeAsync(sp => OnRewoundAsync(sp, r));
                break;
        }
    }

    /// <summary>
    /// The book was rewound (from here, the web or another thread): a divider replying to the last message before the
    /// moment, rewound book-chat messages crossed out and their buttons removed, and the changed chapters' files sent
    /// as new versions.
    /// </summary>
    private async Task OnRewoundAsync(IServiceProvider sp, TgRewoundEvent r)
    {
        var tg = sp.GetRequiredService<ITelegramRepository>();
        var repo = sp.GetRequiredService<IBookRepository>();
        if (!_b.IsChannel)
        {
            foreach (var messageId in r.HiddenChatMessageIds)
            {
                var message = await repo.GetChatMessageAsync(_b.BookId, messageId);
                if (await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.ChatMessage, messageId) is { MessageId: > 0 } link
                    && link.Version != TelegramUpdateHandler.UserMessageVersion && message is not null)
                    await EditQuietlyAsync(link.MessageId,
                        $"⏪ <i>Rewound — no longer part of the book chat.</i>\n<s>{H(Clip(message.Content, 3000))}</s>", null);
                if (await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.ChatProposals, messageId) is { MessageId: > 0 } proposals)
                    await EditQuietlyAsync(proposals.MessageId, "🛠 <s>Suggested changes</s> — ⏪ <i>rewound</i>", null);
            }
        }

        var before = await tg.GetLastLinkBeforeAsync(_b.Id, r.At);
        var who = r.Origin is { Length: > 0 } o ? $" by {H(o)}" : "";
        await SendTextAsync(
            $"⏪ <b>Rewound to {MomentText(r.At)}</b>{who}.\n" +
            "What came after that point no longer counts: every part is back to its state then (as new versions — " +
            "the old ones stay in abook's history), and later book-chat messages are hidden.",
            replyTo: before?.MessageId);

        _rewound = true;
        _chapterSyncAt = DateTime.UtcNow;
        _chatSyncAt = DateTime.UtcNow;
        RefreshStatus();
    }

    /// <summary>Edits a message the bot posted, ignoring ones Telegram no longer lets it edit.</summary>
    private async Task EditQuietlyAsync(int messageId, string html, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard)
    {
        try { await EditAsync(messageId, html, keyboard); }
        catch (TelegramApiException ex) when (!ex.ChatGone) { }
    }

    /// <summary>The first sign of a new run: its token counters and drafts start afresh.</summary>
    private void OnRunActivity()
    {
        if (!_runEnded) return;
        _runEnded = false;
        _prompt = _completion = 0;
        _draftStopped = false;
        _lastStep = null;
    }

    private static DateTime Min(DateTime? a, DateTime b) => a is { } x && x < b ? x : b;

    private async Task DoDueWorkAsync()
    {
        var now = DateTime.UtcNow;
        var run = _d.RunState.GetStatus(_b.BookId);

        if (_askClarification || (run?.State == RunStates.WaitingForInput && now >= _clarifyCheckAt))
        {
            _askClarification = false;
            _clarifyCheckAt = now + ClarificationRecheck;
            await WithScopeAsync(AskClarificationAsync);
        }
        if (_chapterSyncAt is { } chapterAt && now >= chapterAt)
        {
            _chapterSyncAt = null;
            await WithScopeAsync(SyncChaptersAsync);
        }
        if (_illustrationSyncAt is { } artAt && now >= artAt)
        {
            _illustrationSyncAt = null;
            await WithScopeAsync(SyncIllustrationsAsync);
        }
        if (_chatSyncAt is { } chatAt && now >= chatAt)
        {
            _chatSyncAt = null;
            await WithScopeAsync(SyncChatAsync);
        }

        // Live text changes the word count on the card; refresh it now and then while writing.
        var streamTicks = _d.Sink.LastStreamTicks(_b.BookId);
        if (streamTicks != _lastStatusStreamSeen && run is { IsActive: true })
        {
            _lastStatusStreamSeen = streamTicks;
            _statusDirty = true;
        }
        var statusGap = _b.IsPrivate ? TimeSpan.FromSeconds(4) : TimeSpan.FromSeconds(10);
        if (_statusDirty && (_statusUrgent || now - _lastStatusEdit >= statusGap))
        {
            _statusDirty = _statusUrgent = false;
            _lastStatusEdit = now;
            await WithScopeAsync(UpdateStatusCardAsync);
        }

        if (streamTicks != _lastStreamSeen && now - _lastDraft >= DraftInterval)
            await SendDraftAsync(run, streamTicks);
    }

    // ── Sending ───────────────────────────────────────────────────────────────

    /// <summary>On a suggestions or status link: the message was sent as a rich message (edited as one).</summary>
    public const int RichLinkVersion = 2;

    private static async Task<bool> Done(Task task)
    {
        await task;
        return true;
    }

    private TimeSpan MinGap => _b.IsPrivate ? TimeSpan.FromSeconds(1.05) : TimeSpan.FromSeconds(3.1);

    /// <summary>
    /// Every send goes through here: waits for the chat's pacing gap, follows a group→supergroup move once, and ends
    /// the binding when the chat or topic is gone for good.
    /// </summary>
    private async Task<T> SendAsync<T>(Func<long, Task<T>> call)
    {
        var wait = _lastSend + MinGap - DateTime.UtcNow;
        if (wait > TimeSpan.Zero) await Task.Delay(wait, _cts.Token);
        try
        {
            return await call(_b.ChatId);
        }
        catch (TelegramApiException ex) when (ex.MigrateToChatId is long newChatId)
        {
            await WithScopeAsync(async sp =>
            {
                await sp.GetRequiredService<ITelegramRepository>().MoveChatAsync(_b.ChatId, newChatId);
                await ReloadAsync(sp);
            });
            return await call(_b.ChatId);
        }
        catch (TelegramApiException ex) when (ex.ChatGone)
        {
            await WithScopeAsync(sp => EndAsync(sp, $"Telegram refused to deliver: {ex.Description}"));
            throw new BindingGoneException();
        }
        finally
        {
            _lastSend = DateTime.UtcNow;
        }
    }

    public Task<TgMessage> SendTextAsync(
        string html, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard = null, int? replyTo = null, bool silent = false) =>
        SendAsync(chat => _d.Api.SendMessageAsync(chat, _b.ThreadId, html, keyboard, replyTo, silent, _cts.Token));

    public Task<TgMessage> SendDocumentAsync(string fileName, byte[] data, string contentType, string caption, int? replyTo = null) =>
        // Captions are built from parts clipped before escaping (Telegram's 1,024 limit counts the visible text);
        // clipping the HTML itself could cut a tag in half.
        SendAsync(chat => _d.Api.SendDocumentAsync(chat, _b.ThreadId, fileName, data, contentType, caption, replyTo, null, _cts.Token));

    private async Task EditAsync(int messageId, string html, IReadOnlyList<IReadOnlyList<TgButton>>? keyboard)
    {
        try { await SendAsync(chat => _d.Api.EditMessageTextAsync(chat, messageId, html, keyboard, _cts.Token)); }
        catch (TelegramApiException ex) when (ex.NotModified) { }
    }

    private async Task WithScopeAsync(Func<IServiceProvider, Task> work)
    {
        using var scope = _d.Scopes.CreateScope();
        await work(scope.ServiceProvider);
    }

    // ── Binding lifecycle ─────────────────────────────────────────────────────

    public Task ReloadAsync() => WithScopeAsync(ReloadAsync);

    private async Task ReloadAsync(IServiceProvider sp)
    {
        var fresh = await sp.GetRequiredService<ITelegramRepository>().GetBindingAsync(_b.Id);
        if (fresh is null || !fresh.IsActive)
        {
            _cts.Cancel();
            return;
        }
        var levelChanged = fresh.Level != _b.Level;
        _b = fresh;
        if (levelChanged)
        {
            RefreshStatus();
            // The suggestion buttons depend on the level too.
            SyncChatSoon();
        }
    }

    /// <summary>Ends the binding because Telegram will not deliver to it any more.</summary>
    private async Task EndAsync(IServiceProvider sp, string reason)
    {
        var repo = sp.GetRequiredService<ITelegramRepository>();
        if (await repo.GetBindingAsync(_b.Id) is { IsActive: true } binding)
        {
            binding.RevokedAt = DateTime.UtcNow;
            binding.RevokedReason = Clip(reason, 300);
            await repo.UpdateBindingAsync(binding);
        }
        _d.Logger.LogWarning("Telegram: binding {BindingId} ended — {Reason}", _b.Id, reason);
        _cts.Cancel();
    }

    private async Task Welcome(IServiceProvider sp)
    {
        var book = await sp.GetRequiredService<IBookRepository>().GetByIdAsync(_b.BookId);
        if (book is null) return;
        var text = $"✅ This {(_b.IsPrivate ? "chat" : _b.IsChannel ? "channel" : "thread")} is now connected to <b>{H(book.Title)}</b>.\n" +
                   $"Everyone here can {H(LevelText(_b.Level))}.\n\n" +
                   (_b.IsChannel
                       ? "Progress, chapter files and the finished book will be posted here."
                       : "Chapter files arrive after each chapter edit. Type /help for the commands.");
        await SendTextAsync(text);
        RefreshStatus();
    }

    // ── Status card ───────────────────────────────────────────────────────────

    public async Task<FormatStatus> BuildStatusAsync(IServiceProvider sp)
    {
        var repo = sp.GetRequiredService<IBookRepository>();
        var book = await repo.GetByIdAsync(_b.BookId) ?? throw new BindingGoneException();
        var chapters = (await repo.GetChaptersAsync(_b.BookId)).OrderBy(c => c.Number).ToList();
        var run = _d.RunState.GetStatus(_b.BookId);
        int? words = null;
        if (run is { IsActive: true } && _d.RunState.GetLiveStream(_b.BookId) is { } live && IsProse(live.AgentRole))
            words = CountWords(live.Content);
        var open = run?.State == RunStates.WaitingForInput
            ? (await repo.GetClarificationsAsync(_b.BookId)).Count(c => c.IsOpen)
            : 0;
        var view = new StatusView(book, chapters, run, _d.RunState.GetLastProgress(_b.BookId) ?? _lastStep,
            words, _prompt, _completion, open);
        return new FormatStatus(StatusCard(view), StatusKeyboard(_b, view));
    }

    public sealed record FormatStatus(string Html, IReadOnlyList<IReadOnlyList<TgButton>> Keyboard);

    private async Task UpdateStatusCardAsync(IServiceProvider sp)
    {
        var status = await BuildStatusAsync(sp);
        // Same text and buttons as the card shows already: nothing to edit.
        var hash = Hash(status.Html + JsonSerializer.Serialize(status.Keyboard));
        if (_b.StatusMessageId is not null && hash == _lastStatusHash) return;
        if (_b.StatusMessageId is int editId)
        {
            try
            {
                if (_statusPlain != true && !TelegramRich.Unsupported)
                {
                    try
                    {
                        await SendAsync(chat => _d.Api.EditRichMessageAsync(chat, editId, status.Html, status.Keyboard, _cts.Token));
                        _statusPlain = false;
                        _lastStatusHash = hash;
                        return;
                    }
                    // A card posted before rich messages stays a plain message: edit it as one from now on.
                    catch (TelegramApiException ex) when (ex.Code == 400 && !ex.NotModified && !ex.MessageMissing && !ex.ChatGone)
                    {
                        _statusPlain = true;
                    }
                }
                var (plain, inline) = TelegramRich.ToPlain(status.Html);
                await SendAsync(chat => _d.Api.EditMessageTextAsync(chat, editId, plain, [.. inline.Select(b => (IReadOnlyList<TgButton>)[b]), .. status.Keyboard], _cts.Token));
                _lastStatusHash = hash;
                return;
            }
            catch (TelegramApiException ex) when (ex.NotModified) { return; }
            catch (TelegramApiException ex) when (ex.MessageMissing) { /* deleted: post a new one */ }
        }
        await PostStatusCardAsync(sp, status);
    }

    /// <summary>Posts a fresh status card (the old one stays as it was) and pins it.</summary>
    public async Task PostStatusCardAsync(IServiceProvider sp, FormatStatus? status = null)
    {
        status ??= await BuildStatusAsync(sp);
        var sent = await SendAsync(chat => TelegramRich.SendAsync(_d.Api, chat, _b.ThreadId, status.Html, status.Keyboard, silent: true, ct: _cts.Token));
        _statusPlain = null;
        _lastStatusHash = Hash(status.Html + JsonSerializer.Serialize(status.Keyboard));
        var repo = sp.GetRequiredService<ITelegramRepository>();
        if (await repo.GetBindingAsync(_b.Id) is { } binding)
        {
            binding.StatusMessageId = sent.MessageId;
            await repo.UpdateBindingAsync(binding);
            _b.StatusMessageId = sent.MessageId;
        }
        // Pinning needs admin rights in groups; without them the card simply stays unpinned.
        try { await _d.Api.PinChatMessageAsync(_b.ChatId, sent.MessageId, _cts.Token); }
        catch (TelegramApiException) { }
    }

    // ── Questions ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Planner clarifications, one at a time: the next open one is asked once the one asked before is answered
    /// (here or on the Clarifications page). Asked only while a planning run waits for them.
    /// </summary>
    private async Task AskClarificationAsync(IServiceProvider sp)
    {
        if (_d.RunState.GetStatus(_b.BookId) is not { State: RunStates.WaitingForInput, Role: AgentRole.Planner }) return;
        if (_b.IsChannel) return;

        var repo = sp.GetRequiredService<IBookRepository>();
        var tg = sp.GetRequiredService<ITelegramRepository>();
        var open = (await repo.GetClarificationsAsync(_b.BookId)).Where(c => c.IsOpen).OrderBy(c => c.Id).ToList();
        if (open.Count == 0) return;

        var asked = (await tg.GetLinksAsync(_b.Id, TelegramLinkKind.Clarification)).Select(l => l.RefId).ToHashSet();
        var again = _askAgain;
        _askAgain = false;
        var waiting = open.FirstOrDefault(c => asked.Contains(c.Id));
        if (waiting is not null && !again) return;   // still waiting for the one asked
        var next = waiting ?? open[0];

        var canAnswer = _b.Level >= TelegramAccessLevel.Participate;
        var text = $"❓ <b>Planner question</b> · {open.Count} open\n\n{H(next.Question)}" +
                   (canAnswer ? "\n\n<i>Reply to this message to answer it.</i>" : "");
        var keyboard = canAnswer
            ? new List<IReadOnlyList<TgButton>> { new[] { new TgButton("⏭ Skip", Callback(_b.Id, "skipc", next.Id)) } }
            : null;
        var sent = await SendTextAsync(text, keyboard);
        await tg.AddLinkAsync(new TelegramMessageLink
        {
            BindingId = _b.Id, Kind = TelegramLinkKind.Clarification, RefId = next.Id, MessageId = sent.MessageId,
        });
        RefreshStatus();
    }

    private async Task SendQuestionAsync(IServiceProvider sp, int messageId)
    {
        if (_b.IsChannel) return;
        var tg = sp.GetRequiredService<ITelegramRepository>();
        if (await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.Question, messageId) is not null) return;
        var message = await sp.GetRequiredService<IBookRepository>().FindMessageByIdAsync(messageId);
        if (message is not { MessageType: MessageType.Question, IsResolved: false }) return;

        var canAnswer = _b.Level >= TelegramAccessLevel.Participate;
        var text = $"❓ <b>{H(RoleName(message.AgentRole))} asks</b>\n\n{H(Clip(message.Content, 3500))}" +
                   (canAnswer ? "\n\n<i>Reply to this message to answer it.</i>" : "");
        var sent = await SendTextAsync(text);
        await tg.AddLinkAsync(new TelegramMessageLink
        {
            BindingId = _b.Id, Kind = TelegramLinkKind.Question, RefId = messageId, MessageId = sent.MessageId,
        });
    }

    private async Task SendErrorAsync(TgErrorEvent e)
    {
        var keyboard = new List<IReadOnlyList<TgButton>>();
        if (_b.Level == TelegramAccessLevel.Control)
            keyboard.Add([new TgButton("⏩ Continue", Callback(_b.Id, "cont"), Style: "success")]);
        await SendTextAsync($"❌ <b>{H(RoleName(e.Role))}</b>: {H(Clip(e.Message, 1500))}", keyboard.Count > 0 ? keyboard : null);
        RefreshStatus();
    }

    // ── Files ─────────────────────────────────────────────────────────────────

    /// <summary>Sends each Done chapter whose text changed since its last file, as a reply to that file.</summary>
    private async Task SyncChaptersAsync(IServiceProvider sp)
    {
        var repo = sp.GetRequiredService<IBookRepository>();
        var tg = sp.GetRequiredService<ITelegramRepository>();
        var exporter = sp.GetRequiredService<BookExportService>();
        var chapters = (await repo.GetChaptersAsync(_b.BookId)).OrderBy(c => c.Number).ToList();
        _sentChapterHashes ??= (await tg.GetLinksAsync(_b.Id, TelegramLinkKind.ChapterFile))
            .GroupBy(l => l.RefId).ToDictionary(g => g.Key, g => g.Last().ContentHash);

        foreach (var chapter in chapters.Where(c => c.Status == ChapterStatus.Done && !string.IsNullOrWhiteSpace(c.Content)))
        {
            var hash = ChapterHash(chapter);
            if (_sentChapterHashes.GetValueOrDefault(chapter.Id) == hash) continue;
            var last = await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.ChapterFile, chapter.Id);
            if (last?.ContentHash == hash)
            {
                _sentChapterHashes[chapter.Id] = hash;
                continue;
            }

            var version = (last?.Version ?? 0) + 1;
            var (bytes, contentType, fileName) = await exporter.ExportChapterAsync(_b.BookId, chapter.Id, _b.FileFormat);
            var caption = $"📄 <b>Chapter {chapter.Number}{(chapter.Title.Length > 0 ? ": " + H(Clip(chapter.Title, 150)) : "")}</b>\n" +
                          $"v{version}{(_rewound ? " · ⏪ rewound" : "")} · {N(CountWords(chapter.Content))} words" +
                          (string.IsNullOrWhiteSpace(chapter.Summary) ? "" : $"\n\n{H(Clip(chapter.Summary, 650))}");
            var replyTo = last is { MessageId: > 0 } ? last.MessageId : (int?)null;
            var messageId = await SendFileOrNoticeAsync(fileName, bytes, contentType, caption, replyTo);
            await tg.AddLinkAsync(new TelegramMessageLink
            {
                BindingId = _b.Id, Kind = TelegramLinkKind.ChapterFile, RefId = chapter.Id,
                MessageId = messageId, ContentHash = hash, Version = version,
            });
            _sentChapterHashes[chapter.Id] = hash;
        }

        _rewound = false;
        if (_planningRunSeen) await SendPlanningIfCompleteAsync(sp, chapters);
        if (_bookCheck)
        {
            _bookCheck = false;
            await SendBookIfCompleteAsync(sp, chapters);
        }
    }

    private async Task<int> SendFileOrNoticeAsync(string fileName, byte[] bytes, string contentType, string caption, int? replyTo)
    {
        var max = _d.Options.MaxUploadBytes;
        if (bytes.Length > max)
        {
            var notice = await SendTextAsync(caption +
                $"\n\n⚠️ The file is {bytes.Length / (1024 * 1024)} MB — over the {max / (1024 * 1024)} MB a bot may upload. Download it from abook.",
                replyTo: replyTo);
            return notice.MessageId;
        }
        var sent = await SendDocumentAsync(fileName, bytes, contentType, caption, replyTo);
        return sent.MessageId;
    }

    public static string ChapterHash(Chapter c) => Hash(c.Title + "\n" + c.Content);

    public static async Task<string> PlanningHashAsync(IBookRepository repo, int bookId, IEnumerable<Chapter> chapters)
    {
        var bible = await repo.GetStoryBibleAsync(bookId);
        var characters = (await repo.GetCharacterCardsAsync(bookId)).Select(c => c.Name).OrderBy(n => n);
        var threads = (await repo.GetPlotThreadsAsync(bookId)).Select(t => t.Name).OrderBy(n => n);
        return Hash(string.Join("\n", [
            bible?.UpdatedAt.Ticks.ToString() ?? "-",
            string.Join("|", characters), string.Join("|", threads),
            .. chapters.OrderBy(c => c.Number).Select(c => $"{c.Number}:{c.Title}:{c.Outline}")]));
    }

    public static string BookHash(IEnumerable<Chapter> chapters) =>
        Hash(string.Join(",", chapters.OrderBy(c => c.Number).Select(ChapterHash)));

    public static bool IsPlanningComplete(Book book) =>
        book.StoryBibleStatus == PlanningPhaseStatus.Complete && book.CharactersStatus == PlanningPhaseStatus.Complete
        && book.PlotThreadsStatus == PlanningPhaseStatus.Complete && book.ChaptersStatus == PlanningPhaseStatus.Complete;

    public static bool IsBookComplete(Book book, IReadOnlyCollection<Chapter> chapters) =>
        book.Mode == BookMode.Autonomous && chapters.Count > 0 && chapters.All(c => c.Status == ChapterStatus.Done)
        && chapters.Count >= book.TargetChapterCount;

    /// <summary>After a planning run: the planning export (Story Bible, characters, plot threads, outlines), once per plan.</summary>
    private async Task SendPlanningIfCompleteAsync(IServiceProvider sp, IReadOnlyList<Chapter> chapters)
    {
        var repo = sp.GetRequiredService<IBookRepository>();
        var book = await repo.GetByIdAsync(_b.BookId);
        if (book is null || !IsPlanningComplete(book) || chapters.Count == 0) return;
        _planningRunSeen = false;

        var tg = sp.GetRequiredService<ITelegramRepository>();
        var hash = await PlanningHashAsync(repo, _b.BookId, chapters);
        if ((await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.PlanningFile, 0))?.ContentHash == hash) return;

        var (bytes, contentType, fileName) = await sp.GetRequiredService<BookExportService>().ExportAsync(_b.BookId, "metadata");
        var characters = (await repo.GetCharacterCardsAsync(_b.BookId)).Count();
        var threads = (await repo.GetPlotThreadsAsync(_b.BookId)).Count();
        var caption = $"🗺 <b>Planning complete</b>\n{chapters.Count} chapters · {characters} characters · {threads} plot threads\n\n" +
                      "<i>Story Bible, characters, plot threads and chapter outlines.</i>";
        var messageId = await SendFileOrNoticeAsync(fileName, bytes, contentType, caption, null);
        await tg.AddLinkAsync(new TelegramMessageLink
        {
            BindingId = _b.Id, Kind = TelegramLinkKind.PlanningFile, RefId = 0, MessageId = messageId, ContentHash = hash,
        });
    }

    /// <summary>When the last chapter is done: the whole book in the binding's format.</summary>
    private async Task SendBookIfCompleteAsync(IServiceProvider sp, IReadOnlyList<Chapter> chapters)
    {
        var book = await sp.GetRequiredService<IBookRepository>().GetByIdAsync(_b.BookId);
        if (book is null || !IsBookComplete(book, chapters)) return;

        var tg = sp.GetRequiredService<ITelegramRepository>();
        var hash = BookHash(chapters);
        if ((await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.BookFile, 0))?.ContentHash == hash) return;

        var (bytes, contentType, fileName) = await sp.GetRequiredService<BookExportService>().ExportAsync(_b.BookId, _b.FileFormat);
        var words = chapters.Sum(c => CountWords(c.Content));
        var caption = $"📚 <b>{H(Clip(book.Title, 300))}</b> is complete\n{chapters.Count} chapters · {N(words)} words";
        var messageId = await SendFileOrNoticeAsync(fileName, bytes, contentType, caption, null);
        await tg.AddLinkAsync(new TelegramMessageLink
        {
            BindingId = _b.Id, Kind = TelegramLinkKind.BookFile, RefId = 0, MessageId = messageId, ContentHash = hash,
        });
    }

    /// <summary>/export and the Export button: the book as it stands, in the binding's format (or the one asked for).</summary>
    public async Task SendExportAsync(IServiceProvider sp, string? format, string requestedBy)
    {
        var fmt = format is "epub" or "fb2" or "html" or "metadata" ? format : _b.FileFormat;
        var repo = sp.GetRequiredService<IBookRepository>();
        var book = await repo.GetByIdAsync(_b.BookId);
        if (book is null) return;
        var chapters = (await repo.GetChaptersAsync(_b.BookId)).Where(c => !string.IsNullOrWhiteSpace(c.Content)).ToList();
        if (fmt != "metadata" && chapters.Count == 0)
        {
            await SendTextAsync("📦 Nothing to export yet — no chapter has been written.");
            return;
        }
        var (bytes, contentType, fileName) = await sp.GetRequiredService<BookExportService>().ExportAsync(_b.BookId, fmt);
        var caption = fmt == "metadata"
            ? $"🗺 <b>{H(Clip(book.Title, 300))}</b> — planning\n<i>Requested by {H(requestedBy)}</i>"
            : $"📦 <b>{H(Clip(book.Title, 300))}</b> — {chapters.Count} chapter(s) so far\n<i>Requested by {H(requestedBy)}</i>";
        await SendFileOrNoticeAsync(fileName, bytes, contentType, caption, null);
    }

    // ── Illustrations ─────────────────────────────────────────────────────────

    private async Task SyncIllustrationsAsync(IServiceProvider sp)
    {
        var repo = sp.GetRequiredService<IBookRepository>();
        var tg = sp.GetRequiredService<ITelegramRepository>();
        var illustrations = (await repo.GetIllustrationsAsync(_b.BookId))
            .Where(i => i.Status == IllustrationStatus.Rendered && i.ImageId.HasValue).ToList();
        if (illustrations.Count == 0) return;
        var chapters = (await repo.GetChaptersAsync(_b.BookId)).ToDictionary(c => c.Id);

        foreach (var ill in illustrations.OrderBy(i => i.Id))
        {
            var hash = ill.ImageId!.Value.ToString();
            if ((await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.Illustration, ill.Id))?.ContentHash == hash) continue;
            var image = await repo.GetBookImageAsync(_b.BookId, ill.ImageId.Value);
            if (image is null) continue;

            var (data, contentType) = ExportImageCompressor.Compress(image.Data, image.ContentType);
            var where = ill.Kind switch
            {
                IllustrationKind.Cover => "Cover",
                IllustrationKind.Portrait => "Portrait",
                _ => ill.ChapterId is int id && chapters.TryGetValue(id, out var ch) ? $"Chapter {ch.Number}" : "Scene",
            };
            var caption = $"🎨 <b>{H(where)}</b>" + (ill.Caption.Length > 0 ? $" — {H(Clip(ill.Caption, 800))}" : "");
            var ext = contentType == "image/jpeg" ? "jpg" : contentType == "image/webp" ? "webp" : "png";
            var sent = await SendAsync(chat => _d.Api.SendPhotoAsync(chat, _b.ThreadId, $"illustration-{ill.Id}.{ext}", data, contentType,
                caption, _cts.Token));
            await tg.AddLinkAsync(new TelegramMessageLink
            {
                BindingId = _b.Id, Kind = TelegramLinkKind.Illustration, RefId = ill.Id, MessageId = sent.MessageId, ContentHash = hash,
            });
        }
    }

    // ── Book chat (Interactive books) ─────────────────────────────────────────

    /// <summary>
    /// Mirrors the book chat: replies (with their suggested changes as buttons), events, messages typed in the web
    /// UI, and failed replies (with Retry). Telegram's own messages are already in the thread.
    /// </summary>
    private async Task SyncChatAsync(IServiceProvider sp)
    {
        if (_b.IsChannel) return;
        var repo = sp.GetRequiredService<IBookRepository>();
        var book = await repo.GetByIdAsync(_b.BookId);
        if (book is not { Mode: BookMode.Interactive }) return;
        var tg = sp.GetRequiredService<ITelegramRepository>();

        foreach (var m in await repo.GetChatMessagesAsync(_b.BookId, null, 30))
        {
            var link = await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.ChatMessage, m.Id);
            switch (m.Role)
            {
                case BookChatRole.Author:
                    await SyncAuthorMessageAsync(tg, m, link);
                    break;
                case BookChatRole.Assistant when m.Status == BookChatMessageStatus.Done:
                    if (link is null) link = await SendAssistantMessageAsync(tg, m);
                    await SyncProposalsAsync(tg, m);
                    break;
                case BookChatRole.Event when link is null:
                    var sent = await SendTextAsync($"ℹ️ <i>{H(Clip(m.Content, 3500))}</i>", silent: true);
                    await tg.AddLinkAsync(new TelegramMessageLink
                    {
                        BindingId = _b.Id, Kind = TelegramLinkKind.ChatMessage, RefId = m.Id, MessageId = sent.MessageId,
                    });
                    break;
            }
        }
    }

    private async Task SyncAuthorMessageAsync(ITelegramRepository tg, BookChatMessage m, TelegramMessageLink? link)
    {
        var status = m.Status.ToString();
        if (link is null)
        {
            var messageId = 0;
            // Typed in the web UI (or over MCP): show it so the thread reads as the whole conversation.
            if (m.Origin is null || !m.Origin.StartsWith("Telegram", StringComparison.Ordinal))
                messageId = (await SendTextAsync($"✍️ <b>{H(m.Origin ?? "Author (web)")}</b>\n{H(Clip(m.Content, 3500))}", silent: true)).MessageId;
            link = await tg.AddLinkAsync(new TelegramMessageLink
            {
                BindingId = _b.Id, Kind = TelegramLinkKind.ChatMessage, RefId = m.Id, MessageId = messageId,
                ContentHash = m.Status == BookChatMessageStatus.Failed ? null : status,
            });
        }
        if (link.ContentHash == status) return;
        if (m.Status == BookChatMessageStatus.Failed)
        {
            var keyboard = _b.Level >= TelegramAccessLevel.Participate
                ? new List<IReadOnlyList<TgButton>> { new[] { new TgButton("🔁 Retry", Callback(_b.Id, "retry", m.Id)) } }
                : null;
            await SendTextAsync($"⚠️ The reply to “{H(Clip(m.Content, 120))}” failed or was stopped.", keyboard,
                replyTo: link.MessageId > 0 ? link.MessageId : null);
        }
        link.ContentHash = status;
        await tg.UpdateLinkAsync(link);
    }

    private async Task<TelegramMessageLink> SendAssistantMessageAsync(ITelegramRepository tg, BookChatMessage m)
    {
        var text = m.Content.Trim();
        var conflicts = ReadConflicts(m.ConflictsJson);
        if (conflicts.Count > 0)
            text += "\n\n⚠️ Conflicts with the book:\n" + string.Join("\n", conflicts.Select(c => "• " + c));
        var first = 0;
        foreach (var chunk in SplitForMessages(text.Length > 0 ? text : "…"))
        {
            var sent = await SendTextAsync("🤖 " + H(chunk));
            if (first == 0) first = sent.MessageId;
        }
        return await tg.AddLinkAsync(new TelegramMessageLink
        {
            BindingId = _b.Id, Kind = TelegramLinkKind.ChatMessage, RefId = m.Id, MessageId = first,
        });
    }

    private static List<string> ReadConflicts(string json)
    {
        try
        {
            return (JsonSerializer.Deserialize<List<ChatConflict>>(json,
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? [])
                .Where(c => !string.IsNullOrWhiteSpace(c.Description))
                .Select(c => c.Chapter is int n ? $"{c.Description} (chapter {n})" : c.Description)
                .ToList();
        }
        catch (JsonException) { return []; }
    }

    /// <summary>Re-draws one reply's suggestions message now (after a tick, or an apply from a button or a reply).</summary>
    public Task RedrawProposalsAsync(int chatMessageId) => WithScopeAsync(async sp =>
    {
        var m = await sp.GetRequiredService<IBookRepository>().GetChatMessageAsync(_b.BookId, chatMessageId);
        if (m is { HiddenAt: null }) await SyncProposalsAsync(sp.GetRequiredService<ITelegramRepository>(), m);
    });

    /// <summary>The reply's suggested changes as one message with buttons, edited as their states change.</summary>
    private async Task SyncProposalsAsync(ITelegramRepository tg, BookChatMessage m)
    {
        var proposals = m.Proposals.OrderBy(p => p.Ordinal).ToList();
        if (proposals.Count == 0) return;
        // Applying is part of using the book chat: everyone who can write in it can apply what it suggests.
        var canAct = _b.Level >= TelegramAccessLevel.Participate;
        // Ticks of suggestions no longer open are dropped.
        var ticked = _d.Ticks(_b.Id, m.Id).Where(id => proposals.Any(p => p.Id == id && IsOpen(p))).ToHashSet();
        _d.SetTicks(_b.Id, m.Id, ticked);
        // Whether the chat may act, and what is ticked, are part of what is shown: either changing re-draws the buttons.
        var hash = ProposalsHash(proposals) + (canAct ? "+act" : "") + (ticked.Count > 0 ? "+t" + string.Join(".", ticked.Order()) : "");
        var link = await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.ChatProposals, m.Id);
        if (link?.ContentHash == hash) return;

        var footer = !canAct && proposals.Any(IsOpen)
            ? "This chat can only follow the book — apply these in abook, or ask the book's owner for Participate access."
            : canAct && proposals.Count(IsOpen) >= 2
                ? "Tick several and apply them together — or reply with their numbers (“1 3”, “reject 2”)."
                : null;
        var keyboard = canAct ? ProposalsKeyboard(_b.Id, m.Id, proposals, ticked) : [];
        var rich = ProposalsRich(_b.Id, proposals, footer is null ? null : H(footer));
        if (link is null)
        {
            var reply = await tg.GetLatestLinkAsync(_b.Id, TelegramLinkKind.ChatMessage, m.Id);
            var sent = await SendAsync(chat => TelegramRich.SendAsync(_d.Api, chat, _b.ThreadId, rich, keyboard.Count > 0 ? keyboard : null,
                reply is { MessageId: > 0 } ? reply.MessageId : null, ct: _cts.Token));
            await tg.AddLinkAsync(new TelegramMessageLink
            {
                BindingId = _b.Id, Kind = TelegramLinkKind.ChatProposals, RefId = m.Id, MessageId = sent.MessageId, ContentHash = hash,
                Version = RichLinkVersion,
            });
            return;
        }
        if (link.MessageId > 0)
        {
            if (link.Version == RichLinkVersion)
                await SendAsync(chat => Done(TelegramRich.EditAsync(_d.Api, chat, link.MessageId, rich, keyboard, _cts.Token)));
            else
            {
                // A plain message from before rich messages: it stays plain (with /details commands).
                var text = ProposalsText(proposals) + (footer is null ? "" : $"\n\n<i>{H(footer)}</i>");
                await EditAsync(link.MessageId, text, keyboard);
            }
        }
        link.ContentHash = hash;
        await tg.UpdateLinkAsync(link);
    }

    // ── Live drafts ───────────────────────────────────────────────────────────

    private static bool IsProse(string role) => role is nameof(AgentRole.Writer) or nameof(AgentRole.Editor);

    /// <summary>
    /// Private chats only: the end of the chapter being written, as a draft that Telegram shows for ~30 s and
    /// replaces as it grows. Control can stop the run from the draft's stop button.
    /// </summary>
    private async Task SendDraftAsync(AgentRunStatus? run, long streamTicks)
    {
        _lastStreamSeen = streamTicks;
        if (!_b.IsPrivate || !_b.LiveDrafts || _draftStopped || run is not { IsActive: true }) return;
        if (_d.RunState.GetLiveStream(_b.BookId) is not { } live || !IsProse(live.AgentRole) || live.Content.Length == 0) return;

        _lastDraft = DateTime.UtcNow;
        var body = live.Content.Length > MaxDraftTail ? "…" + live.Content[^MaxDraftTail..] : live.Content;
        var header = $"✍️ {live.AgentRole} · ~{N(CountWords(live.Content))} words\n\n";
        var draftId = DraftId(_b.BookId, live.ChapterId);
        try
        {
            await _d.Api.SendMessageDraftAsync(_b.ChatId, _b.ThreadId, draftId, header + body,
                canStop: _b.Level == TelegramAccessLevel.Control, _cts.Token);
        }
        catch (TelegramApiException ex)
        {
            // Not offered for this chat (or rejected): no more drafts for this run.
            _d.Logger.LogInformation("Telegram: drafts off for binding {BindingId} — {Description}", _b.Id, ex.Description);
            _draftStopped = true;
        }
    }

    private static long DraftId(int bookId, int? chapterId) =>
        ((bookId * 1_000_003L) ^ (chapterId ?? 0)) & 0x7FFF_FFFF | 1;
}

/// <summary>The bound chat or topic is gone; the worker stops.</summary>
public sealed class BindingGoneException : Exception;
