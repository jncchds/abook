using ABook.Core.Interfaces;
using ABook.Core.Models;
using System.Collections.Concurrent;

namespace ABook.Agents;

/// <summary>
/// Singleton service that tracks per-book agent run state across HTTP requests.
/// </summary>
public class AgentRunStateService
{
    private readonly ConcurrentDictionary<int, AgentRunStatus> _status = new();

    // bookId -> (messageId, TaskCompletionSource for answer)
    private readonly ConcurrentDictionary<int, (int MessageId, TaskCompletionSource<string> Tcs)> _pending = new();

    public AgentRunStatus? GetStatus(int bookId) =>
        _status.TryGetValue(bookId, out var s) ? s : null;

    public void SetStatus(int bookId, AgentRunStatus status) =>
        _status[bookId] = status;

    public bool TryStartRun(int bookId, AgentRole role, int? chapterId)
    {
        var current = _status.GetValueOrDefault(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            return false;
        _status[bookId] = new AgentRunStatus(role, "Running", chapterId);
        return true;
    }

    public void SetPending(int bookId, int messageId, TaskCompletionSource<string> tcs) =>
        _pending[bookId] = (messageId, tcs);

    public bool TryResolvePending(int bookId, int messageId, string answer)
    {
        if (_pending.TryGetValue(bookId, out var pending) && pending.MessageId == messageId)
        {
            _pending.TryRemove(bookId, out _);
            pending.Tcs.TrySetResult(answer);
            return true;
        }
        return false;
    }

    public bool HasPending(int bookId) => _pending.ContainsKey(bookId);
}
