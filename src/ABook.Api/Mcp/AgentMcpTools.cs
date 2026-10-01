using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for triggering and controlling AI agent workflows.</summary>
public class AgentMcpTools : McpToolBase
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly AgentRunStateService _runState;
    private readonly AgentRunnerService _runner;
    private readonly IBookRepository _repo;

    public AgentMcpTools(IServiceScopeFactory scopeFactory, AgentRunStateService runState, AgentRunnerService runner, IBookRepository repo, IHttpContextAccessor http)
        : base(http)
    {
        _scopeFactory = scopeFactory;
        _runState = runState;
        _runner = runner;
        _repo = repo;
    }

    /// <summary>
    /// Checks ownership (and, for chapter runs, that the chapter exists and is not archived — archived
    /// chapters are display-only history), then claims the book and starts the run in the background.
    /// </summary>
    private async Task StartAsync(
        int bookId, AgentRole role, int? chapterId, Func<IAgentOrchestrator, CancellationToken, Task> action)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);

        if (chapterId is int id)
        {
            var chapter = await _repo.GetChapterAsync(bookId, id)
                ?? throw new McpException($"Chapter {id} not found in book {bookId}.");
            if (chapter.IsArchived)
                throw new McpException($"Chapter {id} is archived and cannot take part in generation. Restore it first.");
        }

        var result = _runner.TryStart(bookId, role, chapterId, action);
        if (result != RunStartResult.Success)
            throw new McpException(result.ToMessage());
    }

    // ── Planning ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "start_planning")]
    [Description("Start the planning pipeline for a book. Runs four sequential phases: Story Bible → Characters → Plot Threads → Chapter Outlines. It may first ask clarifying questions and wait (WaitingForInput in get_agent_status): answer the open ones from list_clarifications with update_clarification — the last answer resumes planning. Human-assisted pauses after phases are answered with answer_agent_question. The run is asynchronous; returns immediately after scheduling.")]
    public async Task<string> StartPlanning(
        [Description("The book ID to plan.")] int bookId)
    {
        await StartAsync(bookId, AgentRole.Planner, null, (o, c) => o.StartPlanningAsync(bookId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "planning" }, JsonOptions);
    }

    [McpServerTool(Name = "continue_planning")]
    [Description("Resume a partially-completed planning pipeline. Skips phases already marked Complete and continues with the remaining phases. Use this when planning was interrupted.")]
    public async Task<string> ContinuePlanning(
        [Description("The book ID.")] int bookId)
    {
        await StartAsync(bookId, AgentRole.Planner, null, (o, c) => o.ContinuePlanningAsync(bookId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "continue_planning" }, JsonOptions);
    }

    // ── Full workflow ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "start_workflow")]
    [Description("Start the full autonomous book-writing workflow: Plan (Story Bible + Characters + Plot Threads + Outlines) → Write each chapter → Continuity check → Edit each chapter. This is a long-running operation. Monitor with get_agent_status and respond to questions with answer_agent_question.")]
    public async Task<string> StartWorkflow(
        [Description("The book ID to run the full workflow for.")] int bookId)
    {
        await StartAsync(bookId, AgentRole.Planner, null, (o, c) => o.StartWorkflowAsync(bookId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "full_workflow" }, JsonOptions);
    }

    [McpServerTool(Name = "continue_workflow")]
    [Description("Resume an interrupted full workflow from where it left off. Chapters already written will be skipped.")]
    public async Task<string> ContinueWorkflow(
        [Description("The book ID.")] int bookId)
    {
        await StartAsync(bookId, AgentRole.Writer, null, (o, c) => o.ContinueWorkflowAsync(bookId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, phase = "continue_workflow" }, JsonOptions);
    }

    [McpServerTool(Name = "stop_workflow")]
    [Description("Stop any currently running agent for a book. The agent will finish its current LLM call before stopping.")]
    public async Task<string> StopWorkflow(
        [Description("The book ID.")] int bookId)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);
        var stopped = _runState.CancelRun(bookId);
        return JsonSerializer.Serialize(new { stopped, bookId }, JsonOptions);
    }

    // ── Per-chapter operations ────────────────────────────────────────────────

    [McpServerTool(Name = "write_chapter")]
    [Description("Write a specific chapter using the Writer agent. Uses the chapter's outline plus RAG context from previous chapters. The chapter must already exist with an outline. Progress is streamed via SignalR; use get_chapter when complete to read the result.")]
    public async Task<string> WriteChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID to write.")] int chapterId)
    {
        await StartAsync(bookId, AgentRole.Writer, chapterId, (o, c) => o.StartWritingAsync(bookId, chapterId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "writing" }, JsonOptions);
    }

    [McpServerTool(Name = "edit_chapter")]
    [Description("Edit a specific chapter using the Editor agent. The chapter must already have content written. The editor will improve prose and return editorial notes.")]
    public async Task<string> EditChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID to edit.")] int chapterId)
    {
        await StartAsync(bookId, AgentRole.Editor, chapterId, (o, c) => o.StartEditingAsync(bookId, chapterId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "editing" }, JsonOptions);
    }

    [McpServerTool(Name = "run_continuity_check")]
    [Description("Run the Continuity Checker agent. When chapterId is provided, checks that specific chapter against established facts. When omitted, performs a full cross-manuscript review of all chapters.")]
    public async Task<string> RunContinuityCheck(
        [Description("The book ID.")] int bookId,
        [Description("Optional chapter ID for a focused per-chapter check. Omit for a full manuscript check.")] int? chapterId = null)
    {
        await StartAsync(bookId, AgentRole.ContinuityChecker, chapterId,
            (o, c) => o.StartContinuityCheckAsync(bookId, chapterId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "continuity_check" }, JsonOptions);
    }

    // ── Human-in-the-loop ─────────────────────────────────────────────────────

    [McpServerTool(Name = "answer_agent_question")]
    [Description("Answer a question posed by a paused agent (human-assisted pauses). Use get_agent_status to detect WaitingForInput state, then get_agent_messages to find the unanswered Question message and its ID. Submitting the answer unblocks the agent and it resumes generation. The planner's clarifying questions are not messages — answer those with list_clarifications / update_clarification.")]
    public async Task<string> AnswerAgentQuestion(
        [Description("The ID of the AgentMessage with MessageType=Question that you are answering (from get_agent_messages).")] int messageId,
        [Description("Your answer to the agent's question.")] string answer)
    {
        if (string.IsNullOrWhiteSpace(answer))
            throw new McpException("Answer must not be empty.");

        var message = await _repo.FindMessageByIdAsync(messageId)
            ?? throw new McpException($"Message {messageId} not found.");
        await EnsureBookOwnershipAsync(message.BookId, _repo);

        using var scope = _scopeFactory.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IAgentOrchestrator>();
        await orchestrator.ResumeWithAnswerAsync(messageId, answer);
        return JsonSerializer.Serialize(new { answered = true, messageId }, JsonOptions);
    }
}
