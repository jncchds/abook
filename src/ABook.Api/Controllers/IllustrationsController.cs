using System.Text.Json;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Core.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

/// <summary>
/// Illustrations: the plan the LLM wrote (reviewable and editable), the runs that plan and render them,
/// and the rendered image bytes. Planning and rendering are separate runs so the text model can be
/// unloaded before the image model loads.
/// </summary>
[ApiController]
[Route("api/books/{bookId:int}/illustrations")]
[Authorize]
public class IllustrationsController : ControllerBase
{
    private readonly IBookRepository _repo;
    private readonly AgentRunnerService _runner;
    private readonly IBookNotifier _notifier;

    public IllustrationsController(IBookRepository repo, AgentRunnerService runner, IBookNotifier notifier)
    {
        _repo = repo;
        _runner = runner;
        _notifier = notifier;
    }

    [HttpGet]
    public async Task<IActionResult> List(int bookId, [FromQuery] int? chapterId, [FromQuery] IllustrationKind? kind)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        return Ok(await IllustrationViews.LoadAsync(_repo, bookId, chapterId, kind));
    }

    [HttpPost]
    public async Task<IActionResult> Create(int bookId, [FromBody] IllustrationRequest req)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        if (req.Kind == IllustrationKind.Portrait)
            return BadRequest(new { message = "Portraits are created by planning the characters' visuals." });

        var illustration = new Illustration { BookId = bookId, Kind = req.Kind };
        if (req.Kind == IllustrationKind.Scene)
        {
            if (req.ChapterId is not int chapterId) return BadRequest(new { message = "A scene needs a chapter." });
            var chapter = await _repo.GetChapterAsync(bookId, chapterId);
            if (chapter is null) return NotFound(new { message = $"Chapter {chapterId} not found." });
            illustration.ChapterId = chapterId;
            PlaceAfter(illustration, ParagraphAnchors.Split(chapter.Content), req.ParagraphNumber ?? 1);
        }
        else if ((await _repo.GetIllustrationsAsync(bookId, kind: IllustrationKind.Cover)).Count > 0)
        {
            return Conflict(new { message = "The book already has a cover — edit it instead." });
        }

        Apply(illustration, req);
        var saved = await _repo.AddIllustrationAsync(illustration);
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, saved.Id);
        return Ok(await ToDtoAsync(bookId, saved));
    }

    /// <summary>
    /// Edits the plan. A change to what the image shows marks it for re-rendering; its current image
    /// stays visible (and exported) until then.
    /// </summary>
    [HttpPut("{id:int}")]
    public async Task<IActionResult> Update(int bookId, int id, [FromBody] IllustrationRequest req)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        var illustration = await _repo.GetIllustrationAsync(bookId, id);
        if (illustration is null) return NotFound();

        if (illustration.ChapterId is int chapterId && req.ParagraphNumber is int paragraph
            && paragraph != illustration.ParagraphNumber)
        {
            var chapter = await _repo.GetChapterAsync(bookId, chapterId);
            PlaceAfter(illustration, ParagraphAnchors.Split(chapter?.Content), paragraph);
        }

        var visualChanged = illustration.Prompt != (req.Prompt ?? illustration.Prompt)
            || illustration.NegativePrompt != (req.NegativePrompt ?? illustration.NegativePrompt)
            || illustration.Orientation != (req.Orientation ?? illustration.Orientation)
            || (req.FocusCharacterId.HasValue && illustration.FocusCharacterId != NullIfZero(req.FocusCharacterId));
        Apply(illustration, req);
        if (visualChanged && illustration.Status == IllustrationStatus.Rendered)
            illustration.Status = IllustrationStatus.Planned;

        await _repo.UpdateIllustrationAsync(illustration);
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, id);
        return Ok(await ToDtoAsync(bookId, illustration));
    }

    [HttpDelete("{id:int}")]
    public async Task<IActionResult> Delete(int bookId, int id)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        await _repo.DeleteIllustrationAsync(bookId, id);
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, null);
        return NoContent();
    }

    /// <summary>
    /// Sets a character's canonical visual description by hand. The new wording replaces the old one in
    /// every prompt that quotes it, and those illustrations are marked for re-rendering.
    /// </summary>
    [HttpPut("characters/{cardId:int}/visual")]
    public async Task<IActionResult> UpdateCharacterVisual(int bookId, int cardId, [FromBody] CharacterVisualRequest req)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        var card = await _repo.GetCharacterCardAsync(bookId, cardId);
        if (card is null) return NotFound();

        var updated = await IllustrationPlannerAgent.SetCharacterVisualAsync(_repo, card, req.VisualPrompt ?? "");
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, null);
        return Ok(new { card.Id, card.VisualPrompt, card.ImageSeed, updatedIllustrations = updated });
    }

    /// <summary>
    /// Edits the book's art direction, which is added word for word to every image. Portrait prompts are
    /// rebuilt from the new composition and rendered images are marked for re-rendering.
    /// </summary>
    [HttpPut("style")]
    public async Task<IActionResult> UpdateStyle(int bookId, [FromBody] StyleSheetRequest req)
    {
        var (denied, book) = await this.RequireBookOwnershipAndLoadAsync(bookId, _repo);
        if (denied is not null) return denied;
        var marked = await IllustrationPlannerAgent.ApplyStyleSheetAsync(_repo, book!,
            req.Style?.Trim() ?? "", req.PortraitFraming?.Trim() ?? "", req.NegativePrompt?.Trim() ?? "");
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, null);
        return Ok(new
        {
            style = book!.IllustrationStyle,
            portraitFraming = book.IllustrationPortraitFraming,
            negativePrompt = book.IllustrationNegativePrompt,
            markedForRerender = marked,
        });
    }

    [HttpPost("plan")]
    public async Task<IActionResult> Plan(int bookId, [FromBody] PlanIllustrationsRequest req)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        if (req.Scope == IllustrationPlanScope.Chapter)
        {
            var chapter = req.TargetId is int cid ? await _repo.GetChapterAsync(bookId, cid) : null;
            if (chapter is null) return NotFound(new { message = "Chapter not found." });
            if (chapter.IsArchived) return BadRequest(new { message = "This chapter is archived. Restore it first." });
            if (string.IsNullOrWhiteSpace(chapter.Content))
                return BadRequest(new { message = "This chapter has no text to illustrate yet." });
        }

        var request = new IllustrationPlanRequest(req.Scope, req.TargetId);
        var chapterId = req.Scope == IllustrationPlanScope.Chapter ? req.TargetId : null;
        var result = _runner.TryStart(bookId, AgentRole.IllustrationPlanner, chapterId,
            (o, c) => o.StartIllustrationPlanningAsync(bookId, request, c));
        return result == RunStartResult.Success ? Accepted() : Conflict(new { message = result.ToMessage() });
    }

    [HttpPost("render")]
    public async Task<IActionResult> Render(int bookId, [FromBody] RenderIllustrationsRequest req)
    {
        var (denied, book) = await this.RequireBookOwnershipAndLoadAsync(bookId, _repo);
        if (denied is not null) return denied;
        var config = await _repo.GetLlmConfigAsync(bookId, book!.UserId);
        if (config is null || !config.HasImageGeneration())
            return BadRequest(new { message = "No image endpoint is configured. Set one in the LLM settings or apply a preset that has one." });
        if (req.Scope is RenderScope.Chapter or RenderScope.Character or RenderScope.Single && req.TargetId is null)
            return BadRequest(new { message = "This render scope needs a target id." });

        var request = new RenderRequest(req.Scope, req.TargetId, req.Reroll);
        var result = _runner.TryStart(bookId, AgentRole.Illustrator, null,
            (o, c) => o.StartRenderAsync(bookId, request, c));
        return result == RunStartResult.Success ? Accepted() : Conflict(new { message = result.ToMessage() });
    }

    [HttpGet("~/api/books/{bookId:int}/images/{imageId:int}")]
    public async Task<IActionResult> Image(int bookId, int imageId)
    {
        var denied = await this.RequireBookOwnershipAsync(bookId, _repo);
        if (denied is not null) return denied;
        var image = await _repo.GetBookImageAsync(bookId, imageId);
        if (image is null) return NotFound();
        // A re-render stores a new image row, so an image id's bytes never change.
        Response.Headers.CacheControl = "private, max-age=31536000, immutable";
        return File(image.Data, image.ContentType);
    }

    private async Task<IllustrationDto> ToDtoAsync(int bookId, Illustration illustration)
    {
        IReadOnlyList<string>? paragraphs = null;
        if (illustration.ChapterId is int chapterId)
            paragraphs = ParagraphAnchors.Split((await _repo.GetChapterAsync(bookId, chapterId))?.Content);
        return IllustrationViews.ToDto(illustration, paragraphs);
    }

    /// <summary>Anchors the illustration to a paragraph by quoting its opening words, so later edits don't move it.</summary>
    private static void PlaceAfter(Illustration illustration, IReadOnlyList<string> paragraphs, int paragraphNumber)
    {
        if (paragraphs.Count == 0)
        {
            illustration.ParagraphNumber = 1;
            illustration.AnchorText = "";
            return;
        }
        var index = Math.Clamp(paragraphNumber - 1, 0, paragraphs.Count - 1);
        illustration.ParagraphNumber = index + 1;
        illustration.AnchorText = OpeningWords(paragraphs[index]);
    }

    private static string OpeningWords(string paragraph)
    {
        var words = paragraph.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Take(10));
    }

    private static int? NullIfZero(int? id) => id is > 0 ? id : null;

    private static void Apply(Illustration i, IllustrationRequest req)
    {
        if (req.Caption is not null) i.Caption = req.Caption;
        if (req.Prompt is not null) i.Prompt = req.Prompt;
        if (req.NegativePrompt is not null) i.NegativePrompt = req.NegativePrompt;
        if (req.Orientation is { } o) i.Orientation = o;
        if (req.FocusCharacterId.HasValue) i.FocusCharacterId = NullIfZero(req.FocusCharacterId);
        if (req.Characters is not null) i.CharacterNamesJson = JsonSerializer.Serialize(req.Characters);
    }
}

/// <param name="FocusCharacterId">0 clears the focus character; null leaves it unchanged.</param>
public record IllustrationRequest(
    IllustrationKind Kind = IllustrationKind.Scene,
    int? ChapterId = null,
    int? ParagraphNumber = null,
    string? Caption = null,
    string? Prompt = null,
    string? NegativePrompt = null,
    IllustrationOrientation? Orientation = null,
    int? FocusCharacterId = null,
    List<string>? Characters = null);

public record CharacterVisualRequest(string? VisualPrompt);

public record StyleSheetRequest(string? Style, string? PortraitFraming, string? NegativePrompt);

public record PlanIllustrationsRequest(IllustrationPlanScope Scope = IllustrationPlanScope.Missing, int? TargetId = null);

public record RenderIllustrationsRequest(RenderScope Scope = RenderScope.All, int? TargetId = null, bool Reroll = false);
