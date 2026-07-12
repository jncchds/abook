using ABook.Api.Services;
using ABook.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/export")]
[Authorize]
public class ExportController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly BookExportService _exporter;

    public ExportController(IBookRepository repo, BookExportService exporter)
    {
        _repo = repo;
        _exporter = exporter;
    }

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value)
            : (int?)null;

    [HttpGet]
    public async Task<IActionResult> Export(int bookId, [FromQuery] string format = "html")
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        var (bytes, contentType, filename) = await _exporter.ExportAsync(bookId, format);
        return File(bytes, contentType, filename);
    }
}
