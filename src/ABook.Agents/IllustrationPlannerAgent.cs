using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Core.Text;
using Microsoft.Extensions.Logging;

namespace ABook.Agents;

/// <summary>
/// Writes the illustration plan with the text model: each character's canonical visual description and
/// reference-portrait prompt, the scenes of a chapter (prompt, negative prompt and the paragraph each
/// follows) and the cover. Nothing is rendered here — <see cref="IllustrationRenderer"/> does that in a
/// separate, LLM-free run so the text model can be unloaded to free VRAM for the image model.
/// </summary>
public class IllustrationPlannerAgent : AgentBase
{
    public IllustrationPlannerAgent(
        IBookRepository repo,
        ILlmProviderFactory llmFactory,
        IVectorStoreService vectorStore,
        IBookNotifier notifier,
        AgentRunStateService stateService,
        ILoggerFactory loggerFactory)
        : base(repo, llmFactory, vectorStore, notifier, stateService, loggerFactory) { }

    /// <summary>
    /// Writes visual descriptions and portrait prompts for characters whose description changed since
    /// theirs was written (or who have none). <paramref name="force"/> rewrites every character's.
    /// </summary>
    public async Task EnsureCharacterVisualsAsync(int bookId, CancellationToken ct, bool force = false)
    {
        await EnsureStyleSheetAsync(bookId, ct);
        var cards = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        if (cards.Count == 0) return;

        var portraits = (await Repo.GetIllustrationsAsync(bookId, kind: IllustrationKind.Portrait))
            .Where(p => p.CharacterCardId.HasValue)
            .ToDictionary(p => p.CharacterCardId!.Value);

        var stale = cards
            .Where(c => force
                || string.IsNullOrWhiteSpace(c.VisualPrompt)
                || c.VisualPromptSourceHash != VisualSourceHash(c)
                || !portraits.ContainsKey(c.Id))
            .ToList();
        if (stale.Count == 0) return;

        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Describing how {stale.Count} character{(stale.Count == 1 ? "" : "s")} look…", false, ct);

        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var (client, config, messages) = await StartConversationAsync(book);
        var others = cards.Except(stale);
        messages.Add(new LlmChatMessage(LlmChatRole.User,
            PromptTemplates.CharacterVisualsUser(book, stale, PromptTemplates.CharacterVisuals(others))));

        var (raw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.IllustrationPlanner, JsonSchemas.CharacterVisuals, ct,
            forwardToUi: false);

        // Keep every description that finished streaming, even when the call failed part-way.
        List<CharacterVisual> visuals = [];
        Exception? parseError = null;
        try { visuals = ParseCharacterVisuals(raw); }
        catch (Exception ex) { parseError = ex; }

        var byName = stale.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
        var saved = 0;
        foreach (var v in visuals)
        {
            if (!byName.TryGetValue(v.Name, out var card)) continue;
            // Scenes already planned quote the old wording; carry the new one into them.
            await SetCharacterVisualAsync(Repo, card, v.VisualPrompt);

            var portraitPrompt = PortraitPrompt(book.IllustrationPortraitFraming, card.VisualPrompt);
            if (portraits.TryGetValue(card.Id, out var portrait))
            {
                portrait.Prompt = portraitPrompt;
                portrait.NegativePrompt = "";
                portrait.Caption = card.Name;
                portrait.FocusCharacterId = card.Id;
                portrait.CharacterNamesJson = JsonSerializer.Serialize(new[] { card.Name });
                portrait.Status = IllustrationStatus.Planned;
                portrait.Error = null;
                await Repo.UpdateIllustrationAsync(portrait);
            }
            else
            {
                await Repo.AddIllustrationAsync(new Illustration
                {
                    BookId = bookId,
                    Kind = IllustrationKind.Portrait,
                    CharacterCardId = card.Id,
                    FocusCharacterId = card.Id,
                    Caption = card.Name,
                    Prompt = portraitPrompt,
                    NegativePrompt = "",
                    Orientation = IllustrationOrientation.Portrait,
                    CharacterNamesJson = JsonSerializer.Serialize(new[] { card.Name }),
                });
            }
            saved++;
        }

        if (saved > 0) await Notifier.NotifyIllustrationsUpdatedAsync(bookId, null, ct);

        if (failure is not null) Rethrow(failure);
        if (parseError is not null) Rethrow(parseError);

        var missing = stale.Where(c => !visuals.Any(v => string.Equals(v.Name, c.Name, StringComparison.OrdinalIgnoreCase)))
            .Select(c => c.Name).ToList();
        if (missing.Count > 0)
            await ReportNoteAsync(bookId, null, AgentRole.IllustrationPlanner,
                $"No visual description came back for: {string.Join(", ", missing)}. Plan again to retry.", ct);
    }

    /// <summary>
    /// Writes the book's art direction — style, the portrait composition shared by the whole cast, and a
    /// book-wide negative prompt — when there is none or the configured visual style changed since it was
    /// written. <paramref name="force"/> rewrites it. It is added word for word to every image at render
    /// time, which is what keeps every portrait and scene in one style.
    /// </summary>
    public async Task EnsureStyleSheetAsync(int bookId, CancellationToken ct, bool force = false)
    {
        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var config = await Repo.GetLlmConfigAsync(bookId, book.UserId);
        var brief = config?.ImageVisualStyle?.Trim() ?? "";
        if (!force && !string.IsNullOrWhiteSpace(book.IllustrationStyle) && (book.IllustrationStyleSource ?? "") == brief)
            return;

        await Notifier.NotifyWorkflowProgressAsync(bookId, "Writing the book's art direction…", false, ct);
        // Written from the brief, never from the art direction it replaces.
        var (client, llmConfig, messages) = await StartConversationAsync(book,
            styleOverride: brief.Length > 0 ? brief : PromptTemplates.VisualStyleUnset);
        var bible = await Repo.GetStoryBibleAsync(bookId);
        messages.Add(new LlmChatMessage(LlmChatRole.User,
            PromptTemplates.StyleSheetUser(book, bible, brief.Length > 0 ? brief : PromptTemplates.VisualStyleUnset)));

        var (raw, failure) = await StreamJsonResponseAsync(
            client, llmConfig, messages, bookId, null, AgentRole.IllustrationPlanner, JsonSchemas.StyleSheet, ct,
            forwardToUi: false);
        if (failure is not null) Rethrow(failure);

        using var doc = PlanningParse.OpenObject(raw, "Art direction");
        var style = LenientJson.Text(doc.RootElement, "style").Trim();
        if (style.Length == 0)
            throw new FormatException($"Art direction response contained no style. {PlanningParse.Describe(raw)}");
        await ApplyStyleSheetAsync(Repo, book, style,
            LenientJson.Text(doc.RootElement, "portraitFraming").Trim(),
            LenientJson.Text(doc.RootElement, "negativePrompt").Trim(),
            brief);
        await Notifier.NotifyIllustrationsUpdatedAsync(bookId, null, ct);
    }

    /// <summary>
    /// Stores the art direction, rebuilds every portrait prompt from the shared composition, and — when
    /// anything changed — marks rendered images for re-rendering. Returns how many were marked.
    /// </summary>
    public static async Task<int> ApplyStyleSheetAsync(
        IBookRepository repo, Book book, string style, string portraitFraming, string negativePrompt, string? source = null)
    {
        var changed = (book.IllustrationStyle ?? "") != style
            || (book.IllustrationPortraitFraming ?? "") != portraitFraming
            || (book.IllustrationNegativePrompt ?? "") != negativePrompt;
        book.IllustrationStyle = style;
        book.IllustrationPortraitFraming = portraitFraming;
        book.IllustrationNegativePrompt = negativePrompt;
        if (source is not null) book.IllustrationStyleSource = source;
        await repo.UpdateAsync(book);

        var cards = (await repo.GetAllCharacterCardsAsync(book.Id)).ToDictionary(c => c.Id);
        var marked = 0;
        foreach (var ill in await repo.GetIllustrationsAsync(book.Id))
        {
            var dirty = false;
            if (ill.Kind == IllustrationKind.Portrait && ill.CharacterCardId is int id && cards.TryGetValue(id, out var card)
                && !string.IsNullOrWhiteSpace(card.VisualPrompt))
            {
                var prompt = PortraitPrompt(portraitFraming, card.VisualPrompt);
                if (prompt != ill.Prompt) { ill.Prompt = prompt; dirty = true; }
            }
            if ((changed || dirty) && ill.Status == IllustrationStatus.Rendered)
            {
                ill.Status = IllustrationStatus.Planned;
                marked++;
                dirty = true;
            }
            if (dirty) await repo.UpdateIllustrationAsync(ill);
        }
        return marked;
    }

    /// <summary>A reference portrait: the composition shared by the whole cast, then the character's appearance.</summary>
    public static string PortraitPrompt(string? framing, string visualPrompt) =>
        string.IsNullOrWhiteSpace(framing) ? visualPrompt.Trim() : $"{framing.Trim().TrimEnd('.')}. {visualPrompt.Trim()}";

    /// <summary>
    /// Plans the scenes of one chapter, replacing its never-rendered illustrations. Illustrations that
    /// already have an image are kept.
    /// </summary>
    public async Task<IReadOnlyList<Illustration>> PlanChapterAsync(int bookId, int chapterId, CancellationToken ct)
    {
        var chapter = EnsureNotArchived(await Repo.GetChapterAsync(bookId, chapterId)
            ?? throw new InvalidOperationException($"Chapter {chapterId} not found."));
        if (string.IsNullOrWhiteSpace(chapter.Content))
            throw new InvalidOperationException($"Chapter {chapter.Number} has no text to illustrate yet.");

        await EnsureCharacterVisualsAsync(bookId, ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, $"Planning illustrations for Chapter {chapter.Number}…", false, ct);

        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var cards = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        var paragraphs = ParagraphAnchors.Split(chapter.Content);
        var others = await OtherScenesAsync(bookId, chapterId);

        var (client, config, messages) = await StartConversationAsync(book);
        messages.Add(new LlmChatMessage(LlmChatRole.User, PromptTemplates.ChapterIllustrationsUser(
            book, chapter, paragraphs, PromptTemplates.CharacterVisuals(cards), others)));

        // Each illustration is saved the moment its JSON element finishes streaming, so the author sees
        // them appear one by one and an interrupted call keeps everything that arrived.
        var persisted = new List<Illustration>();
        async Task PersistNewAsync(string partial)
        {
            var parsed = ParseChapterIllustrations(partial, paragraphs, cards);
            if (parsed.Count <= persisted.Count) return;
            var fresh = parsed.Skip(persisted.Count).ToList();
            if (persisted.Count == 0)
            {
                // The first new element replaces the chapter's old unrendered plan; until then it stays intact.
                persisted.AddRange(await Repo.ReplaceUnrenderedIllustrationsAsync(bookId, chapterId, IllustrationKind.Scene, fresh));
            }
            else
            {
                foreach (var ill in fresh)
                {
                    ill.BookId = bookId;
                    ill.ChapterId = chapterId;
                    persisted.Add(await Repo.AddIllustrationAsync(ill));
                }
            }
            await Notifier.NotifyIllustrationsUpdatedAsync(bookId, null, CancellationToken.None);
        }

        var (raw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, chapterId, AgentRole.IllustrationPlanner, JsonSchemas.ChapterIllustrations, ct,
            forwardToUi: false, onElementClosed: PersistNewAsync);

        // Pick up anything the last chunk completed (and, on failure, whatever finished before it).
        await PersistNewAsync(raw);

        if (failure is not null)
        {
            if (persisted.Count > 0)
                await ReportNoteAsync(bookId, chapterId, AgentRole.IllustrationPlanner,
                    $"Planning Chapter {chapter.Number}'s illustrations was interrupted — kept the {persisted.Count} " +
                    $"illustration{(persisted.Count == 1 ? "" : "s")} planned before it stopped.", CancellationToken.None);
            Rethrow(failure);
        }

        // A plan of no illustrations still clears the chapter's old unrendered ones.
        if (persisted.Count == 0)
            await Repo.ReplaceUnrenderedIllustrationsAsync(bookId, chapterId, IllustrationKind.Scene, []);

        await Notifier.NotifyIllustrationsUpdatedAsync(bookId, null, ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId,
            $"Chapter {chapter.Number}: {persisted.Count} illustration{(persisted.Count == 1 ? "" : "s")} planned.", false, ct);
        return persisted;
    }

    /// <summary>Plans the cover. An existing cover keeps its image until it is re-rendered.</summary>
    public async Task<Illustration> PlanCoverAsync(int bookId, CancellationToken ct)
    {
        await EnsureCharacterVisualsAsync(bookId, ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Planning the cover…", false, ct);

        var book = await Repo.GetByIdAsync(bookId) ?? throw new InvalidOperationException($"Book {bookId} not found.");
        var bible = await Repo.GetStoryBibleAsync(bookId);
        var cards = (await Repo.GetCharacterCardsAsync(bookId)).ToList();
        var synopses = await BuildChapterSynopsesAsync(bookId, ct: ct);

        var (client, config, messages) = await StartConversationAsync(book);
        messages.Add(new LlmChatMessage(LlmChatRole.User,
            PromptTemplates.CoverUser(book, bible, synopses, PromptTemplates.CharacterVisuals(cards))));

        var (raw, failure) = await StreamJsonResponseAsync(
            client, config, messages, bookId, null, AgentRole.IllustrationPlanner, JsonSchemas.Cover, ct,
            forwardToUi: false);
        if (failure is not null) Rethrow(failure);

        var plan = ParseCover(raw, cards);
        var existing = (await Repo.GetIllustrationsAsync(bookId, kind: IllustrationKind.Cover)).FirstOrDefault();
        Illustration cover;
        if (existing is not null)
        {
            existing.Prompt = plan.Prompt;
            existing.NegativePrompt = plan.NegativePrompt;
            existing.Orientation = plan.Orientation;
            existing.FocusCharacterId = plan.FocusCharacterId;
            existing.CharacterNamesJson = plan.CharacterNamesJson;
            existing.Caption = plan.Caption;
            existing.Status = IllustrationStatus.Planned;
            existing.Error = null;
            await Repo.UpdateIllustrationAsync(existing);
            cover = existing;
        }
        else
        {
            plan.BookId = bookId;
            cover = await Repo.AddIllustrationAsync(plan);
        }

        await Notifier.NotifyIllustrationsUpdatedAsync(bookId, cover.Id, ct);
        await Notifier.NotifyWorkflowProgressAsync(bookId, "Cover planned.", false, ct);
        return cover;
    }

    /// <summary>
    /// Plans every finished chapter that has no illustrations yet and, once all chapters are finished,
    /// the cover if it has none. Chapters are skipped rather than failing the run when one plan fails.
    /// </summary>
    public async Task PlanMissingAsync(int bookId, CancellationToken ct)
    {
        await EnsureCharacterVisualsAsync(bookId, ct);

        var chapters = (await Repo.GetChaptersAsync(bookId)).OrderBy(c => c.Number).ToList();
        var illustrated = (await Repo.GetIllustrationsAsync(bookId, kind: IllustrationKind.Scene))
            .Select(i => i.ChapterId).ToHashSet();

        foreach (var chapter in chapters)
        {
            ct.ThrowIfCancellationRequested();
            if (chapter.Status != ChapterStatus.Done || string.IsNullOrWhiteSpace(chapter.Content)) continue;
            if (illustrated.Contains(chapter.Id)) continue;
            await PlanChapterOrNoteAsync(bookId, chapter, ct);
        }

        var allDone = chapters.Count > 0 && chapters.All(c => c.Status == ChapterStatus.Done);
        var hasCover = (await Repo.GetIllustrationsAsync(bookId, kind: IllustrationKind.Cover)).Count > 0;
        if (allDone && !hasCover)
            await PlanCoverAsync(bookId, ct);
    }

    /// <summary>
    /// Plans one chapter as a workflow side-step: a failure is recorded as a note and never fails the
    /// run, because the chapter itself is finished.
    /// </summary>
    public async Task PlanChapterOrNoteAsync(int bookId, Chapter chapter, CancellationToken ct)
    {
        try
        {
            await PlanChapterAsync(bookId, chapter.Id, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Illustration planning failed for chapter {Number}.", bookId, chapter.Number);
            await ReportNoteAsync(bookId, chapter.Id, AgentRole.IllustrationPlanner,
                $"Illustrations for Chapter {chapter.Number} could not be planned: {ex.Message} " +
                "Use Plan illustrations on the chapter to retry.", ct);
        }
    }

    /// <inheritdoc cref="PlanChapterOrNoteAsync"/>
    public async Task PlanCoverOrNoteAsync(int bookId, CancellationToken ct)
    {
        try
        {
            await PlanCoverAsync(bookId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "[Book {BookId}] Cover planning failed.", bookId);
            await ReportNoteAsync(bookId, null, AgentRole.IllustrationPlanner,
                $"The cover could not be planned: {ex.Message} Use Plan cover on the Illustrations page to retry.", ct);
        }
    }

    private async Task<(ILlmChatClient Client, LlmConfiguration Config, List<LlmChatMessage> Messages)> StartConversationAsync(
        Book book, string? styleOverride = null)
    {
        var (client, config) = await GetChatClientAsync(book.Id);
        var bible = await Repo.GetStoryBibleAsync(book.Id) ?? new StoryBible();
        // The written art direction once it exists; before that, the author's brief.
        var style = styleOverride is not null ? styleOverride
            : !string.IsNullOrWhiteSpace(book.IllustrationStyle) ? book.IllustrationStyle.Trim()
            : !string.IsNullOrWhiteSpace(config.ImageVisualStyle) ? config.ImageVisualStyle.Trim()
            : PromptTemplates.VisualStyleUnset;
        var template = !string.IsNullOrWhiteSpace(book.IllustratorSystemPrompt)
            ? book.IllustratorSystemPrompt
            : DefaultPrompts.Illustrator;
        var cfg = config.ImageCfg is > 0 ? config.ImageCfg.Value : ImageGenDefaults.Cfg;
        var system = InterpolateSystemPrompt(template, book, bible, visualStyle: style,
            negativePrompts: PromptTemplates.NegativePromptSupport(cfg));
        return (client, config, [new LlmChatMessage(LlmChatRole.System, system)]);
    }

    private async Task<string> OtherScenesAsync(int bookId, int chapterId)
    {
        var chapters = (await Repo.GetChaptersAsync(bookId)).ToDictionary(c => c.Id, c => c.Number);
        var scenes = (await Repo.GetIllustrationsAsync(bookId, kind: IllustrationKind.Scene))
            .Where(i => i.ChapterId is int id && id != chapterId && chapters.ContainsKey(id))
            .Select(i => (chapters[i.ChapterId!.Value], string.IsNullOrWhiteSpace(i.Caption) ? i.AnchorText : i.Caption))
            .OrderBy(x => x.Item1);
        return PromptTemplates.OtherIllustrations(scenes);
    }

    /// <summary>
    /// Stores a character's canonical visual description and replaces the old wording in every prompt that
    /// quotes it (the planner copies it word for word), marking those illustrations for re-rendering.
    /// Returns how many illustrations were updated.
    /// </summary>
    public static async Task<int> SetCharacterVisualAsync(IBookRepository repo, CharacterCard card, string visualPrompt)
    {
        var old = card.VisualPrompt?.Trim() ?? "";
        card.VisualPrompt = visualPrompt.Trim();
        card.VisualPromptSourceHash = VisualSourceHash(card);
        card.ImageSeed ??= DeriveSeed(card.BookId, card.Name.Trim().ToLowerInvariant());
        await repo.UpdateCharacterCardAsync(card);

        if (old.Length == 0 || old == card.VisualPrompt) return 0;
        var updated = 0;
        foreach (var ill in await repo.GetIllustrationsAsync(card.BookId))
        {
            if (!ill.Prompt.Contains(old, StringComparison.Ordinal)) continue;
            ill.Prompt = ill.Prompt.Replace(old, card.VisualPrompt, StringComparison.Ordinal);
            if (ill.Status == IllustrationStatus.Rendered) ill.Status = IllustrationStatus.Planned;
            await repo.UpdateIllustrationAsync(ill);
            updated++;
        }
        return updated;
    }

    // ── Parsing ───────────────────────────────────────────────────────────────

    internal sealed record CharacterVisual(string Name, string VisualPrompt);

    internal static List<CharacterVisual> ParseCharacterVisuals(string raw)
    {
        using var doc = PlanningParse.OpenArray(raw, "Character visuals");
        var result = new List<CharacterVisual>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var name = LenientJson.Text(el, "name").Trim();
            var visual = LenientJson.Text(el, "visualPrompt").Trim();
            if (name.Length == 0 || visual.Length == 0) continue;
            result.Add(new CharacterVisual(name, visual));
        }
        if (result.Count == 0)
            throw new FormatException($"Character visuals response contained no usable entry. {PlanningParse.Describe(raw)}");
        return result;
    }

    /// <summary>
    /// Parses the scenes of a chapter plan. Accepts a response cut off mid-stream: every element that
    /// finished is returned and the incomplete tail is ignored. The illustrations array may be wrapped in
    /// an object (the schema) or bare.
    /// </summary>
    internal static List<Illustration> ParseChapterIllustrations(
        string raw, IReadOnlyList<string> paragraphs, IReadOnlyList<CharacterCard> cards)
    {
        var result = new List<Illustration>();
        var json = PartialJson.SalvageArray(raw);
        if (json.Length == 0) return result;
        using var doc = JsonDocument.Parse(json);
        var items = doc.RootElement;

        var order = 0;
        foreach (var el in items.EnumerateArray())
        {
            var prompt = LenientJson.Text(el, "prompt").Trim();
            if (prompt.Length == 0) continue;
            var anchor = LenientJson.Text(el, "anchorText").Trim();
            var hint = LenientJson.Int(el, "paragraphNumber") ?? paragraphs.Count;
            var index = ParagraphAnchors.Resolve(paragraphs, anchor, hint);
            var names = ReadNames(el);
            result.Add(new Illustration
            {
                Kind = IllustrationKind.Scene,
                ParagraphNumber = index + 1,
                AnchorText = anchor,
                Caption = LenientJson.Text(el, "caption").Trim(),
                Prompt = prompt,
                NegativePrompt = LenientJson.Text(el, "negativePrompt").Trim(),
                Orientation = ParseOrientation(LenientJson.Text(el, "orientation"), IllustrationOrientation.Landscape),
                FocusCharacterId = FindCharacter(cards, LenientJson.Text(el, "focusCharacter"))?.Id
                    ?? (names.Count == 1 ? FindCharacter(cards, names[0])?.Id : null),
                CharacterNamesJson = JsonSerializer.Serialize(names),
                SortOrder = order++,
            });
        }
        return result;
    }

    internal static Illustration ParseCover(string raw, IReadOnlyList<CharacterCard> cards)
    {
        using var doc = PlanningParse.OpenObject(raw, "Cover");
        var el = doc.RootElement;
        var prompt = LenientJson.Text(el, "prompt").Trim();
        if (prompt.Length == 0)
            throw new FormatException($"Cover response contained no prompt. {PlanningParse.Describe(raw)}");
        var names = ReadNames(el);
        return new Illustration
        {
            Kind = IllustrationKind.Cover,
            Caption = "Cover",
            Prompt = prompt,
            NegativePrompt = LenientJson.Text(el, "negativePrompt").Trim(),
            Orientation = ParseOrientation(LenientJson.Text(el, "orientation"), IllustrationOrientation.Portrait),
            FocusCharacterId = FindCharacter(cards, LenientJson.Text(el, "focusCharacter"))?.Id,
            CharacterNamesJson = JsonSerializer.Serialize(names),
        };
    }

    private static List<string> ReadNames(JsonElement el) =>
        el.TryGetProperty("characters", out var c) && c.ValueKind == JsonValueKind.Array
            ? c.EnumerateArray().Select(LenientJson.Text).Select(s => s.Trim()).Where(s => s.Length > 0).ToList()
            : [];

    internal static IllustrationOrientation ParseOrientation(string? value, IllustrationOrientation fallback) =>
        Enum.TryParse<IllustrationOrientation>(value?.Trim(), ignoreCase: true, out var o) ? o : fallback;

    /// <summary>Matches a character by exact name, then by the name's first word (models often use first names).</summary>
    internal static CharacterCard? FindCharacter(IReadOnlyList<CharacterCard> cards, string? name)
    {
        name = name?.Trim();
        if (string.IsNullOrEmpty(name)) return null;
        return cards.FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? cards.FirstOrDefault(c => c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault()?.Equals(name, StringComparison.OrdinalIgnoreCase) == true)
            ?? cards.FirstOrDefault(c => name.Contains(c.Name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Hash of the fields a visual description is written from; a change means it must be rewritten.</summary>
    public static string VisualSourceHash(CharacterCard card)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"{card.Name}\n{card.PhysicalDescription}"));
        return Convert.ToHexString(bytes);
    }

    /// <summary>
    /// Deterministic seed in the image server's range (0..2^31). Stable across restarts and — keyed by
    /// name — across a re-run of the Characters phase, which recreates the cards.
    /// </summary>
    public static long DeriveSeed(int bookId, string key)
    {
        const uint offset = 2166136261, prime = 16777619;
        var hash = offset;
        foreach (var b in Encoding.UTF8.GetBytes($"{bookId}|{key}"))
        {
            hash ^= b;
            hash *= prime;
        }
        return hash & 0x7FFFFFFF;
    }
}
