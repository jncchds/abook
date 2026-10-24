using System.ComponentModel;
using System.Text.Json;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace ABook.Api.Mcp;

/// <summary>MCP tools to rewind a whole book (and its chat) to an earlier moment.</summary>
public class RewindMcpTools(IBookRepository repo, IBookRewinder rewinder, RewindService rewinds, AgentRunStateService runState, IHttpContextAccessor http)
    : McpToolBase(http)
{
    [McpServerTool(Name = "list_rewind_points", ReadOnly = true)]
    [Description("Moments a book can be rewound to, newest first: before/after book-chat messages, after chapter versions, before/after agent runs. Also the earliest moment (when the book's change log began).")]
    public async Task<string> ListRewindPoints(
        [Description("The book ID.")] int bookId,
        [Description("Only moments before this UTC time (ISO 8601), to page back.")] DateTime? before = null,
        [Description("How many (default 30, max 100).")] int limit = 30)
    {
        await GetOwnedBookAsync(bookId, repo);
        return JsonSerializer.Serialize(new
        {
            earliest = await rewinder.EarliestAsync(bookId),
            points = await rewinder.PointsAsync(bookId, before?.ToUniversalTime(), Math.Clamp(limit, 1, 100)),
        }, JsonOptions);
    }

    [McpServerTool(Name = "preview_rewind", ReadOnly = true)]
    [Description("What rewinding the book to a moment would change — chapters, Story Bible, characters, plot threads, clarifications, book fields and book-chat suggestions restored, recreated or archived, and how many chat messages get hidden. Nothing changes. Pass the returned fingerprint to rewind_book.")]
    public async Task<string> PreviewRewind(
        [Description("The book ID.")] int bookId,
        [Description("The moment, UTC (ISO 8601) — e.g. the 'at' of a rewind point.")] DateTime at)
    {
        await GetOwnedBookAsync(bookId, repo);
        try { return JsonSerializer.Serialize(await rewinder.PreviewAsync(bookId, at.ToUniversalTime()), JsonOptions); }
        catch (RewindTooEarlyException ex) { throw new McpException(ex.Message); }
    }

    [McpServerTool(Name = "rewind_book")]
    [Description("Rewind the whole book and its chat to a moment, as a new state: every part gets the state it had then (saved as new versions — everything in between stays in each part's history), parts made since are archived, and book-chat messages after it are hidden and never sent to the LLM again. There is no fast-forward. Refused while an agent runs, or when the book changed since the preview (fingerprint).")]
    public async Task<string> RewindBook(
        [Description("The book ID.")] int bookId,
        [Description("The moment, UTC (ISO 8601).")] DateTime at,
        [Description("The fingerprint from preview_rewind.")] string fingerprint,
        [Description("Seconds to wait for the rewind to finish (max 120).")] int waitSeconds = 60)
    {
        await GetOwnedBookAsync(bookId, repo);
        var outcome = await rewinds.StartAsync(bookId, at.ToUniversalTime(), fingerprint, origin: "MCP");
        if (outcome.Result != RewindStartResult.Started) throw new McpException(outcome.Message);

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 120));
        while (runState.GetStatus(bookId) is { IsActive: true } && DateTime.UtcNow < deadline)
            await Task.Delay(300);
        var status = runState.GetStatus(bookId);
        return JsonSerializer.Serialize(new { status = status?.State ?? "Unknown", rewoundTo = at.ToUniversalTime() }, JsonOptions);
    }
}
