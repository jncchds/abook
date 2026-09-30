using System.Text.Json;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Core.Text;

namespace ABook.Api.Services;

/// <summary>An illustration as the UI sees it, with its paragraph resolved against the current chapter text.</summary>
public record IllustrationDto(
    int Id,
    int? ChapterId,
    IllustrationKind Kind,
    int? CharacterCardId,
    int ParagraphNumber,
    /// <summary>0-based paragraph the image follows in the current text; -1 for covers and portraits.</summary>
    int ParagraphIndex,
    string AnchorText,
    string Caption,
    string Prompt,
    string NegativePrompt,
    IllustrationOrientation Orientation,
    int? FocusCharacterId,
    IReadOnlyList<string> Characters,
    long? Seed,
    IllustrationStatus Status,
    string? Error,
    int? ImageId,
    DateTime UpdatedAt);

public static class IllustrationViews
{
    /// <summary>
    /// Loads the book's illustrations and resolves each scene's paragraph. With <paramref name="publicView"/>
    /// only illustrations that have an image are returned, and prompts are left out.
    /// </summary>
    public static async Task<IReadOnlyList<IllustrationDto>> LoadAsync(
        IBookRepository repo, int bookId, int? chapterId = null, IllustrationKind? kind = null, bool publicView = false)
    {
        var items = await repo.GetIllustrationsAsync(bookId, chapterId, kind);
        if (publicView) items = items.Where(i => i.ImageId.HasValue).ToList();

        var chapterIds = items.Where(i => i.ChapterId.HasValue).Select(i => i.ChapterId!.Value).ToHashSet();
        var paragraphs = new Dictionary<int, IReadOnlyList<string>>();
        if (chapterIds.Count > 0)
            foreach (var ch in await repo.GetChaptersAsync(bookId, includeArchived: true))
                if (chapterIds.Contains(ch.Id))
                    paragraphs[ch.Id] = ParagraphAnchors.Split(ch.Content);

        return items.Select(i => ToDto(i,
            i.ChapterId is int id && paragraphs.TryGetValue(id, out var p) ? p : null, publicView)).ToList();
    }

    public static IllustrationDto ToDto(Illustration i, IReadOnlyList<string>? paragraphs, bool publicView = false) => new(
        i.Id, i.ChapterId, i.Kind, i.CharacterCardId, i.ParagraphNumber,
        paragraphs is null ? -1 : ParagraphAnchors.Resolve(paragraphs, i.AnchorText, i.ParagraphNumber),
        i.AnchorText, i.Caption,
        publicView ? "" : i.Prompt,
        publicView ? "" : i.NegativePrompt,
        i.Orientation, i.FocusCharacterId, ReadNames(i.CharacterNamesJson),
        publicView ? null : i.Seed,
        i.Status, publicView ? null : i.Error, i.ImageId, i.UpdatedAt);

    private static IReadOnlyList<string> ReadNames(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<string>>(json) ?? []; }
        catch (JsonException) { return []; }
    }
}
