using ABook.Core.Interfaces;
using ABook.Core.Models;

namespace ABook.Agents;

/// <summary>
/// Manages agent run lifecycle. Delegates to individual agents and
/// tracks per-book run state via the singleton AgentRunStateService.
/// </summary>
public class AgentOrchestrator : IAgentOrchestrator
{
    private readonly IBookRepository _repo;
    private readonly PlannerAgent _planner;
    private readonly WriterAgent _writer;
    private readonly EditorAgent _editor;
    private readonly ContinuityCheckerAgent _continuity;
    private readonly AgentRunStateService _state;

    public AgentOrchestrator(
        IBookRepository repo,
        PlannerAgent planner,
        WriterAgent writer,
        EditorAgent editor,
        ContinuityCheckerAgent continuity,
        AgentRunStateService state)
    {
        _repo = repo;
        _planner = planner;
        _writer = writer;
        _editor = editor;
        _continuity = continuity;
        _state = state;
    }

    public async Task StartPlanningAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Planner, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _planner.PlanAsync(bookId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Done", null));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Planner, "Failed", null));
            throw;
        }
    }

    public async Task StartWritingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Writer, chapterId))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _writer.WriteAsync(bookId, chapterId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Done", chapterId));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Writer, "Failed", chapterId));
            throw;
        }
    }

    public async Task StartEditingAsync(int bookId, int chapterId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.Editor, chapterId))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _editor.EditAsync(bookId, chapterId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Done", chapterId));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.Editor, "Failed", chapterId));
            throw;
        }
    }

    public async Task StartContinuityCheckAsync(int bookId, CancellationToken ct = default)
    {
        if (!_state.TryStartRun(bookId, AgentRole.ContinuityChecker, null))
            throw new InvalidOperationException("An agent is already running for this book.");
        try
        {
            await _continuity.CheckAsync(bookId, ct);
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Done", null));
        }
        catch
        {
            _state.SetStatus(bookId, new AgentRunStatus(AgentRole.ContinuityChecker, "Failed", null));
            throw;
        }
    }

    public async Task ResumeWithAnswerAsync(int messageId, string answer, CancellationToken ct = default)
    {
        // Find the message to get bookId
        var messages = await _repo.FindMessageByIdAsync(messageId);
        if (messages is null) return;

        // Mark message resolved in DB
        messages.IsResolved = true;
        await _repo.UpdateMessageAsync(messages);

        // Persist answer
        await _repo.AddMessageAsync(new AgentMessage
        {
            BookId = messages.BookId,
            ChapterId = messages.ChapterId,
            AgentRole = messages.AgentRole,
            MessageType = MessageType.Answer,
            Content = answer,
            IsResolved = true
        });

        // Unblock the waiting agent
        _state.TryResolvePending(messages.BookId, messageId, answer);
    }

    public Task<AgentRunStatus?> GetRunStatusAsync(int bookId) =>
        Task.FromResult(_state.GetStatus(bookId));
}
