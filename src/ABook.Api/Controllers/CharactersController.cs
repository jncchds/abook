using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/characters")]
public class CharactersController : ControllerBase
{
    private readonly IBookRepository _repo;

    public CharactersController(IBookRepository repo) => _repo = repo;

    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId) =>
        Ok(await _repo.GetCharacterCardsAsync(bookId));

    [HttpGet("{cardId:int}")]
    public async Task<IActionResult> GetById(int bookId, int cardId)
    {
        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        return card is null ? NotFound() : Ok(card);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] CharacterCardRequest req)
    {
        var card = new CharacterCard
        {
            BookId = bookId,
            Name = req.Name,
            Role = req.Role,
            PhysicalDescription = req.PhysicalDescription,
            Personality = req.Personality,
            Backstory = req.Backstory,
            GoalMotivation = req.GoalMotivation,
            Arc = req.Arc,
            FirstAppearanceChapterNumber = req.FirstAppearanceChapterNumber,
            Notes = req.Notes
        };
        var created = await _repo.AddCharacterCardAsync(card);
        return CreatedAtAction(nameof(GetById), new { bookId, cardId = created.Id }, created);
    }

    [HttpPut("{cardId:int}")]
    public async Task<IActionResult> Update(int bookId, int cardId, [FromBody] CharacterCardRequest req)
    {
        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        if (card is null) return NotFound();
        card.Name = req.Name;
        card.Role = req.Role;
        card.PhysicalDescription = req.PhysicalDescription;
        card.Personality = req.Personality;
        card.Backstory = req.Backstory;
        card.GoalMotivation = req.GoalMotivation;
        card.Arc = req.Arc;
        card.FirstAppearanceChapterNumber = req.FirstAppearanceChapterNumber;
        card.Notes = req.Notes;
        await _repo.UpdateCharacterCardAsync(card);
        return Ok(card);
    }

    [HttpDelete("{cardId:int}")]
    public async Task<IActionResult> Delete(int bookId, int cardId)
    {
        await _repo.DeleteCharacterCardAsync(bookId, cardId);
        return NoContent();
    }
}

public record CharacterCardRequest(
    string Name,
    CharacterRole Role,
    string PhysicalDescription,
    string Personality,
    string Backstory,
    string GoalMotivation,
    string Arc,
    int? FirstAppearanceChapterNumber,
    string Notes);
