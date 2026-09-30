using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using ABook.Agents;
using ABook.Api.Services;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Core.Text;
using ABook.Infrastructure.Imaging;

namespace ABook.Tests;

public class ParagraphAnchorsTests
{
    private const string Text = "First paragraph here.\n\nSecond one, with “curly” quotes\nspanning two lines.\n\n   \n\nThird and last.";

    [Fact]
    public void Split_SeparatesOnBlankAndWhitespaceOnlyLines()
    {
        var paragraphs = ParagraphAnchors.Split(Text);
        Assert.Equal(3, paragraphs.Count);
        Assert.Equal("Second one, with “curly” quotes\nspanning two lines.", paragraphs[1]);
    }

    [Fact]
    public void Resolve_FindsAnchorDespiteQuoteAndWhitespaceDrift()
    {
        var paragraphs = ParagraphAnchors.Split(Text);
        Assert.Equal(1, ParagraphAnchors.Resolve(paragraphs, "with \"curly\" quotes spanning", paragraphNumberHint: 3));
    }

    [Fact]
    public void Resolve_MissingAnchor_FallsBackToClampedHint()
    {
        var paragraphs = ParagraphAnchors.Split(Text);
        Assert.Equal(2, ParagraphAnchors.Resolve(paragraphs, "not in the text", paragraphNumberHint: 99));
        Assert.Equal(0, ParagraphAnchors.Resolve(paragraphs, null, paragraphNumberHint: 0));
    }

    [Fact]
    public void Resolve_RepeatedAnchor_PicksOccurrenceNearestTheHint()
    {
        var paragraphs = ParagraphAnchors.Split("She ran.\n\nMiddle.\n\nShe ran.");
        Assert.Equal(2, ParagraphAnchors.Resolve(paragraphs, "She ran", paragraphNumberHint: 3));
        Assert.Equal(0, ParagraphAnchors.Resolve(paragraphs, "She ran", paragraphNumberHint: 1));
    }

    [Fact]
    public void Resolve_NoParagraphs_ReturnsMinusOne() =>
        Assert.Equal(-1, ParagraphAnchors.Resolve([], "x", 1));
}

public class IllustrationPlannerParseTests
{
    private static readonly List<CharacterCard> Cards =
    [
        new() { Id = 1, Name = "Anna Petrova" },
        new() { Id = 2, Name = "Boris" },
    ];

    [Fact]
    public void ParseChapterIllustrations_ResolvesAnchorAndFocusCharacter()
    {
        var paragraphs = ParagraphAnchors.Split("Morning came.\n\nAnna opened the gate and stepped into the fog.\n\nNight fell.");
        const string raw = """
            {"illustrations":[{"paragraphNumber":3,"anchorText":"opened the gate","caption":"The gate",
              "prompt":"A woman at a gate in fog","negativePrompt":"text","orientation":"Portrait",
              "focusCharacter":"Anna","characters":["Anna"]}]}
            """;

        var result = IllustrationPlannerAgent.ParseChapterIllustrations(raw, paragraphs, Cards);

        var ill = Assert.Single(result);
        Assert.Equal(2, ill.ParagraphNumber);          // anchor wins over the wrong hint
        Assert.Equal(IllustrationOrientation.Portrait, ill.Orientation);
        Assert.Equal(1, ill.FocusCharacterId);          // first-name match
        Assert.Equal("text", ill.NegativePrompt);
    }

    [Fact]
    public void ParseChapterIllustrations_EmptyListIsValid()
    {
        var result = IllustrationPlannerAgent.ParseChapterIllustrations("""{"illustrations":[]}""", ["x"], Cards);
        Assert.Empty(result);
    }

    [Fact]
    public void ParseCharacterVisuals_SkipsEntriesWithoutNameOrVisual()
    {
        const string raw = """
            [{"name":"Boris","visualPrompt":"a stocky man","portraitPrompt":"","portraitNegativePrompt":""},
             {"name":"","visualPrompt":"nobody"}]
            """;
        var v = Assert.Single(IllustrationPlannerAgent.ParseCharacterVisuals(raw));
        Assert.Equal("a stocky man", v.PortraitPrompt);   // falls back to the visual prompt
    }

    [Fact]
    public void DeriveSeed_IsStableAndInServerRange()
    {
        var a = IllustrationPlannerAgent.DeriveSeed(7, "anna");
        Assert.Equal(a, IllustrationPlannerAgent.DeriveSeed(7, "anna"));
        Assert.NotEqual(a, IllustrationPlannerAgent.DeriveSeed(8, "anna"));
        Assert.InRange(a, 0, int.MaxValue);
    }

    [Fact]
    public void SeedFor_SceneUsesFocusCharactersSeedUnlessRerolled()
    {
        var cards = new Dictionary<int, CharacterCard> { [1] = new() { Id = 1, Name = "Anna", ImageSeed = 42 } };
        var scene = new Illustration { Id = 5, Kind = IllustrationKind.Scene, FocusCharacterId = 1 };
        Assert.Equal(42, IllustrationRenderer.SeedFor(3, scene, cards));

        scene.Seed = 99;
        Assert.Equal(99, IllustrationRenderer.SeedFor(3, scene, cards));

        var portrait = new Illustration { Kind = IllustrationKind.Portrait, CharacterCardId = 1, Seed = 99 };
        Assert.Equal(42, IllustrationRenderer.SeedFor(3, portrait, cards));
    }
}

public class ZImageImageGeneratorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpRequestMessage Request, string? Body)> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            Calls.Add((request, body));
            return respond(request);
        }
    }

    private static (ZImageImageGenerator Generator, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new ZImageImageGenerator((timeout, baseAddress) => new HttpClient(handler) { BaseAddress = baseAddress }), handler);
    }

    private static readonly LlmConfiguration Settings = new() { ImageEndpoint = "http://gpu:8000", ImageSteps = 8, ImageCfg = 1.0 };

    [Fact]
    public async Task Generate_PostsSnakeCaseRequestThenFetchesImage()
    {
        var (gen, handler) = Create(req => req.RequestUri!.AbsolutePath switch
        {
            "/generate" => new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"image_id":"abc","seed":7}""", Encoding.UTF8, "application/json"),
            },
            "/images/abc" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) },
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        });

        var image = await gen.GenerateAsync(new ImageGenRequest("a cat", "blurry", 7, 768, 1152), Settings);

        Assert.Equal(Png, image.Data);
        Assert.Equal("image/png", image.ContentType);
        using var body = JsonDocument.Parse(handler.Calls[0].Body!);
        Assert.Equal("a cat", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("blurry", body.RootElement.GetProperty("negative_prompt").GetString());
        Assert.Equal(8, body.RootElement.GetProperty("num_inference_steps").GetInt32());
        Assert.Equal(1152, body.RootElement.GetProperty("height").GetInt32());
    }

    [Fact]
    public async Task Generate_ServerError_SurfacesFastApiDetail()
    {
        var (gen, _) = Create(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
        {
            Content = new StringContent("""{"detail":"Pipeline not initialized"}"""),
        });

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gen.GenerateAsync(new ImageGenRequest("x", "", 1, 64, 64), Settings));
        Assert.Contains("Pipeline not initialized", ex.Message);
    }

    [Fact]
    public async Task Generate_NonPngBytes_Throws()
    {
        var (gen, _) = Create(req => req.RequestUri!.AbsolutePath == "/generate"
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""{"image_id":"x"}""") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("<html>") });

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gen.GenerateAsync(new ImageGenRequest("x", "", 1, 64, 64), Settings));
    }

    [Fact]
    public void ImageSize_SquareUsesPortraitAreaRoundedTo64()
    {
        var settings = new LlmConfiguration();
        Assert.Equal((768, 1152), settings.ImageSize(IllustrationOrientation.Portrait));
        Assert.Equal((1152, 768), settings.ImageSize(IllustrationOrientation.Landscape));
        Assert.Equal((960, 960), settings.ImageSize(IllustrationOrientation.Square));
    }
}

public class BookExportIllustrationTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 1, 2];

    private static (Book Book, BookExportService.ExportArt Art) Fixture()
    {
        var book = new Book
        {
            Id = 1, Title = "Fog", Genre = "Mystery", Language = "English",
            Chapters =
            [
                new Chapter { Id = 10, Number = 1, Title = "Gate", Content = "Para one.\n\nPara two.\n\nPara three." },
            ],
        };
        var scene = new BookExportService.ExportImage("img-5", "At the gate", Png, "image/png", ParagraphIndex: 1);
        var cover = new BookExportService.ExportImage("img-6", "Cover", Png, "image/png", ParagraphIndex: -1);
        var art = new BookExportService.ExportArt(
            cover, new[] { (10, scene) }.ToLookup(x => x.Item1, x => x.Item2), new Dictionary<int, BookExportService.ExportImage>());
        return (book, art);
    }

    [Fact]
    public void Html_PlacesFigureAfterAnchoredParagraph()
    {
        var (book, art) = Fixture();
        var html = BookExportService.GenerateHtml(book, art);
        var figure = html.IndexOf("<figcaption>At the gate</figcaption>", StringComparison.Ordinal);
        Assert.True(figure > html.IndexOf("Para two.", StringComparison.Ordinal));
        Assert.True(figure < html.IndexOf("Para three.", StringComparison.Ordinal));
        Assert.Contains("data:image/png;base64,", html);
        Assert.Contains("figure class=\"illustration cover\"", html);
    }

    [Fact]
    public void Html_WithoutArt_MatchesPlainConversion()
    {
        var (book, _) = Fixture();
        Assert.Equal(BookExportService.GenerateHtml(book), BookExportService.GenerateHtml(book, BookExportService.ExportArt.Empty));
        Assert.DoesNotContain("<figure", BookExportService.GenerateHtml(book));
    }

    [Fact]
    public void Epub_ShipsImagesCoverAndManifestEntries()
    {
        var (book, art) = Fixture();
        using var zip = new ZipArchive(new MemoryStream(BookExportService.GenerateEpub(book, null, art)));

        Assert.NotNull(zip.GetEntry("OEBPS/images/img-5.png"));
        Assert.NotNull(zip.GetEntry("OEBPS/images/img-6.png"));
        Assert.NotNull(zip.GetEntry("OEBPS/cover.xhtml"));
        var opf = new StreamReader(zip.GetEntry("OEBPS/content.opf")!.Open()).ReadToEnd();
        Assert.Contains("properties=\"cover-image\"", opf);
        Assert.Contains("href=\"images/img-5.png\"", opf);
        var chapter = new StreamReader(zip.GetEntry("OEBPS/chapter-1.xhtml")!.Open()).ReadToEnd();
        Assert.Contains("<img src=\"images/img-5.png\"", chapter);
    }

    [Fact]
    public void Fb2_EmbedsBinariesAndCoverpage()
    {
        var (book, art) = Fixture();
        var fb2 = BookExportService.GenerateFb2(book, null, art);
        Assert.Contains("<coverpage><image l:href=\"#img-6.png\"/></coverpage>", fb2);
        Assert.Contains("<image l:href=\"#img-5.png\"/>", fb2);
        Assert.Contains("<binary id=\"img-5.png\" content-type=\"image/png\">", fb2);
        Assert.True(fb2.IndexOf("<binary", StringComparison.Ordinal) > fb2.IndexOf("</body>", StringComparison.Ordinal));
    }
}
