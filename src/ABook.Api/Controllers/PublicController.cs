using ABook.Api.Services;
using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

/// <summary>
/// Unauthenticated endpoints for the public library. In public mode all books are visible.
/// In non-public mode only the authenticated user''s own books are visible.
/// </summary>
[ApiController]
[Route("api/public")]
[AllowAnonymous]
public class PublicController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly BookExportService _exporter;
    private readonly PublicModeOptions _mode;

    public PublicController(IBookRepository repo, BookExportService exporter, PublicModeOptions mode)
    {
        _repo = repo;
        _exporter = exporter;
        _mode = mode;
    }

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true &&
        User.FindFirstValue(ClaimTypes.NameIdentifier) is string s &&
        int.TryParse(s, out var id) ? id : (int?)null;

    // -- Config ----------------------------------------------------------------

    [HttpGet("config")]
    public IActionResult GetConfig() => Ok(new { isPublicMode = _mode.IsEnabled });

    // -- Genres ----------------------------------------------------------------

    [HttpGet("genres")]
    public async Task<IActionResult> GetGenres()
    {
        var userId = _mode.IsEnabled ? null : CurrentUserId;
        // If non-public and unauthenticated there are no visible books ? return empty list
        if (!_mode.IsEnabled && userId is null)
            return Ok(Array.Empty<string>());

        var genreStrings = await _repo.GetAllGenreStringsAsync(userId);
        var genres = genreStrings
            .SelectMany(g => g.Split(','))
            .Select(g => g.Trim())
            .Where(g => g.Length > 0)
            .GroupBy(g => g.ToLowerInvariant())
            .Select(grp => grp.First()) // preserve original casing of first occurrence
            .OrderBy(g => g, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Ok(genres);
    }

    // -- Books list ------------------------------------------------------------

    [HttpGet("books")]
    public async Task<IActionResult> GetBooks(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? author = null,
        [FromQuery] string? genre = null,
        [FromQuery] int? chapterCount = null)
    {
        if (!_mode.IsEnabled && CurrentUserId is null)
            return Unauthorized(new { message = "Authentication required in non-public mode." });

        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 100);

        var filter = new PublicBookFilter(
            Page: page,
            PageSize: pageSize,
            Author: string.IsNullOrWhiteSpace(author) ? null : author.Trim(),
            Genre: string.IsNullOrWhiteSpace(genre) ? null : genre.Trim(),
            ChapterCount: chapterCount,
            UserId: _mode.IsEnabled ? null : CurrentUserId);

        var (items, totalCount) = await _repo.GetPublicBooksAsync(filter);

        return Ok(new
        {
            items,
            totalCount,
            page,
            pageSize,
            totalPages = (int)Math.Ceiling((double)totalCount / pageSize)
        });
    }

    // -- Single book -----------------------------------------------------------

    [HttpGet("books/{id:int}")]
    public async Task<IActionResult> GetBook(int id)
    {
        if (!_mode.IsEnabled && CurrentUserId is null)
            return Unauthorized(new { message = "Authentication required in non-public mode." });

        var book = await _repo.GetByIdWithDetailsAsync(id);
        if (book is null) return NotFound();
        if (!_mode.IsEnabled && book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        var chapters = (book.Chapters ?? Enumerable.Empty<ABook.Core.Models.Chapter>())
            .Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Number)
            .Select(c => new
            {
                c.Id, c.Number, c.Title, c.Outline, c.Content, status = c.Status.ToString()
            })
            .ToList();

        return Ok(new
        {
            book.Id,
            book.Title,
            book.Genre,
            book.Language,
            book.Premise,
            status = book.Status.ToString(),
            book.TargetChapterCount,
            writtenChapterCount = chapters.Count,
            book.CreatedAt,
            book.UpdatedAt,
            chapters
        });
    }

    // -- Export ----------------------------------------------------------------

    [HttpGet("books/{id:int}/export")]
    public async Task<IActionResult> Export(int id, [FromQuery] string format = "html")
    {
        if (!_mode.IsEnabled && CurrentUserId is null)
            return Unauthorized(new { message = "Authentication required in non-public mode." });

        var book = await _repo.GetByIdAsync(id);
        if (book is null) return NotFound();
        if (!_mode.IsEnabled && book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        // Only allow HTML/FB2/EPUB from public endpoint (not metadata)
        var fmt = format.ToLowerInvariant();
        if (fmt is not ("html" or "fb2" or "epub")) fmt = "html";

        var (bytes, contentType, filename) = await _exporter.ExportAsync(id, fmt);
        return File(bytes, contentType, filename);
    }
}
