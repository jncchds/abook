using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Agents;

/// <summary>
/// Manages agent run lifecycle. Delegates to individual agents and
/// tracks per-book run state in memory (survives within the app session).
/// </summary>
public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IBookRepository _repo;
    private readonly PlannerAgent _planner;
    private readonly WriterAgent _writer;
    private readonly EditorAgent _editor;
    private readonly ContinuityCheckerAgent _continuity;

    // bookId -> current run state, for status queries
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, AgentRunStatus> _status = new();

    // bookId -> pending question answer (TaskCompletionSource keyed by messageId)
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int MessageId, TaskCompletionSource<string> Tcs)> _pending = new();

    public AgentOrchestrator(
        IBookRepository repo,
        PlannerAgent planner,
        WriterAgent writer,
        EditorAgent editor,
        ContinuityCheckerAgent continuity)
    {
        _repo = repo;
        _planner = planner;
        _writer = writer;
        _editor = editor;
        _continuity = continuity;
    }

    public async Task StartPlanningAsync(int bookId, CancellationToken ct = default)
    {
        _status[bookId] = new AgentRunStatus(AgentRole.Planner, "Running", null);
        try
        {
            await _planner.PlanAsync(bookId, ct);
            _status[bookId] = new AgentRunStatus(AgentRole.Planner, "Done", null);
        }
        catch
        {
            _status[bookId] = new AgentRunStatus(AgentRole.Planner, "Failed", null);
            throw;
        }
    }

    public async Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        _status[bookId] = new AgentRunStatus(AgentRole.Writer, "Running", chapterId);
        try
        {
            await _writer.WriteAsync(bookId, chapterId, ct);
            _status[bookId] = new AgentRunStatus(AgentRole.Writer, "Done", chapterId);
        }
        catch
        {
            _status[bookId] = new AgentRunStatus(AgentRole.Writer, "Failed", chapterId);
            throw;
        }
    }

    public async Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        _status[bookId] = new AgentRunStatus(AgentRole.Editor, "Running", chapterId);
        try
        {
            await _editor.EditAsync(bookId, chapterId, ct);
            _status[bookId] = new AgentRunStatus(AgentRole.Editor, "Done", chapterId);
        }
        catch
        {
            _status[bookId] = new AgentRunStatus(AgentRole.Editor, "Failed", chapterId);
            throw;
        }
    }

    public async Task StartContinuityCheckAsync(int bookId, CancellationToken ct = default)
    {
        _status[bookId] = new AgentRunStatus(AgentRole.ContinuityChecker, "Running", null);
        try
        {
            await _continuity.CheckAsync(bookId, ct);
            _status[bookId] = new AgentRunStatus(AgentRole.ContinuityChecker, "Done", null);
        }
        catch
        {
            _status[bookId] = new AgentRunStatus(AgentRole.ContinuityChecker, "Failed", null);
            throw;
        }
    }

    public async Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default)
    {
        var message = (await _repo.GetMessagesAsync(0))  // Loaded below per-message
            .FirstOrDefault();

        // Find the message directly by ID via a helper query ─ we look through all pending
        foreach (var kv in _pending)
        {
            if (kv.Value.MessageId == messageId)
            {
                var msg = new AgentMessage { Id = messageId };
                msg.Content = answer;
                msg.IsResolved = true;
                // Resolve the pending TCS
                kv.Value.Tcs.TrySetResult(answer);

                // Persist the answer message
                // (bookId retrieved from _status)
                if (_status.TryGetValue(kv.Key, out var run))
                {
                    await _repo.AddMessageAsync(new AgentMessage
                    {
                        BookId = kv.Key,
                        ChapterId = run.ChapterId,
                        AgentRole = run.Role,
                        MessageType = MessageType.Answer,
                        Content = answer,
                        IsResolved = true
                    });
                }
                return;
            }
        }
    }

    public Task<AgentRunStatus?> GetRunStatusAsync(int bookId)
    {
        _status.TryGetValue(bookId, out var status);
        return Task.FromResult(status);
    }
}
