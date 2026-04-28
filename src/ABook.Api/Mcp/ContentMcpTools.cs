using ABook.Core.Interfaces;
using ABook.Core.Models;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ABook.Api.Mcp;

/// <summary>MCP tools for managing book content: story bible, characters, plot threads, and chapters.</summary>
public class ContentMcpTools
{
    private readonly IBookRepository _repo;
    private readonly IHttpContextAccessor _http;

    private static readonly JsonSerializerOptions _json = new()
    {
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        Converters = { new JsonStringEnumConverter() },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ContentMcpTools(IBookRepository repo, IHttpContextAccessor http)
    {
        _repo = repo;
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

    // ── Story Bible ───────────────────────────────────────────────────────────

    [McpServerTool(Name = "get_story_bible", ReadOnly = true)]
    [Description("Get the story bible for a book. The story bible contains world-building information: setting, time period, themes, tone, world rules, and notes.")]
    public async Task<string> GetStoryBible(
        [Description("The book ID.")] int bookId)
    {
        await GetOwnedBookAsync(bookId);
        var bible = await _repo.GetStoryBibleAsync(bookId);
        if (bible is null)
            return JsonSerializer.Serialize(new { exists = false }, _json);
        return JsonSerializer.Serialize(new
        {
            bible.Id, bible.BookId,
            bible.SettingDescription, bible.TimePeriod, bible.Themes,
            bible.ToneAndStyle, bible.WorldRules, bible.Notes,
            bible.CreatedAt, bible.UpdatedAt
        }, _json);
    }

    [McpServerTool(Name = "update_story_bible")]
    [Description("Create or update the story bible for a book. Provide only the fields you want to set or change.")]
    public async Task<string> UpdateStoryBible(
        [Description("The book ID.")] int bookId,
        [Description("Description of the setting and world.")] string? settingDescription = null,
        [Description("The time period the story takes place in.")] string? timePeriod = null,
        [Description("Core themes of the story.")] string? themes = null,
        [Description("Narrative tone and style (e.g. dark, humorous, literary).")] string? toneAndStyle = null,
        [Description("Rules and constraints of the story's world (e.g. magic system rules).")] string? worldRules = null,
        [Description("Additional notes for the story bible.")] string? notes = null)
    {
        await GetOwnedBookAsync(bookId);
        var bible = await _repo.GetStoryBibleAsync(bookId) ?? new StoryBible { BookId = bookId };
        if (settingDescription != null) bible.SettingDescription = settingDescription;
        if (timePeriod != null) bible.TimePeriod = timePeriod;
        if (themes != null) bible.Themes = themes;
        if (toneAndStyle != null) bible.ToneAndStyle = toneAndStyle;
        if (worldRules != null) bible.WorldRules = worldRules;
        if (notes != null) bible.Notes = notes;
        var saved = await _repo.UpsertStoryBibleAsync(bible);
        return JsonSerializer.Serialize(new
        {
            saved.Id, saved.BookId, saved.SettingDescription, saved.TimePeriod,
            saved.Themes, saved.ToneAndStyle, saved.WorldRules, saved.Notes
        }, _json);
    }

    // ── Characters ────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_characters", ReadOnly = true)]
    [Description("List all character cards for a book. Returns name, role, personality summary, and key goals for each character.")]
    public async Task<string> ListCharacters(
        [Description("The book ID.")] int bookId)
    {
        await GetOwnedBookAsync(bookId);
        var chars = await _repo.GetCharacterCardsAsync(bookId);
        var result = chars.Select(c => new
        {
            c.Id, c.Name,
            Role = c.Role.ToString(),
            c.PhysicalDescription, c.Personality, c.Backstory,
            c.GoalMotivation, c.Arc, c.FirstAppearanceChapterNumber, c.Notes
        });
        return JsonSerializer.Serialize(result, _json);
    }

    [McpServerTool(Name = "create_character")]
    [Description("Add a new character card to a book.")]
    public async Task<string> CreateCharacter(
        [Description("The book ID.")] int bookId,
        [Description("Character's full name.")] string name,
        [Description("Character role: Protagonist, Antagonist, Supporting, or Minor.")] string role,
        [Description("Physical description of the character.")] string? physicalDescription = null,
        [Description("Personality traits.")] string? personality = null,
        [Description("Character backstory.")] string? backstory = null,
        [Description("Character's primary goal and motivation.")] string? goalMotivation = null,
        [Description("Character arc — how they change through the story.")] string? arc = null,
        [Description("Chapter number where the character first appears.")] int? firstAppearanceChapterNumber = null,
        [Description("Additional notes.")] string? notes = null)
    {
        await GetOwnedBookAsync(bookId);
        if (!Enum.TryParse<CharacterRole>(role, ignoreCase: true, out var roleEnum))
            throw new McpException($"Invalid role '{role}'. Valid values: Protagonist, Antagonist, Supporting, Minor.");
        var card = await _repo.AddCharacterCardAsync(new CharacterCard
        {
            BookId = bookId, Name = name, Role = roleEnum,
            PhysicalDescription = physicalDescription ?? string.Empty,
            Personality = personality ?? string.Empty,
            Backstory = backstory ?? string.Empty,
            GoalMotivation = goalMotivation ?? string.Empty,
            Arc = arc ?? string.Empty,
            FirstAppearanceChapterNumber = firstAppearanceChapterNumber,
            Notes = notes ?? string.Empty
        });
        return JsonSerializer.Serialize(new { card.Id, card.Name, Role = card.Role.ToString() }, _json);
    }

    [McpServerTool(Name = "update_character")]
    [Description("Update an existing character card. Provide only the fields you want to change.")]
    public async Task<string> UpdateCharacter(
        [Description("The book ID.")] int bookId,
        [Description("The character card ID.")] int characterId,
        [Description("New name (leave null to keep existing).")] string? name = null,
        [Description("New role (leave null to keep existing): Protagonist, Antagonist, Supporting, Minor.")] string? role = null,
        [Description("New physical description.")] string? physicalDescription = null,
        [Description("New personality description.")] string? personality = null,
        [Description("New backstory.")] string? backstory = null,
        [Description("New goal and motivation.")] string? goalMotivation = null,
        [Description("New character arc.")] string? arc = null,
        [Description("New notes.")] string? notes = null)
    {
        await GetOwnedBookAsync(bookId);
        var card = await _repo.GetCharacterCardAsync(bookId, characterId)
            ?? throw new McpException($"Character {characterId} not found in book {bookId}.");
        if (name != null) card.Name = name;
        if (role != null)
        {
            if (!Enum.TryParse<CharacterRole>(role, ignoreCase: true, out var roleEnum))
                throw new McpException($"Invalid role '{role}'. Valid values: Protagonist, Antagonist, Supporting, Minor.");
            card.Role = roleEnum;
        }
        if (physicalDescription != null) card.PhysicalDescription = physicalDescription;
        if (personality != null) card.Personality = personality;
        if (backstory != null) card.Backstory = backstory;
        if (goalMotivation != null) card.GoalMotivation = goalMotivation;
        if (arc != null) card.Arc = arc;
        if (notes != null) card.Notes = notes;
        await _repo.UpdateCharacterCardAsync(card);
        return JsonSerializer.Serialize(new { card.Id, card.Name, Role = card.Role.ToString() }, _json);
    }

    [McpServerTool(Name = "delete_character", Destructive = true)]
    [Description("Delete a character card from a book.")]
    public async Task<string> DeleteCharacter(
        [Description("The book ID.")] int bookId,
        [Description("The character card ID to delete.")] int characterId)
    {
        await GetOwnedBookAsync(bookId);
        await _repo.DeleteCharacterCardAsync(bookId, characterId);
        return JsonSerializer.Serialize(new { deleted = true, characterId }, _json);
    }

    // ── Plot Threads ──────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_plot_threads", ReadOnly = true)]
    [Description("List all plot threads for a book. Each thread has a name, type, description, and status (Active, Resolved, Dormant).")]
    public async Task<string> ListPlotThreads(
        [Description("The book ID.")] int bookId)
    {
        await GetOwnedBookAsync(bookId);
        var threads = await _repo.GetPlotThreadsAsync(bookId);
        var result = threads.Select(t => new
        {
            t.Id, t.Name,
            Type = t.Type.ToString(),
            Status = t.Status.ToString(),
            t.Description, t.IntroducedChapterNumber, t.ResolvedChapterNumber
        });
        return JsonSerializer.Serialize(result, _json);
    }

    [McpServerTool(Name = "create_plot_thread")]
    [Description("Add a new plot thread to a book. Types: MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, ThematicThread.")]
    public async Task<string> CreatePlotThread(
        [Description("The book ID.")] int bookId,
        [Description("Name of the plot thread.")] string name,
        [Description("Description of this thread and its significance.")] string description,
        [Description("Thread type: MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, or ThematicThread.")] string type,
        [Description("Chapter number where this thread is introduced (optional).")] int? introducedChapterNumber = null,
        [Description("Chapter number where this thread is resolved (optional).")] int? resolvedChapterNumber = null,
        [Description("Status: Active, Resolved, or Dormant. Defaults to Active.")] string status = "Active")
    {
        await GetOwnedBookAsync(bookId);
        if (!Enum.TryParse<PlotThreadType>(type, ignoreCase: true, out var typeEnum))
            throw new McpException($"Invalid type '{type}'. Valid values: MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, ThematicThread.");
        if (!Enum.TryParse<PlotThreadStatus>(status, ignoreCase: true, out var statusEnum))
            throw new McpException($"Invalid status '{status}'. Valid values: Active, Resolved, Dormant.");
        var thread = await _repo.AddPlotThreadAsync(new PlotThread
        {
            BookId = bookId, Name = name, Description = description,
            Type = typeEnum, Status = statusEnum,
            IntroducedChapterNumber = introducedChapterNumber,
            ResolvedChapterNumber = resolvedChapterNumber
        });
        return JsonSerializer.Serialize(new { thread.Id, thread.Name, Type = thread.Type.ToString(), Status = thread.Status.ToString() }, _json);
    }

    [McpServerTool(Name = "update_plot_thread")]
    [Description("Update an existing plot thread. Provide only the fields you want to change.")]
    public async Task<string> UpdatePlotThread(
        [Description("The book ID.")] int bookId,
        [Description("The plot thread ID.")] int threadId,
        [Description("New name (leave null to keep existing).")] string? name = null,
        [Description("New description.")] string? description = null,
        [Description("New type: MainPlot, Subplot, CharacterArc, Mystery, Foreshadowing, WorldBuilding, ThematicThread.")] string? type = null,
        [Description("New status: Active, Resolved, Dormant.")] string? status = null,
        [Description("New introduced chapter number.")] int? introducedChapterNumber = null,
        [Description("New resolved chapter number.")] int? resolvedChapterNumber = null)
    {
        await GetOwnedBookAsync(bookId);
        var thread = await _repo.GetPlotThreadAsync(bookId, threadId)
            ?? throw new McpException($"Plot thread {threadId} not found in book {bookId}.");
        if (name != null) thread.Name = name;
        if (description != null) thread.Description = description;
        if (type != null)
        {
            if (!Enum.TryParse<PlotThreadType>(type, ignoreCase: true, out var typeEnum))
                throw new McpException($"Invalid type '{type}'.");
            thread.Type = typeEnum;
        }
        if (status != null)
        {
            if (!Enum.TryParse<PlotThreadStatus>(status, ignoreCase: true, out var statusEnum))
                throw new McpException($"Invalid status '{status}'.");
            thread.Status = statusEnum;
        }
        if (introducedChapterNumber.HasValue) thread.IntroducedChapterNumber = introducedChapterNumber;
        if (resolvedChapterNumber.HasValue) thread.ResolvedChapterNumber = resolvedChapterNumber;
        await _repo.UpdatePlotThreadAsync(thread);
        return JsonSerializer.Serialize(new { thread.Id, thread.Name, Type = thread.Type.ToString(), Status = thread.Status.ToString() }, _json);
    }

    [McpServerTool(Name = "delete_plot_thread", Destructive = true)]
    [Description("Delete a plot thread from a book.")]
    public async Task<string> DeletePlotThread(
        [Description("The book ID.")] int bookId,
        [Description("The plot thread ID to delete.")] int threadId)
    {
        await GetOwnedBookAsync(bookId);
        await _repo.DeletePlotThreadAsync(bookId, threadId);
        return JsonSerializer.Serialize(new { deleted = true, threadId }, _json);
    }

    // ── Chapters ──────────────────────────────────────────────────────────────

    [McpServerTool(Name = "list_chapters", ReadOnly = true)]
    [Description("List all chapters for a book with their number, title, outline, and status. Does not include full chapter content — use get_chapter for that.")]
    public async Task<string> ListChapters(
        [Description("The book ID.")] int bookId)
    {
        await GetOwnedBookAsync(bookId);
        var chapters = await _repo.GetChaptersAsync(bookId);
        var result = chapters.Select(c => new
        {
            c.Id, c.Number, c.Title, c.Outline,
            Status = c.Status.ToString(),
            c.PovCharacter, c.ForeshadowingNotes, c.PayoffNotes
        });
        return JsonSerializer.Serialize(result, _json);
    }

    [McpServerTool(Name = "get_chapter", ReadOnly = true)]
    [Description("Get the full content and metadata for a specific chapter, including the complete markdown prose.")]
    public async Task<string> GetChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId)
    {
        await GetOwnedBookAsync(bookId);
        var chapter = await _repo.GetChapterAsync(bookId, chapterId)
            ?? throw new McpException($"Chapter {chapterId} not found in book {bookId}.");
        return JsonSerializer.Serialize(new
        {
            chapter.Id, chapter.Number, chapter.Title, chapter.Outline, chapter.Content,
            Status = chapter.Status.ToString(),
            chapter.PovCharacter, chapter.ForeshadowingNotes, chapter.PayoffNotes,
            chapter.CreatedAt, chapter.UpdatedAt
        }, _json);
    }

    [McpServerTool(Name = "create_chapter")]
    [Description("Add a new chapter to a book. The chapter number is automatically assigned as the next in sequence.")]
    public async Task<string> CreateChapter(
        [Description("The book ID.")] int bookId,
        [Description("Chapter title.")] string title,
        [Description("Chapter outline — a synopsis of events in this chapter.")] string outline,
        [Description("POV character for this chapter (optional).")] string? povCharacter = null,
        [Description("Foreshadowing notes — what hints are planted in this chapter (optional).")] string? foreshadowingNotes = null,
        [Description("Payoff notes — what earlier foreshadowing is paid off (optional).")] string? payoffNotes = null)
    {
        await GetOwnedBookAsync(bookId);
        var existing = await _repo.GetChaptersAsync(bookId);
        var nextNumber = existing.Any() ? existing.Max(c => c.Number) + 1 : 1;
        var chapter = await _repo.AddChapterAsync(new Chapter
        {
            BookId = bookId,
            Number = nextNumber,
            Title = title,
            Outline = outline,
            PovCharacter = povCharacter ?? string.Empty,
            ForeshadowingNotes = foreshadowingNotes ?? string.Empty,
            PayoffNotes = payoffNotes ?? string.Empty
        });
        return JsonSerializer.Serialize(new { chapter.Id, chapter.Number, chapter.Title }, _json);
    }

    [McpServerTool(Name = "update_chapter")]
    [Description("Update a chapter's metadata or content. Provide only fields you want to change. You can set the full chapter content (markdown prose) here if writing manually.")]
    public async Task<string> UpdateChapter(
        [Description("The book ID.")] int bookId,
        [Description("The chapter ID.")] int chapterId,
        [Description("New title (leave null to keep existing).")] string? title = null,
        [Description("New outline (leave null to keep existing).")] string? outline = null,
        [Description("New full chapter content in markdown (leave null to keep existing).")] string? content = null,
        [Description("New POV character (leave null to keep existing).")] string? povCharacter = null,
        [Description("New foreshadowing notes.")] string? foreshadowingNotes = null,
        [Description("New payoff notes.")] string? payoffNotes = null,
        [Description("New status: Outlined, Writing, Review, Editing, Done.")] string? status = null)
    {
        await GetOwnedBookAsync(bookId);
        var chapter = await _repo.GetChapterAsync(bookId, chapterId)
            ?? throw new McpException($"Chapter {chapterId} not found in book {bookId}.");
        if (title != null) chapter.Title = title;
        if (outline != null) chapter.Outline = outline;
        if (content != null) chapter.Content = content;
        if (povCharacter != null) chapter.PovCharacter = povCharacter;
        if (foreshadowingNotes != null) chapter.ForeshadowingNotes = foreshadowingNotes;
        if (payoffNotes != null) chapter.PayoffNotes = payoffNotes;
        if (status != null)
        {
            if (!Enum.TryParse<ChapterStatus>(status, ignoreCase: true, out var statusEnum))
                throw new McpException($"Invalid status '{status}'. Valid values: Outlined, Writing, Review, Editing, Done.");
            chapter.Status = statusEnum;
        }
        chapter.UpdatedAt = DateTime.UtcNow;
        await _repo.UpdateChapterAsync(chapter);
        return JsonSerializer.Serialize(new { chapter.Id, chapter.Number, chapter.Title, Status = chapter.Status.ToString() }, _json);
    }
}
