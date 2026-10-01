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
/// MCP tools for book illustrations. Planning (LLM) and rendering (image model) are separate runs so
/// the text model can be unloaded before the image model loads.
/// </summary>
public class IllustrationMcpTools : McpToolBase
{
    private readonly IBookRepository _repo;
    private readonly AgentRunnerService _runner;
    private readonly IBookNotifier _notifier;

    public IllustrationMcpTools(IBookRepository repo, AgentRunnerService runner, IBookNotifier notifier, IHttpContextAccessor http)
        : base(http)
    {
        _repo = repo;
        _runner = runner;
        _notifier = notifier;
    }

    [McpServerTool(Name = "list_illustrations", ReadOnly = true)]
    [Description("List a book's illustrations: character portraits, the cover and chapter scenes, with their prompts, the paragraph each follows (paragraphIndex, 0-based), status (Planned = needs rendering, Rendered, Failed) and imageId.")]
    public async Task<string> ListIllustrations(
        [Description("The book ID.")] int bookId,
        [Description("Optional chapter ID to list only that chapter's scenes.")] int? chapterId = null)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);
        return JsonSerializer.Serialize(await IllustrationViews.LoadAsync(_repo, bookId, chapterId), JsonOptions);
    }

    [McpServerTool(Name = "update_illustration")]
    [Description("Edit a planned illustration. Changing the prompt, negative prompt or orientation marks a rendered illustration for re-rendering. Only provide fields you want to change.")]
    public async Task<string> UpdateIllustration(
        [Description("The book ID.")] int bookId,
        [Description("The illustration ID (from list_illustrations).")] int illustrationId,
        [Description("New image prompt.")] string? prompt = null,
        [Description("New negative prompt.")] string? negativePrompt = null,
        [Description("New caption.")] string? caption = null,
        [Description("New orientation: Portrait, Landscape or Square.")] string? orientation = null)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);
        var ill = await _repo.GetIllustrationAsync(bookId, illustrationId)
            ?? throw new McpException($"Illustration {illustrationId} not found.");

        var changed = false;
        if (prompt is not null && prompt != ill.Prompt) { ill.Prompt = prompt; changed = true; }
        if (negativePrompt is not null && negativePrompt != ill.NegativePrompt) { ill.NegativePrompt = negativePrompt; changed = true; }
        if (orientation is not null)
        {
            if (!Enum.TryParse<IllustrationOrientation>(orientation, true, out var o))
                throw new McpException("Orientation must be Portrait, Landscape or Square.");
            if (o != ill.Orientation) { ill.Orientation = o; changed = true; }
        }
        if (caption is not null) ill.Caption = caption;
        if (changed && ill.Status == IllustrationStatus.Rendered) ill.Status = IllustrationStatus.Planned;

        await _repo.UpdateIllustrationAsync(ill);
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, ill.Id);
        return JsonSerializer.Serialize(IllustrationViews.ToDto(ill, null), JsonOptions);
    }

    [McpServerTool(Name = "list_visual_assets", ReadOnly = true)]
    [Description("List a book's recurring visual assets — props, outfits (including each character's usual outfit), places, creatures and vehicles — with the canonical description the illustration planner repeats word for word in every prompt that shows them, which keeps them identical from image to image.")]
    public async Task<string> ListVisualAssets([Description("The book ID.")] int bookId)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);
        return JsonSerializer.Serialize(await _repo.GetVisualAssetsAsync(bookId), JsonOptions);
    }

    [McpServerTool(Name = "update_visual_asset")]
    [Description("Edit a recurring visual asset. A new description replaces the old wording in every illustration prompt that quotes it and marks those illustrations for re-rendering. Only provide fields you want to change.")]
    public async Task<string> UpdateVisualAsset(
        [Description("The book ID.")] int bookId,
        [Description("The asset ID (from list_visual_assets).")] int assetId,
        [Description("New canonical description (English prose; outfits start with 'wearing').")] string? description = null,
        [Description("New name, unique within the book.")] string? name = null,
        [Description("New kind: Object, Outfit, Location, Creature, Vehicle or Other.")] string? kind = null)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);
        var asset = await _repo.GetVisualAssetAsync(bookId, assetId)
            ?? throw new McpException($"Visual asset {assetId} not found.");
        if (!string.IsNullOrWhiteSpace(name) && name.Trim() != asset.Name)
        {
            if ((await _repo.GetVisualAssetsAsync(bookId)).Any(a => a.Id != assetId && string.Equals(a.Name, name.Trim(), StringComparison.OrdinalIgnoreCase)))
                throw new McpException($"There is already an asset named \"{name.Trim()}\".");
            asset.Name = name.Trim();
        }
        if (kind is not null)
        {
            if (!Enum.TryParse<VisualAssetKind>(kind, true, out var k))
                throw new McpException("Kind must be Object, Outfit, Location, Creature, Vehicle or Other.");
            asset.Kind = k;
        }
        var updated = description is not null
            ? await IllustrationPlannerAgent.SetVisualAssetAsync(_repo, asset, description)
            : 0;
        if (description is null) await _repo.UpdateVisualAssetAsync(asset);
        await _notifier.NotifyIllustrationsUpdatedAsync(bookId, null);
        return JsonSerializer.Serialize(new { asset, updatedIllustrations = updated }, JsonOptions);
    }

    [McpServerTool(Name = "plan_illustrations")]
    [Description("Start an LLM run that writes illustration prompts and positions (nothing is rendered). Scopes: Missing (finished chapters without illustrations, then the cover once all chapters are finished), Chapter (re-plan one chapter; pass chapterId), Cover, Characters (rewrite every character's visual description and portrait prompt), Style (rewrite the book's art direction — the style, portrait composition and negative prompt added to every image).")]
    public async Task<string> PlanIllustrations(
        [Description("The book ID.")] int bookId,
        [Description("Missing, Chapter, Cover, Characters or Style.")] string scope = "Missing",
        [Description("Chapter ID, for the Chapter scope.")] int? chapterId = null)
    {
        await EnsureBookOwnershipAsync(bookId, _repo);
        if (!Enum.TryParse<IllustrationPlanScope>(scope, true, out var s))
            throw new McpException("Scope must be Missing, Chapter, Cover, Characters or Style.");
        if (s == IllustrationPlanScope.Chapter)
        {
            var chapter = chapterId is int id ? await _repo.GetChapterAsync(bookId, id) : null;
            if (chapter is null) throw new McpException("The Chapter scope needs an existing chapterId.");
            if (chapter.IsArchived) throw new McpException($"Chapter {chapter.Id} is archived. Restore it first.");
        }

        var request = new IllustrationPlanRequest(s, chapterId);
        var result = _runner.TryStart(bookId, AgentRole.IllustrationPlanner, s == IllustrationPlanScope.Chapter ? chapterId : null,
            (o, c) => o.StartIllustrationPlanningAsync(bookId, request, c));
        if (result != RunStartResult.Success) throw new McpException(result.ToMessage());
        return JsonSerializer.Serialize(new { started = true, bookId, scope = s.ToString(), chapterId }, JsonOptions);
    }

    [McpServerTool(Name = "render_illustrations")]
    [Description("Start an image-model run that renders planned illustrations (no LLM calls — unload the LLM first if VRAM is short, or enable unloadLlmBeforeRender). Scopes: All (everything not yet rendered: portraits, cover, scenes), Portraits, Chapter (targetId = chapter ID), Character (re-render a character's portrait and the scenes centred on them; targetId = character ID), Single (targetId = illustration ID).")]
    public async Task<string> RenderIllustrations(
        [Description("The book ID.")] int bookId,
        [Description("All, Portraits, Chapter, Character or Single.")] string scope = "All",
        [Description("Chapter, character or illustration ID, depending on the scope.")] int? targetId = null,
        [Description("Single scope only: render with a new random seed. A re-rolled portrait's seed becomes the character's.")] bool reroll = false)
    {
        var book = await GetOwnedBookAsync(bookId, _repo);
        if (!Enum.TryParse<RenderScope>(scope, true, out var s))
            throw new McpException("Scope must be All, Portraits, Chapter, Character or Single.");
        if (s is RenderScope.Chapter or RenderScope.Character or RenderScope.Single && targetId is null)
            throw new McpException($"The {s} scope needs a targetId.");
        var config = await _repo.GetLlmConfigAsync(bookId, book.UserId);
        if (config is null || !config.HasImageGeneration())
            throw new McpException("No image endpoint is configured — set imageEndpoint with set_llm_config or apply a preset that has one.");

        var request = new RenderRequest(s, targetId, reroll);
        var result = _runner.TryStart(bookId, AgentRole.Illustrator, null, (o, c) => o.StartRenderAsync(bookId, request, c));
        if (result != RunStartResult.Success) throw new McpException(result.ToMessage());
        return JsonSerializer.Serialize(new { started = true, bookId, scope = s.ToString(), targetId, reroll }, JsonOptions);
    }
}
