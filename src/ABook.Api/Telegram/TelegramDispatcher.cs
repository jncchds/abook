using System.Collections.Concurrent;
using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Api.Telegram;

/// <summary>
/// Sends everything the bot posts on its own: the status card, questions, files, illustrations, book-chat replies
/// and live drafts. Reads book events from <see cref="TelegramEventSink"/> and hands them to one
/// <see cref="BindingWorker"/> per active binding, which paces its sends to Telegram's per-chat limits — so a slow
/// chat only delays itself, and nothing here can slow an agent run.
/// </summary>
public sealed class TelegramDispatcher(
    TelegramOptions options,
    TelegramEventSink sink,
    TelegramBotState bot,
    TelegramApi api,
    IServiceScopeFactory scopes,
    AgentRunStateService runState,
    ILogger<TelegramDispatcher> logger) : BackgroundService
{
    private readonly ConcurrentDictionary<int, BindingWorker> _workers = new();
    private CancellationToken _stopping;

    internal TelegramApi Api => api;
    internal TelegramOptions Options => options;
    internal IServiceScopeFactory Scopes => scopes;
    internal AgentRunStateService RunState => runState;
    internal TelegramEventSink Sink => sink;
    internal ILogger Logger => logger;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        _stopping = stoppingToken;
        try
        {
            await bot.WaitReadyAsync(stoppingToken);

            using (var scope = scopes.CreateScope())
            {
                var repo = scope.ServiceProvider.GetRequiredService<ITelegramRepository>();
                var bookIds = await repo.GetBoundBookIdsAsync();
                sink.SetBoundBooks(bookIds);
                foreach (var bookId in bookIds)
                    foreach (var binding in await repo.GetBindingsForBookAsync(bookId))
                        StartWorker(binding, isNew: false);
            }
            logger.LogInformation("Telegram: {Count} bound chat(s) loaded.", _workers.Count);

            await foreach (var ev in sink.Reader.ReadAllAsync(stoppingToken))
                foreach (var worker in _workers.Values)
                    if (worker.BookId == ev.BookId) worker.Post(ev);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    /// <summary>A binding was just made: start its worker, which greets the chat and pins the status card.</summary>
    public void Attach(TelegramBinding binding)
    {
        sink.SetBound(binding.BookId, true);
        StartWorker(binding, isNew: true);
    }

    /// <summary>The binding's settings changed (or it was revoked): the worker re-reads it.</summary>
    public void Reload(int bindingId)
    {
        if (_workers.TryGetValue(bindingId, out var worker)) worker.Enqueue(w => w.ReloadAsync());
    }

    /// <summary>Stops the binding's worker; the book stops being queued when it has no binding left.</summary>
    public void Detach(int bindingId)
    {
        if (!_workers.TryRemove(bindingId, out var worker)) return;
        worker.Stop();
        if (!_workers.Values.Any(w => w.BookId == worker.BookId)) sink.SetBound(worker.BookId, false);
    }

    /// <summary>Runs <paramref name="job"/> on the binding's worker (in order with its other sends).</summary>
    public bool Enqueue(int bindingId, Func<BindingWorker, Task> job)
    {
        if (!_workers.TryGetValue(bindingId, out var worker)) return false;
        worker.Enqueue(job);
        return true;
    }

    public BindingWorker? Worker(int bindingId) => _workers.GetValueOrDefault(bindingId);

    private void StartWorker(TelegramBinding binding, bool isNew)
    {
        var worker = new BindingWorker(this, binding, isNew);
        if (_workers.TryAdd(binding.Id, worker))
            worker.Start(_stopping);
    }

    internal void OnWorkerEnded(BindingWorker worker)
    {
        if (_workers.TryGetValue(worker.BindingId, out var current) && ReferenceEquals(current, worker))
            Detach(worker.BindingId);
    }
}
