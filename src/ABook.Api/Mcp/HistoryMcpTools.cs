using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text.Json;

namespace ABook.Api.Mcp;

/// <summary>
/// MCP tools for what the web UI keeps instead of deleting: archiving and restoring chapters, characters and plot
/// threads (sections: <c>archive_section</c> / <c>restore_section</c> in <see cref="ContentMcpTools"/>), their version
/// history with restore, the Story Bible's snapshots, and the planning phases' status (complete / reopen / clear).
/// Changes are refused while an agent run works on the book — a run holds its own copy of what it works on.
/// </summary>
public class HistoryMcpTools(
    IBookRepository repo, BookContentService content, PlanningPhaseService phases, IVectorStoreService vectorStore,
    IChapterIndexQueue indexQueue, IBookNotifier notifier, ABook.Api.Telegram.TelegramEventSink telegram,
    IHttpContextAccessor http) : McpToolBase(http)
{
    private void EnsureIdle(int bookId)
    {
        try { content.EnsureNoActiveRun(bookId); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    private async Task<Chapter> GetChapterAsync(int bookId, int chapterId) =>
        await repo.GetChapterAsync(bookId, chapterId)
        ?? throw new McpException($"Chapter {chapterId} not found in book {bookId}.");

    // ── Chapters ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "archive_chapter")]
    [Description("Archive a chapter: it keeps its place (the chapters after it are renumbered) and is never sent to a model, checked or exported until restored. A section left without active chapters is archived with it. Refused while an agent run works on the book.")]
    public async Task<string> ArchiveChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId)
    {
        await GetOwnedBookAsync(bookId, repo);
        var chapter = await GetChapterAsync(bookId, chapterId);
        if (chapter.IsArchived) throw new McpException($"Chapter {chapterId} is already archived.");
        EnsureIdle(bookId);
        await repo.ArchiveChapterAsync(bookId, chapterId);
        try { await vectorStore.DeleteChapterChunksAsync(bookId, chapterId); } catch { /* non-fatal: RAG skips archived chapters */ }
        await notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        return JsonSerializer.Serialize(new { archived = true, chapterId }, JsonOptions);
    }

    [McpServerTool(Name = "restore_chapter")]
    [Description("Bring an archived chapter back in its place (with its section, if that was archived). Refused while an agent run works on the book.")]
    public async Task<string> RestoreChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId)
    {
        await GetOwnedBookAsync(bookId, repo);
        var chapter = await GetChapterAsync(bookId, chapterId);
        if (!chapter.IsArchived) throw new McpException($"Chapter {chapterId} is not archived.");
        EnsureIdle(bookId);
        await repo.RestoreChapterAsync(bookId, chapterId);
        await notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        var restored = await GetChapterAsync(bookId, chapterId);
        return JsonSerializer.Serialize(new { restored.Id, restored.Number, restored.SectionId, restored.Title }, JsonOptions);
    }

    [McpServerTool(Name = "list_chapter_versions", ReadOnly = true)]
    [Description("List a chapter's versions, newest first: every write, edit, outline change and restore is kept. isActive marks the one in use; read one with get_chapter_version, bring one back with activate_chapter_version.")]
    public async Task<string> ListChapterVersions(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId)
    {
        await GetOwnedBookAsync(bookId, repo);
        await GetChapterAsync(bookId, chapterId);
        var versions = await repo.GetChapterVersionsAsync(chapterId);
        return JsonSerializer.Serialize(versions.Select(v => new
        {
            v.Id, v.VersionNumber, v.CreatedBy, v.IsActive, v.Title, v.Status,
            WordCount = string.IsNullOrEmpty(v.Content) ? 0 : v.Content.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length,
            v.CreatedAt,
        }), JsonOptions);
    }

    [McpServerTool(Name = "get_chapter_version", ReadOnly = true)]
    [Description("Read one version of a chapter in full: title, outline, plan fields and prose.")]
    public async Task<string> GetChapterVersion(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId,
        [Description("The version ID (from list_chapter_versions).")] int versionId)
    {
        await GetOwnedBookAsync(bookId, repo);
        await GetChapterAsync(bookId, chapterId);
        var v = await repo.GetChapterVersionAsync(chapterId, versionId)
            ?? throw new McpException($"Version {versionId} not found for chapter {chapterId}.");
        return JsonSerializer.Serialize(new
        {
            v.Id, v.VersionNumber, v.CreatedBy, v.IsActive, v.Title, v.Outline, v.Status, v.PovCharacter,
            v.CharactersInvolvedJson, v.PlotThreadsJson, v.ForeshadowingNotes, v.PayoffNotes, v.Content, v.CreatedAt,
        }, JsonOptions);
    }

    [McpServerTool(Name = "activate_chapter_version")]
    [Description("Make an earlier version of a chapter the active one (title, outline, plan fields and prose). The versions in between stay in the history. Refused while an agent run works on the book.")]
    public async Task<string> ActivateChapterVersion(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId,
        [Description("The version ID (from list_chapter_versions).")] int versionId)
    {
        await GetOwnedBookAsync(bookId, repo);
        var chapter = await GetChapterAsync(bookId, chapterId);
        if (chapter.IsArchived) throw new McpException($"Chapter {chapterId} is archived. Restore it first.");
        EnsureIdle(bookId);
        ChapterVersion version;
        try { version = await repo.ActivateChapterVersionAsync(bookId, chapterId, versionId); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
        // A version that was never embedded has no chunks of its own — index it now that it answers searches.
        if (!version.HasEmbeddings && !string.IsNullOrEmpty(version.Content))
            indexQueue.QueueIndex(bookId, chapterId, version.Id);
        await notifier.NotifyChapterUpdatedAsync(bookId, chapterId);
        telegram.Post(new ABook.Api.Telegram.TgChapterEvent(bookId, chapterId));
        return JsonSerializer.Serialize(new { activated = true, chapterId, version.Id, version.VersionNumber }, JsonOptions);
    }

    // ── Characters ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "archive_character")]
    [Description("Archive a character card: kept for viewing and restore, but never sent to a model again. Prefer this to delete_character. Refused while an agent run works on the book.")]
    public async Task<string> ArchiveCharacter(
        [Description("The book ID.")] int bookId,
        [Description("The character card ID.")] int characterId)
    {
        await GetOwnedBookAsync(bookId, repo);
        var card = await repo.GetCharacterCardAsync(bookId, characterId)
            ?? throw new McpException($"Character {characterId} not found in book {bookId}.");
        EnsureIdle(bookId);
        await repo.AddCharacterVersionAsync(CharactersAgent.VersionsOf(bookId, [card], "archived").Single());
        await repo.ArchiveCharacterCardAsync(bookId, characterId);
        return JsonSerializer.Serialize(new { archived = true, characterId, card.Name }, JsonOptions);
    }

    [McpServerTool(Name = "restore_character")]
    [Description("Bring an archived character card back. Refused while an agent run works on the book.")]
    public async Task<string> RestoreCharacter(
        [Description("The book ID.")] int bookId,
        [Description("The character card ID.")] int characterId)
    {
        await GetOwnedBookAsync(bookId, repo);
        _ = await repo.GetCharacterCardAsync(bookId, characterId)
            ?? throw new McpException($"Character {characterId} not found in book {bookId}.");
        EnsureIdle(bookId);
        await repo.UnarchiveCharacterCardAsync(bookId, characterId);
        return JsonSerializer.Serialize(new { restored = true, characterId }, JsonOptions);
    }

    [McpServerTool(Name = "list_character_versions", ReadOnly = true)]
    [Description("List a character card's versions (every save is kept), with their fields. Bring one back with restore_character_version.")]
    public async Task<string> ListCharacterVersions(
        [Description("The book ID.")] int bookId,
        [Description("The character card ID.")] int characterId)
    {
        await GetOwnedBookAsync(bookId, repo);
        return JsonSerializer.Serialize(await repo.GetCharacterVersionsAsync(bookId, characterId), JsonOptions);
    }

    [McpServerTool(Name = "restore_character_version")]
    [Description("Restore a character card to one of its versions; the restored state is saved as the newest version. Refused while an agent run works on the book.")]
    public async Task<string> RestoreCharacterVersion(
        [Description("The book ID.")] int bookId,
        [Description("The character card ID.")] int characterId,
        [Description("The version ID (from list_character_versions).")] int versionId)
    {
        await GetOwnedBookAsync(bookId, repo);
        EnsureIdle(bookId);
        try { return JsonSerializer.Serialize(await repo.RestoreCharacterVersionAsync(bookId, characterId, versionId), JsonOptions); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    // ── Plot threads ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "archive_plot_thread")]
    [Description("Archive a plot thread: kept for viewing and restore, but never sent to a model again. Prefer this to delete_plot_thread. Refused while an agent run works on the book.")]
    public async Task<string> ArchivePlotThread(
        [Description("The book ID.")] int bookId,
        [Description("The plot thread ID.")] int threadId)
    {
        await GetOwnedBookAsync(bookId, repo);
        var thread = await repo.GetPlotThreadAsync(bookId, threadId)
            ?? throw new McpException($"Plot thread {threadId} not found in book {bookId}.");
        EnsureIdle(bookId);
        await repo.AddPlotThreadVersionAsync(PlotThreadsAgent.VersionsOf(bookId, [thread], "archived").Single());
        await repo.ArchivePlotThreadAsync(bookId, threadId);
        return JsonSerializer.Serialize(new { archived = true, threadId, thread.Name }, JsonOptions);
    }

    [McpServerTool(Name = "restore_plot_thread")]
    [Description("Bring an archived plot thread back. Refused while an agent run works on the book.")]
    public async Task<string> RestorePlotThread(
        [Description("The book ID.")] int bookId,
        [Description("The plot thread ID.")] int threadId)
    {
        await GetOwnedBookAsync(bookId, repo);
        _ = await repo.GetPlotThreadAsync(bookId, threadId)
            ?? throw new McpException($"Plot thread {threadId} not found in book {bookId}.");
        EnsureIdle(bookId);
        await repo.UnarchivePlotThreadAsync(bookId, threadId);
        return JsonSerializer.Serialize(new { restored = true, threadId }, JsonOptions);
    }

    [McpServerTool(Name = "list_plot_thread_versions", ReadOnly = true)]
    [Description("List a plot thread's versions (every save is kept), with their fields. Bring one back with restore_plot_thread_version.")]
    public async Task<string> ListPlotThreadVersions(
        [Description("The book ID.")] int bookId,
        [Description("The plot thread ID.")] int threadId)
    {
        await GetOwnedBookAsync(bookId, repo);
        return JsonSerializer.Serialize(await repo.GetPlotThreadVersionsAsync(bookId, threadId), JsonOptions);
    }

    [McpServerTool(Name = "restore_plot_thread_version")]
    [Description("Restore a plot thread to one of its versions; the restored state is saved as the newest version. Refused while an agent run works on the book.")]
    public async Task<string> RestorePlotThreadVersion(
        [Description("The book ID.")] int bookId,
        [Description("The plot thread ID.")] int threadId,
        [Description("The version ID (from list_plot_thread_versions).")] int versionId)
    {
        await GetOwnedBookAsync(bookId, repo);
        EnsureIdle(bookId);
        try { return JsonSerializer.Serialize(await repo.RestorePlotThreadVersionAsync(bookId, threadId, versionId), JsonOptions); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "list_clarification_versions", ReadOnly = true)]
    [Description("List a clarification's versions (every change of its question or answer is kept, newest first). Bring one back with restore_clarification_version.")]
    public async Task<string> ListClarificationVersions(
        [Description("The book ID.")] int bookId,
        [Description("The clarification ID.")] int clarificationId)
    {
        await GetOwnedBookAsync(bookId, repo);
        return JsonSerializer.Serialize(await repo.GetClarificationVersionsAsync(bookId, clarificationId), JsonOptions);
    }

    [McpServerTool(Name = "restore_clarification_version")]
    [Description("Restore a clarification to one of its versions (and un-archive it); the restored text is saved as the newest version. Refused while an agent run works on the book.")]
    public async Task<string> RestoreClarificationVersion(
        [Description("The book ID.")] int bookId,
        [Description("The clarification ID.")] int clarificationId,
        [Description("The version ID (from list_clarification_versions).")] int versionId)
    {
        await GetOwnedBookAsync(bookId, repo);
        EnsureIdle(bookId);
        try { return JsonSerializer.Serialize(await repo.RestoreClarificationVersionAsync(bookId, clarificationId, versionId), JsonOptions); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    // ── Story Bible ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_story_bible_versions", ReadOnly = true)]
    [Description("List the Story Bible's snapshots (newest first), each with its fields and the reason it was taken. Bring one back with restore_story_bible_version.")]
    public async Task<string> ListStoryBibleVersions(
        [Description("The book ID.")] int bookId)
    {
        await GetOwnedBookAsync(bookId, repo);
        return JsonSerializer.Serialize(await repo.GetStoryBibleSnapshotsAsync(bookId), JsonOptions);
    }

    [McpServerTool(Name = "restore_story_bible_version")]
    [Description("Restore the Story Bible to one of its snapshots; the current one is kept as a snapshot too. Refused while the Story Bible is locked (Interactive books being written — only the author unlocks it, in Book Settings) or while an agent run works on the book.")]
    public async Task<string> RestoreStoryBibleVersion(
        [Description("The book ID.")] int bookId,
        [Description("The snapshot ID (from list_story_bible_versions).")] int snapshotId)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        if (BookContentService.IsBibleLocked(book)) throw new McpException(new StoryBibleLockedException().Message);
        EnsureIdle(bookId);
        try { return JsonSerializer.Serialize(await repo.RestoreStoryBibleSnapshotAsync(bookId, snapshotId), JsonOptions); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }
    }

    // ── Planning phases ───────────────────────────────────────────────────────

    private const string PhaseHelp =
        "clarifications, storybible, characters, plotthreads or chapters (the plan: sections and their chapters).";

    [McpServerTool(Name = "complete_planning_phase")]
    [Description("Mark a planning phase Complete (its data is kept): start_planning / start_workflow then skip it. Completing clarifications releases a planning run waiting for answers.")]
    public async Task<string> CompletePlanningPhase(
        [Description("The book ID.")] int bookId,
        [Description(PhaseHelp)] string phase)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        if (!await phases.SetStatusAsync(book, phase, PlanningPhaseStatus.Complete))
            throw new McpException($"Unknown planning phase: {phase}. Valid: {string.Join(", ", PlanningPhaseService.Phases)}.");
        return JsonSerializer.Serialize(new { phase, status = "Complete" }, JsonOptions);
    }

    [McpServerTool(Name = "reopen_planning_phase")]
    [Description("Reopen a planning phase (back to NotStarted, data kept): the next start_planning runs it again and refines what exists. Reopening clarifications asks a fresh round of questions.")]
    public async Task<string> ReopenPlanningPhase(
        [Description("The book ID.")] int bookId,
        [Description(PhaseHelp)] string phase)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        if (!await phases.SetStatusAsync(book, phase, PlanningPhaseStatus.NotStarted))
            throw new McpException($"Unknown planning phase: {phase}. Valid: {string.Join(", ", PlanningPhaseService.Phases)}.");
        return JsonSerializer.Serialize(new { phase, status = "NotStarted" }, JsonOptions);
    }

    [McpServerTool(Name = "clear_planning_phase", Destructive = true)]
    [Description("Clear a planning phase and reset it to NotStarted, so the next start_planning starts it from scratch. Story Bible, characters and plot threads are snapshotted first (restore from their history); clarifications and chapters are archived, not deleted (clearing chapters archives every chapter and with them their sections). Refused for the Story Bible while it is locked.")]
    public async Task<string> ClearPlanningPhase(
        [Description("The book ID.")] int bookId,
        [Description(PhaseHelp)] string phase)
    {
        var book = await GetOwnedBookAsync(bookId, repo);
        if (phase.Equals("chapters", StringComparison.OrdinalIgnoreCase)) EnsureIdle(bookId);
        try { await phases.ClearAsync(book, phase); }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { throw new McpException(ex.Message); }
        return JsonSerializer.Serialize(new { phase, cleared = true, status = "NotStarted" }, JsonOptions);
    }
}
