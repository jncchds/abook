using System.Text.Json.Serialization;

namespace ABook.Core.Models;

/// <summary>
/// One planned or rendered image: a scene placed after a chapter paragraph, the book cover, or a
/// character's reference portrait. The prompt text is written by the illustration planner (LLM);
/// rendering is a separate, LLM-free step so the text model can be unloaded to free VRAM.
/// </summary>
public class Illustration
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public int? ChapterId { get; set; }
    public IllustrationKind Kind { get; set; } = IllustrationKind.Scene;
    /// <summary>The character a Portrait belongs to.</summary>
    public int? CharacterCardId { get; set; }

    /// <summary>1-indexed paragraph the image follows — a hint used when <see cref="AnchorText"/> can't be found.</summary>
    public int ParagraphNumber { get; set; }
    /// <summary>A short verbatim quote from the paragraph the image follows; survives edits that shift paragraphs.</summary>
    public string AnchorText { get; set; } = string.Empty;

    public string Caption { get; set; } = string.Empty;
    public string Prompt { get; set; } = string.Empty;
    public string NegativePrompt { get; set; } = string.Empty;
    public IllustrationOrientation Orientation { get; set; } = IllustrationOrientation.Landscape;

    /// <summary>The character whose seed the render uses, keeping their face consistent across scenes.</summary>
    public int? FocusCharacterId { get; set; }
    public string CharacterNamesJson { get; set; } = "[]";

    /// <summary>
    /// Seed override set by a re-roll. When null the render uses the focus character's seed (or one
    /// derived from the illustration), so a portrait re-roll carries over to that character's scenes.
    /// </summary>
    public long? Seed { get; set; }
    /// <summary>
    /// Planned = needs a render (possibly re-render: an edited illustration keeps showing its previous
    /// image until then). Rendered = the image matches the prompt. Failed = the last render failed.
    /// </summary>
    public IllustrationStatus Status { get; set; } = IllustrationStatus.Planned;
    public string? Error { get; set; }
    public int? ImageId { get; set; }

    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
    [JsonIgnore] public Chapter? Chapter { get; set; }
    [JsonIgnore] public BookImage? Image { get; set; }
}

/// <summary>Rendered image bytes, kept apart from <see cref="Illustration"/> so listings never load them.</summary>
public class BookImage
{
    public int Id { get; set; }
    public int BookId { get; set; }
    public byte[] Data { get; set; } = [];
    public string ContentType { get; set; } = "image/png";
    public int Width { get; set; }
    public int Height { get; set; }
    public long Seed { get; set; }
    public DateTime CreatedAt { get; set; }

    [JsonIgnore] public Book Book { get; set; } = null!;
}
