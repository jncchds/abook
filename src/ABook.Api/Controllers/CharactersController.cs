using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using System.Security.Claims;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/characters")]
[Authorize]
public class CharactersController : ControllerBase
{
    private readonly IBookRepository _repo;

    public CharactersController(IBookRepository repo) => _repo = repo;

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!)
            : (int?)null;

    private async Task<IActionResult?> CheckOwnershipAsync(int bookId)
    {
        var book = await _repo.GetByIdAsync(bookId);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();
        return null;
    }

    [HttpGet]
    public async Task<IActionResult> GetAll(int bookId, [FromQuery] bool includeArchived = false) =>
        Ok(includeArchived
            ? await _repo.GetAllCharacterCardsAsync(bookId)
            : await _repo.GetCharacterCardsAsync(bookId));

    [HttpGet("{cardId:int}")]
    public async Task<IActionResult> GetById(int bookId, int cardId)
    {
        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        return card is null ? NotFound() : Ok(card);
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] CharacterCardRequest req)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

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
        await _repo.AddCharacterVersionAsync(new CharacterCardVersion
        {
            CharacterCardId = created.Id,
            BookId = bookId,
            Name = created.Name,
            Role = created.Role,
            PhysicalDescription = created.PhysicalDescription,
            Personality = created.Personality,
            Backstory = created.Backstory,
            GoalMotivation = created.GoalMotivation,
            Arc = created.Arc,
            FirstAppearanceChapterNumber = created.FirstAppearanceChapterNumber,
            Notes = created.Notes,
            CreatedBy = "user",
        });
        return CreatedAtAction(nameof(GetById), new { bookId, cardId = created.Id }, created);
    }

    [HttpPut("{cardId:int}")]
    public async Task<IActionResult> Update(int bookId, int cardId, [FromBody] CharacterCardRequest req)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        if (card is null) return NotFound();
        await _repo.AddCharacterVersionAsync(new CharacterCardVersion
        {
            CharacterCardId = cardId,
            BookId = bookId,
            Name = req.Name,
            Role = req.Role,
            PhysicalDescription = req.PhysicalDescription,
            Personality = req.Personality,
            Backstory = req.Backstory,
            GoalMotivation = req.GoalMotivation,
            Arc = req.Arc,
            FirstAppearanceChapterNumber = req.FirstAppearanceChapterNumber,
            Notes = req.Notes,
            CreatedBy = "user",
        });
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

    [HttpPost("{cardId:int}/archive")]
    public async Task<IActionResult> Archive(int bookId, int cardId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        if (card is null) return NotFound();
        await _repo.AddCharacterVersionAsync(new CharacterCardVersion
        {
            CharacterCardId = cardId,
            BookId = bookId,
            Name = card.Name,
            Role = card.Role,
            PhysicalDescription = card.PhysicalDescription,
            Personality = card.Personality,
            Backstory = card.Backstory,
            GoalMotivation = card.GoalMotivation,
            Arc = card.Arc,
            FirstAppearanceChapterNumber = card.FirstAppearanceChapterNumber,
            Notes = card.Notes,
            CreatedBy = "archived",
        });
        await _repo.ArchiveCharacterCardAsync(bookId, cardId);
        return NoContent();
    }

    [HttpPost("{cardId:int}/unarchive")]
    public async Task<IActionResult> Unarchive(int bookId, int cardId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        if (card is null) return NotFound();
        await _repo.UnarchiveCharacterCardAsync(bookId, cardId);
        return Ok(await _repo.GetCharacterCardAsync(bookId, cardId));
    }

    [HttpDelete("{cardId:int}")]
    public async Task<IActionResult> Delete(int bookId, int cardId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        await _repo.DeleteCharacterCardAsync(bookId, cardId);
        return NoContent();
    }

    [HttpGet("{cardId:int}/history")]
    public async Task<IActionResult> GetItemHistory(int bookId, int cardId)
    {
        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        if (card is null) return NotFound();
        return Ok(await _repo.GetCharacterVersionsAsync(bookId, cardId));
    }

    [HttpGet("{cardId:int}/history/{versionId:int}")]
    public async Task<IActionResult> GetItemVersion(int bookId, int cardId, int versionId)
    {
        var version = await _repo.GetCharacterVersionAsync(bookId, cardId, versionId);
        return version is null ? NotFound() : Ok(version);
    }

    [HttpPost("{cardId:int}/history/{versionId:int}/restore")]
    public async Task<IActionResult> RestoreItemVersion(int bookId, int cardId, int versionId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        try { return Ok(await _repo.RestoreCharacterVersionAsync(bookId, cardId, versionId)); }
        catch (InvalidOperationException) { return NotFound(); }
    }

    [HttpGet("history")]
    public async Task<IActionResult> GetHistory(int bookId) =>
        Ok(await _repo.GetCharactersSnapshotsAsync(bookId));

    [HttpGet("history/{snapshotId:int}")]
    public async Task<IActionResult> GetSnapshot(int bookId, int snapshotId)
    {
        var snapshot = await _repo.GetCharactersSnapshotAsync(bookId, snapshotId);
        return snapshot is null ? NotFound() : Ok(snapshot);
    }

    [HttpPost("history/{snapshotId:int}/restore")]
    public async Task<IActionResult> RestoreSnapshot(int bookId, int snapshotId)
    {
        var ownershipError = await CheckOwnershipAsync(bookId);
        if (ownershipError is not null) return ownershipError;

        try { return Ok(await _repo.RestoreCharactersSnapshotAsync(bookId, snapshotId)); }
        catch (InvalidOperationException) { return NotFound(); }
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
