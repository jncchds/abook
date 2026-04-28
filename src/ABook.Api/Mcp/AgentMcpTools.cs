using ABook.Agents;
using ABook.Core.Interfaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for triggering and controlling AI agent workflows.</summary>
public class AgentMcpTools
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunStateService _runState;
    private readonly IBookRepository _repo;
    private readonly IHttpContextAccessor _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public AgentMcpTools(IServiceScopeFactory scopeFactory, AgentRunStateService runState, IBookRepository repo, IHttpContextAccessor http)
    {
        _scopeFactory = scopeFactory;
        _runState = runState;
        _repo = repo;
        _http = http;
    }

    private int CurrentUserId() =>
        int.Parse(_http.HttpContext!.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task EnsureBookOwnershipAsync(int bookId)
    {
        var userId = CurrentUserId();
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null || book.UserId != userId)
            throw new McpException($"Book {bookId} not found.");
    }

    private void EnsureNotRunning(int bookId)
    {
        var current = _runState.GetStatus(bookId);
        if (current is { State: "Running" or "WaitingForInput" })
            throw new McpException("An agent run is already in progress for this book. Stop it first or wait for it to complete.");
    }

    private async Task RunInBackground(int bookId, Func<IAgentOrchestrator, CancellationToken, Task> action, CancellationToken ct = default)
    {
        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        try { await action(orchestrator, ct); }
        catch (OperationCanceledException) { /* stopped by user */ }
        catch (Exception ex)
        {
            var logger = scope.ServiceProvider.GetRequiredService<ILogger<AgentMcpTools>>();
            logger.LogError(ex, "Agent error for book {BookId}", bookId);
        }
    }

    // ── Planning ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "start_planning")]
    [Description("Start the planning pipeline for a book. Runs four sequential phases: Story Bible → Characters → Plot Threads → Chapter Outlines. Each phase may ask clarifying questions — check get_agent_status and use answer_agent_question if the agent is WaitingForInput. The run is asynchronous; returns immediately after scheduling.")]
    public async Task<string> StartPlanning(
        [Description("The book ID to plan.")] int bookId)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        var ct = _runState.CreateRunCts(bookId);
        _ = RunInBackground(bookId, (o, c) => o.StartPlanningAsync(bookId, c), ct);
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "planning" }, _json);
    }

    [McpServerTool(Name = "continue_planning")]
    [Description("Resume a partially-completed planning pipeline. Skips phases already marked Complete and continues with the remaining phases. Use this when planning was interrupted.")]
    public async Task<string> ContinuePlanning(
        [Description("The book ID.")] int bookId)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        var ct = _runState.CreateRunCts(bookId);
        _ = RunInBackground(bookId, (o, c) => o.ContinuePlanningAsync(bookId, c), ct);
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "continue_planning" }, _json);
    }

    // ── Full workflow ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "start_workflow")]
    [Description("Start the full autonomous book-writing workflow: Plan (Story Bible + Characters + Plot Threads + Outlines) → Write each chapter → Continuity check → Edit each chapter. This is a long-running operation. Monitor with get_agent_status and respond to questions with answer_agent_question.")]
    public async Task<string> StartWorkflow(
        [Description("The book ID to run the full workflow for.")] int bookId)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        var ct = _runState.CreateRunCts(bookId);
        _ = RunInBackground(bookId, (o, c) => o.StartWorkflowAsync(bookId, c), ct);
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "full_workflow" }, _json);
    }

    [McpServerTool(Name = "continue_workflow")]
    [Description("Resume an interrupted full workflow from where it left off. Chapters already written will be skipped.")]
    public async Task<string> ContinueWorkflow(
        [Description("The book ID.")] int bookId)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        var ct = _runState.CreateRunCts(bookId);
        _ = RunInBackground(bookId, (o, c) => o.ContinueWorkflowAsync(bookId, c), ct);
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "continue_workflow" }, _json);
    }

    [McpServerTool(Name = "stop_workflow")]
    [Description("Stop any currently running agent for a book. The agent will finish its current LLM call before stopping.")]
    public string StopWorkflow(
        [Description("The book ID.")] int bookId)
    {
        _runState.CancelRun(bookId);
        return JsonSerializer.Serialize(new { stopped = true, bookId }, _json);
    }

    // ── Per-chapter operations ────────────────────────────────────────────────

    [McpServerTool(Name = "write_chapter")]
    [Description("Write a specific chapter using the Writer agent. Uses the chapter's outline plus RAG context from previous chapters. The chapter must already exist with an outline. Progress is streamed via SignalR; use get_chapter when complete to read the result.")]
    public async Task<string> WriteChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID to write.")] int chapterId)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        _ = RunInBackground(bookId, (o, ct) => o.StartWritingAsync(bookId, chapterId, ct));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "writing" }, _json);
    }

    [McpServerTool(Name = "edit_chapter")]
    [Description("Edit a specific chapter using the Editor agent. The chapter must already have content written. The editor will improve prose and return editorial notes.")]
    public async Task<string> EditChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID to edit.")] int chapterId)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        _ = RunInBackground(bookId, (o, ct) => o.StartEditingAsync(bookId, chapterId, ct));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "editing" }, _json);
    }

    [McpServerTool(Name = "run_continuity_check")]
    [Description("Run the Continuity Checker agent. When chapterId is provided, checks that specific chapter against established facts. When omitted, performs a full cross-manuscript review of all chapters.")]
    public async Task<string> RunContinuityCheck(
        [Description("The book ID.")] int bookId,
        [Description("Optional chapter ID for a focused per-chapter check. Omit for a full manuscript check.")] int? chapterId = null)
    {
        await EnsureBookOwnershipAsync(bookId);
        EnsureNotRunning(bookId);
        _ = RunInBackground(bookId, (o, ct) => o.StartContinuityCheckAsync(bookId, ct));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "continuity_check" }, _json);
    }

    // ── Human-in-the-loop ─────────────────────────────────────────────────────

    [McpServerTool(Name = "answer_agent_question")]
    [Description("Answer a question posed by a paused agent. Use get_agent_status to detect WaitingForInput state, then get_agent_messages to find the unanswered Question message and its ID. Submitting the answer unblocks the agent and it resumes generation.")]
    public async Task<string> AnswerAgentQuestion(
        [Description("The ID of the AgentMessage with MessageType=Question that you are answering (from get_agent_messages).")] int messageId,
        [Description("Your answer to the agent's question.")] string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            throw new McpException("Answer must not be empty.");

        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        await orchestrator.ResumeWithAnswerAsync(messageId, answer);
        return JsonSerializer.Serialize(new { answered = true, messageId }, _json);
    }
}
