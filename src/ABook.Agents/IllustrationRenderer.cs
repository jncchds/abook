using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Renders planned illustrations with the image model. Makes no LLM calls, so the text model can be
/// unloaded first — the LLM and the image model rarely fit in VRAM together.
/// <para>
/// Consistency follows what worked in adate with Z-Image, which takes no reference images: every
/// character has one seed, used for their portrait and for every scene centred on them, and the
/// planner repeats their canonical visual description word for word in each prompt.
/// </para>
/// </summary>
public class IllustrationRenderer
{
    private readonly IBookRepository _repo;
    private readonly IImageGeneratorFactory _imageFactory;
    private readonly ILlmModelUnloader _unloader;
    private readonly IBookNotifier _notifier;
    private readonly AgentRunStateService _state;
    private readonly ILogger<IllustrationRenderer> _logger;

    public IllustrationRenderer(
        IBookRepository repo,
        IImageGeneratorFactory imageFactory,
        ILlmModelUnloader unloader,
        IBookNotifier notifier,
        AgentRunStateService state,
        ILogger<IllustrationRenderer> logger)
    {
        _repo = repo;
        _imageFactory = imageFactory;
        _unloader = unloader;
        _notifier = notifier;
        _state = state;
        _logger = logger;
    }

    public async Task RenderAsync(int bookId, RenderRequest request, CancellationToken ct)
    {
        var book = await _repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var config = await _repo.GetLlmConfigAsync(bookId, book.UserId)
            ?? throw new InvalidOperationException("No LLM configuration found.");
        if (!config.HasImageGeneration())
            throw new InvalidOperationException(
                "No image endpoint is configured. Set one in the image generation settings (LLM configuration or preset).");

        var cards = (await _repo.GetAllCharacterCardsAsync(bookId)).ToDictionary(c => c.Id);
        var targets = await SelectTargetsAsync(bookId, request, cards);
        if (targets.Count == 0)
        {
            await _notifier.NotifyWorkflowProgressAsync(bookId, "Nothing to render — every planned illustration is up to date.", false, ct);
            return;
        }

        if (config.UnloadLlmBeforeRender)
            await UnloadLlmAsync(bookId, config, ct);

        var generator = _imageFactory.Create(config.ImageProvider);
        var health = await generator.CheckHealthAsync(config, ct);
        if (!health.Ready)
            throw new InvalidOperationException(
                $"The image server at {config.ImageEndpoint} is not ready (status: {health.Status}{(health.Detail is null ? "" : $", {health.Detail}")}).");

        var chapterNumbers = (await _repo.GetChaptersAsync(bookId, includeArchived: true)).ToDictionary(c => c.Id, c => c.Number);
        var failures = 0;
        for (int i = 0; i < targets.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var ill = targets[i];
            _state.UpdateRunRole(bookId, AgentRole.Illustrator, ill.ChapterId);
            await _notifier.NotifyWorkflowProgressAsync(bookId,
                $"Rendering {i + 1}/{targets.Count}: {Describe(ill, cards, chapterNumbers)}…", false, ct);

            var reroll = request.Reroll && request.Scope == RenderScope.Single;
            var seed = reroll ? Random.Shared.NextInt64(1, int.MaxValue) : SeedFor(bookId, ill, cards);
            var (width, height) = config.ImageSize(ill.Orientation);
            try
            {
                var image = await generator.GenerateAsync(
                    new ImageGenRequest(ill.Prompt, ill.NegativePrompt, seed, width, height), config, ct);
                await _repo.SaveRenderedImageAsync(bookId, ill.Id, new BookImage
                {
                    Data = image.Data,
                    ContentType = image.ContentType,
                    Width = width,
                    Height = height,
                    Seed = image.Seed,
                });
                if (reroll) await KeepRerolledSeedAsync(bookId, ill, seed, cards);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                failures++;
                _logger.LogWarning(ex, "[Book {BookId}] Rendering illustration {IllustrationId} failed.", bookId, ill.Id);
                var fresh = await _repo.GetIllustrationAsync(bookId, ill.Id);
                if (fresh is not null)
                {
                    fresh.Status = IllustrationStatus.Failed;
                    fresh.Error = AgentBase.DescribeFailure(ex, false, config.ImageTimeoutMs);
                    await _repo.UpdateIllustrationAsync(fresh);
                }
            }
            await _notifier.NotifyIllustrationsUpdatedAsync(bookId, ill.Id, ct);
        }

        var rendered = targets.Count - failures;
        await _notifier.NotifyWorkflowProgressAsync(bookId,
            $"Rendered {rendered} of {targets.Count} illustration{(targets.Count == 1 ? "" : "s")}.", false, ct);
        if (failures > 0)
            await AgentBase.ReportAgentErrorCoreAsync(_repo, _notifier, _logger, bookId, AgentRole.Illustrator, null,
                $"{failures} illustration{(failures == 1 ? "" : "s")} failed to render — see the Illustrations page for the errors, then render again.");
    }

    private async Task<List<Illustration>> SelectTargetsAsync(int bookId, RenderRequest request, IReadOnlyDictionary<int, CharacterCard> cards)
    {
        var all = await _repo.GetIllustrationsAsync(bookId);
        var activeChapters = (await _repo.GetChaptersAsync(bookId)).OrderBy(c => c.Number).ToList();
        var chapterOrder = activeChapters.Select((c, i) => (c.Id, i)).ToDictionary(x => x.Id, x => x.i);

        bool Pending(Illustration i) => i.Status != IllustrationStatus.Rendered;
        bool Live(Illustration i) => i.Kind switch
        {
            // Archived chapters and characters take no part in generation.
            IllustrationKind.Scene => i.ChapterId is int id && chapterOrder.ContainsKey(id),
            IllustrationKind.Portrait => i.CharacterCardId is int c && cards.TryGetValue(c, out var card) && !card.IsArchived,
            _ => true,
        };
        IEnumerable<Illustration> ReadingOrder(IEnumerable<Illustration> items) => items
            .OrderBy(i => i.Kind switch { IllustrationKind.Portrait => 0, IllustrationKind.Cover => 1, _ => 2 })
            .ThenBy(i => i.ChapterId is int id && chapterOrder.TryGetValue(id, out var o) ? o : 0)
            .ThenBy(i => i.ParagraphNumber).ThenBy(i => i.SortOrder);

        var live = all.Where(Live);
        return request.Scope switch
        {
            RenderScope.All => ReadingOrder(live.Where(Pending)).ToList(),
            RenderScope.Portraits => ReadingOrder(live.Where(i => i.Kind == IllustrationKind.Portrait && Pending(i))).ToList(),
            RenderScope.Chapter => ReadingOrder(live.Where(i => i.ChapterId == request.TargetId && Pending(i))).ToList(),
            RenderScope.Character => ReadingOrder(live.Where(i =>
                i.CharacterCardId == request.TargetId
                || (i.Kind != IllustrationKind.Portrait && i.FocusCharacterId == request.TargetId))).ToList(),
            RenderScope.Single => all.Where(i => i.Id == request.TargetId).ToList(),
            _ => [],
        };
    }

    /// <summary>
    /// A re-roll override wins; otherwise a portrait or a scene centred on a character uses that
    /// character's seed, and anything else a seed derived from the illustration.
    /// </summary>
    internal static long SeedFor(int bookId, Illustration ill, IReadOnlyDictionary<int, CharacterCard> cards)
    {
        if (ill.Kind != IllustrationKind.Portrait && ill.Seed is long pinned) return pinned;
        var characterId = ill.Kind == IllustrationKind.Portrait ? ill.CharacterCardId : ill.FocusCharacterId;
        if (characterId is int id && cards.TryGetValue(id, out var card))
            return card.ImageSeed ?? IllustrationPlannerAgent.DeriveSeed(bookId, card.Name.Trim().ToLowerInvariant());
        return IllustrationPlannerAgent.DeriveSeed(bookId, $"illustration:{ill.Id}");
    }

    /// <summary>A re-rolled portrait's seed becomes the character's; a re-rolled scene pins its own.</summary>
    private async Task KeepRerolledSeedAsync(int bookId, Illustration ill, long seed, IReadOnlyDictionary<int, CharacterCard> cards)
    {
        if (ill.Kind == IllustrationKind.Portrait)
        {
            if (ill.CharacterCardId is int id && cards.TryGetValue(id, out var card))
            {
                card.ImageSeed = seed;
                await _repo.UpdateCharacterCardAsync(card);
            }
            return;
        }
        var fresh = await _repo.GetIllustrationAsync(bookId, ill.Id);
        if (fresh is null) return;
        fresh.Seed = seed;
        await _repo.UpdateIllustrationAsync(fresh);
    }

    private async Task UnloadLlmAsync(int bookId, LlmConfiguration config, CancellationToken ct)
    {
        try
        {
            if (await _unloader.UnloadAsync(config, ct))
                await _notifier.NotifyWorkflowProgressAsync(bookId, $"Unloaded {config.ModelName} from Ollama to free VRAM.", false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Book {BookId}] Could not unload the LLM before rendering.", bookId);
            await _notifier.NotifyWorkflowProgressAsync(bookId, $"Could not unload the LLM ({ex.Message}) — rendering anyway.", false, ct);
        }
    }

    private static string Describe(Illustration ill, IReadOnlyDictionary<int, CharacterCard> cards, IReadOnlyDictionary<int, int> chapterNumbers) =>
        ill.Kind switch
        {
            IllustrationKind.Cover => "cover",
            IllustrationKind.Portrait => $"portrait of {(ill.CharacterCardId is int id && cards.TryGetValue(id, out var c) ? c.Name : "a character")}",
            _ => $"Chapter {(ill.ChapterId is int ch && chapterNumbers.TryGetValue(ch, out var n) ? n : 0)}"
                 + (string.IsNullOrWhiteSpace(ill.Caption) ? "" : $" — {ill.Caption}"),
        };
}
