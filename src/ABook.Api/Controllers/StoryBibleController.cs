using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/story-bible")]
public class StoryBibleController : ControllerBase
{
    private readonly IBookRepository _repo;

    public StoryBibleController(IBookRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> Get(int bookId)
    {
        var bible = await _repo.GetStoryBibleAsync(bookId);
        return bible is null ? Ok(new StoryBible { BookId = bookId }) : Ok(bible);
    }

    [HttpPut]
    public async Task<IActionResult> Upsert(int bookId, [FromBody] StoryBibleRequest req)
    {
        var bible = new StoryBible
        {
            BookId = bookId,
            SettingDescription = req.SettingDescription,
            TimePeriod = req.TimePeriod,
            Themes = req.Themes,
            ToneAndStyle = req.ToneAndStyle,
            WorldRules = req.WorldRules,
            Notes = req.Notes
        };
        var result = await _repo.UpsertStoryBibleAsync(bible);
        return Ok(result);
    }

    [HttpDelete]
    public async Task<IActionResult> Delete(int bookId)
    {
        await _repo.DeleteStoryBibleAsync(bookId);
        return NoContent();
    }
}

public record StoryBibleRequest(
    string SettingDescription,
    string TimePeriod,
    string Themes,
    string ToneAndStyle,
    string WorldRules,
    string Notes);
