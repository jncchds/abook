using ABook.Agents;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for managing books, messages, and agent status.</summary>
public class BookMcpTools
{
    private readonly IBookRepository _repo;
    private readonly AgentRunStateService _runState;
    private readonly IHttpContextAccessor _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public BookMcpTools(IBookRepository repo, AgentRunStateService runState, IHttpContextAccessor http)
    {
        _repo = repo;
        _runState = runState;
        _http = http;
    }

    private int CurrentUserId() =>
        int.Parse(_http.HttpContext!.User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private async Task<Book> GetOwnedBookAsync(int bookId)
    {
        var userId = CurrentUserId();
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null || book.UserId != userId)
            throw new McpException($"Book {bookId} not found.");
        return book;
    }

    // ── Books ─────────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_books", ReadOnly = true)]
    [Description("List all books belonging to the current user. Returns a JSON array of books with their id, title, genre, status, planning phase statuses, and chapter count.")]
    public async Task<string> ListBooks()
    {
        var userId = CurrentUserId();
        var books = await _repo.GetAllAsync(userId);
        var result = books.Select(b => new
        {
            b.Id, b.Title, b.Genre, b.Premise,
            Status = b.Status.ToString(),
            StoryBibleStatus = b.StoryBibleStatus.ToString(),
            CharactersStatus = b.CharactersStatus.ToString(),
            PlotThreadsStatus = b.PlotThreadsStatus.ToString(),
            ChaptersStatus = b.ChaptersStatus.ToString(),
            b.TargetChapterCount, b.Language, b.CreatedAt
        });
        return JsonSerializer.Serialize(result, _json);
    }

    [McpServerTool(Name = "get_book", ReadOnly = true)]
    [Description("Get detailed information about a specific book including its premise, genre, language, and planning phase statuses.")]
    public async Task<string> GetBook(
        [Description("The numeric ID of the book.")] int bookId)
    {
        var book = await GetOwnedBookAsync(bookId);
        return JsonSerializer.Serialize(new
        {
            book.Id, book.Title, book.Genre, book.Premise, book.Language,
            book.TargetChapterCount,
            Status = book.Status.ToString(),
            StoryBibleStatus = book.StoryBibleStatus.ToString(),
            CharactersStatus = book.CharactersStatus.ToString(),
            PlotThreadsStatus = book.PlotThreadsStatus.ToString(),
            ChaptersStatus = book.ChaptersStatus.ToString(),
            book.CreatedAt, book.UpdatedAt
        }, _json);
    }

    [McpServerTool(Name = "create_book")]
    [Description("Create a new book project. Returns the newly created book with its assigned ID.")]
    public async Task<string> CreateBook(
        [Description("The book title.")] string title,
        [Description("A concise synopsis of the book's premise and plot.")] string premise,
        [Description("The genre (e.g. Fantasy, Thriller, Romance, Sci-Fi).")] string genre,
        [Description("Target number of chapters.")] int targetChapterCount,
        [Description("Language for the book text (e.g. English, Spanish). Defaults to English.")] string language = "English")
    {
        var userId = CurrentUserId();
        var book = await _repo.AddAsync(new Book
        {
            Title = title,
            Premise = premise,
            Genre = genre,
            TargetChapterCount = targetChapterCount,
            Language = language,
            UserId = userId
        });
        return JsonSerializer.Serialize(new { book.Id, book.Title, book.Genre, book.Premise, book.Language, book.TargetChapterCount }, _json);
    }

    [McpServerTool(Name = "update_book")]
    [Description("Update book metadata such as title, premise, genre, or target chapter count. Only provide fields you want to change.")]
    public async Task<string> UpdateBook(
        [Description("The numeric ID of the book to update.")] int bookId,
        [Description("New title (leave null to keep existing).")] string? title = null,
        [Description("New premise text (leave null to keep existing).")] string? premise = null,
        [Description("New genre (leave null to keep existing).")] string? genre = null,
        [Description("New target chapter count (leave null to keep existing).")] int? targetChapterCount = null,
        [Description("New language (leave null to keep existing).")] string? language = null)
    {
        var book = await GetOwnedBookAsync(bookId);
        if (title != null) book.Title = title;
        if (premise != null) book.Premise = premise;
        if (genre != null) book.Genre = genre;
        if (targetChapterCount.HasValue) book.TargetChapterCount = targetChapterCount.Value;
        if (language != null) book.Language = language;
        book.UpdatedAt = DateTime.UtcNow;
        await _repo.UpdateAsync(book);
        return JsonSerializer.Serialize(new { book.Id, book.Title, book.Genre, book.Premise, book.Language, book.TargetChapterCount }, _json);
    }

    [McpServerTool(Name = "delete_book", Destructive = true)]
    [Description("Permanently delete a book and all its chapters, characters, and story data. This cannot be undone.")]
    public async Task<string> DeleteBook(
        [Description("The numeric ID of the book to delete.")] int bookId)
    {
        await GetOwnedBookAsync(bookId);
        await _repo.DeleteAsync(bookId);
        return JsonSerializer.Serialize(new { deleted = true, bookId }, _json);
    }

    // ── Messages ─────────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_agent_messages", ReadOnly = true)]
    [Description("Retrieve agent messages for a book (agent output, questions, answers, notes). Optionally filter by chapter. Use this to read what agents have written, any questions they asked, and the answers they received.")]
    public async Task<string> GetAgentMessages(
        [Description("The book ID to get messages for.")] int bookId,
        [Description("Optional chapter ID to filter messages to a specific chapter.")] int? chapterId = null)
    {
        await GetOwnedBookAsync(bookId);
        var messages = await _repo.GetMessagesAsync(bookId, chapterId);
        var result = messages.Select(m => new
        {
            m.Id, m.BookId, m.ChapterId,
            AgentRole = m.AgentRole.ToString(),
            MessageType = m.MessageType.ToString(),
            m.Content, m.IsResolved, m.CreatedAt
        });
        return JsonSerializer.Serialize(result, _json);
    }

    // ── Agent status ─────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_agent_status", ReadOnly = true)]
    [Description("Get the current status of any running agent for a book. Returns null if no agent is running. States: Running, WaitingForInput (agent needs an answer — use answer_agent_question).")]
    public string GetAgentStatus(
        [Description("The book ID to check agent status for.")] int bookId)
    {
        var status = _runState.GetStatus(bookId);
        if (status is null) return JsonSerializer.Serialize(new { running = false }, _json);
        return JsonSerializer.Serialize(new
        {
            running = true,
            role = status.Role.ToString(),
            state = status.State,
            chapterId = status.ChapterId
        }, _json);
    }

    // ── Token usage ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_token_usage", ReadOnly = true)]
    [Description("Get token usage statistics for a book, broken down by agent role and chapter.")]
    public async Task<string> GetTokenUsage(
        [Description("The book ID to get token usage for.")] int bookId)
    {
        await GetOwnedBookAsync(bookId);
        var records = await _repo.GetTokenUsageAsync(bookId);
        var result = records.Select(r => new
        {
            r.Id,
            AgentRole = r.AgentRole.ToString(),
            r.ChapterId, r.PromptTokens, r.CompletionTokens, r.CreatedAt
        });
        return JsonSerializer.Serialize(result, _json);
    }
}
