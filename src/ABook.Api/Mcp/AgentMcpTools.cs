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
        var book = await GetOwnedBookAsync(bookId, _repo);

        // Planning and the workflow (Planner, or Writer without a chapter) belong to Autonomous books.
        if (book.Mode == BookMode.Interactive && chapterId is null && role is AgentRole.Planner or AgentRole.Writer)
            throw new McpException("This is an Interactive book: it is planned and written in the book chat (send_book_chat_message). Switch its mode with update_book to use the automatic pipeline.");

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
    [Description("Start the planning pipeline for a book. Runs four sequential phases: Story Bible → Characters → Plot Threads → Chapter Outlines. It may first ask clarifying questions and wait (WaitingForInput in get_agent_status): answer the open ones from list_clarifications with update_clarification — the last answer resumes planning. Autonomous books only. The run is asynchronous; returns immediately after scheduling.")]
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

    [McpServerTool(Name = "break_down_section")]
    [Description("Divide a section (see list_sections) into chapters with the planner: the section keeps its overall plan and gets one chapter per part, each with its own outline. Only for a section none of whose chapters is written yet (for a written chapter use split_chapter). Runs in the background; use list_sections when it completes.")]
    public async Task<string> BreakDownSection(
        [Description("The book ID.")] int bookId,
        [Description("The section ID.")] int sectionId,
        [Description("How many chapters to make (optional; default: the planner decides, 2-4).")] int? chapters = null)
    {
        await GetOwnedBookAsync(bookId, _repo);
        var section = await _repo.GetSectionAsync(bookId, sectionId)
            ?? throw new McpException($"Section {sectionId} not found in book {bookId}.");
        if (section.IsArchived) throw new McpException($"Section {sectionId} is archived. Restore it first.");
        if ((await _repo.GetChaptersAsync(bookId)).Any(c => c.SectionId == sectionId && !string.IsNullOrWhiteSpace(c.Content)))
            throw new McpException("This section already has written chapters. Use split_chapter instead, or clear their text first.");
        if (chapters is < 1 or > 10) throw new McpException("chapters must be 1-10.");
        await StartAsync(bookId, AgentRole.ChaptersAgent, null, (o, c) => o.StartSectionBreakdownAsync(bookId, sectionId, chapters, c));
        return JsonSerializer.Serialize(new { started = true, bookId, sectionId, phase = "breakdown" }, JsonOptions);
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
    [Description("Run the Continuity Checker agent. With chapterId: checks that chapter against established facts. With sectionId: checks each written chapter of that section, one at a time. With neither: a full cross-manuscript review of all chapters. Only written chapters (Review or Done) are checked; findings arrive in the agent messages.")]
    public async Task<string> RunContinuityCheck(
        [Description("The book ID.")] int bookId,
        [Description("Optional chapter ID for a focused per-chapter check.")] int? chapterId = null,
        [Description("Optional section ID: check each written chapter of the section (not with chapterId).")] int? sectionId = null)
    {
        if (chapterId is not null && sectionId is not null) throw new McpException("Pass chapterId or sectionId, not both.");
        if (sectionId is int sid)
        {
            await GetActiveSectionAsync(bookId, sid);
            if (!(await _repo.GetChaptersAsync(bookId)).Any(c => c.SectionId == sid && c.Status is ChapterStatus.Done or ChapterStatus.Review))
                throw new McpException("This section has no written chapters to check.");
            await StartAsync(bookId, AgentRole.ContinuityChecker, null, (o, c) => o.StartSectionCheckAsync(bookId, sid, c));
            return JsonSerializer.Serialize(new { started = true, bookId, sectionId, phase = "continuity_check" }, JsonOptions);
        }
        await StartAsync(bookId, AgentRole.ContinuityChecker, chapterId,
            (o, c) => o.StartContinuityCheckAsync(bookId, chapterId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, chapterId, phase = "continuity_check" }, JsonOptions);
    }

    [McpServerTool(Name = "write_section")]
    [Description("Autonomous books: write one section (see list_sections) the way start_workflow writes the book — a section the planner expects to need several chapters is broken down first, then every chapter of it that is not Done goes through pre-write check → Writer → check → fixes, in order. Done chapters are skipped, so it also resumes. Runs in the background; use list_chapters / get_chapter when it completes.")]
    public async Task<string> WriteSection(
        [Description("The book ID.")] int bookId,
        [Description("The section ID.")] int sectionId)
    {
        await GetActiveSectionAsync(bookId, sectionId);
        await StartAsync(bookId, AgentRole.Writer, null, (o, c) => o.StartSectionWritingAsync(bookId, sectionId, c));
        return JsonSerializer.Serialize(new { started = true, bookId, sectionId, phase = "writing_section" }, JsonOptions);
    }

    private async Task GetActiveSectionAsync(int bookId, int sectionId)
    {
        await GetOwnedBookAsync(bookId, _repo);
        var section = await _repo.GetSectionAsync(bookId, sectionId)
            ?? throw new McpException($"Section {sectionId} not found in book {bookId}.");
        if (section.IsArchived) throw new McpException($"Section {sectionId} is archived. Restore it first.");
    }

    // ── Human-in-the-loop ─────────────────────────────────────────────────────

    [McpServerTool(Name = "answer_agent_question")]
    [Description("Answer a question posed by a paused agent (human-assisted pauses). Use get_agent_status to detect WaitingForInput state, then get_agent_messages to find the unanswered Question message and its ID. Submitting the answer unblocks the agent and it resumes generation. Human-assisted review pauses (after a planning phase, before a chapter is written, before a chapter's creative rewrite) are better handled with the section chat tools (get_section_chat, send_section_chat_message, complete_section_review); answered here, the answer text is ignored except before a creative rewrite, where it is passed to the rewrite as author instructions. The planner's clarifying questions are not messages — answer those with list_clarifications / update_clarification.")]
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
