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
    public void ParseChapterIllustrations_TruncatedStream_KeepsFinishedElementsOnly()
    {
        var paragraphs = ParagraphAnchors.Split("One.\n\nTwo.");
        const string raw = """
            {"illustrations":[{"paragraphNumber":1,"anchorText":"One","caption":"a","prompt":"first","negativePrompt":"",
              "orientation":"landscape","focusCharacter":"","characters":["Boris"]},
             {"paragraphNumber":2,"anchorText":"Two","caption":"b","prompt":"second, cut off mid-str
            """;

        var ill = Assert.Single(IllustrationPlannerAgent.ParseChapterIllustrations(raw, paragraphs, Cards));
        Assert.Equal("first", ill.Prompt);
        Assert.Equal(2, ill.FocusCharacterId);   // the only character shown becomes the focus
    }

    [Fact]
    public void ParseChapterIllustrations_NothingFinishedYet_ReturnsEmpty() =>
        Assert.Empty(IllustrationPlannerAgent.ParseChapterIllustrations(
            """{"illustrations":[{"paragraphNumber":1,"characters":["An""", ["x"], Cards));

    [Fact]
    public void ParseCharacterVisuals_SkipsEntriesWithoutNameOrVisual()
    {
        const string raw = """
            [{"name":"Boris","visualPrompt":"a stocky man"},
             {"name":"","visualPrompt":"nobody"}]
            """;
        var v = Assert.Single(IllustrationPlannerAgent.ParseCharacterVisuals(raw));
        Assert.Equal("a stocky man", v.VisualPrompt);
    }

    [Fact]
    public void PortraitPrompt_PutsSharedCompositionBeforeAppearance()
    {
        Assert.Equal("Head and shoulders, grey backdrop. a stocky man",
            IllustrationPlannerAgent.PortraitPrompt("Head and shoulders, grey backdrop.", "a stocky man"));
        Assert.Equal("a stocky man", IllustrationPlannerAgent.PortraitPrompt(null, " a stocky man "));
    }

    [Fact]
    public void ParseChapterPlan_ReadsRecurringAssets()
    {
        var paragraphs = ParagraphAnchors.Split("Anna lit the lamp.");
        const string raw = """
            {"illustrations":[{"paragraphNumber":1,"anchorText":"lit the lamp","caption":"Lamp","characters":["Anna"],
              "focusCharacter":"Anna","assets":[
                {"name":"the brass lamp","kind":"object","description":"a dented brass oil lamp"},
                {"name":"The Brass Lamp","kind":"object","description":"duplicate"},
                {"name":"","kind":"object","description":"nameless"},
                {"name":"the attic","kind":"spaceship","description":"a low attic"}],
              "prompt":"a woman lighting a dented brass oil lamp","negativePrompt":"","orientation":"landscape"}]}
            """;

        var plan = Assert.Single(IllustrationPlannerAgent.ParseChapterPlan(raw, paragraphs, Cards));
        Assert.Collection(plan.Assets,
            a => Assert.Equal(("the brass lamp", VisualAssetKind.Object, "a dented brass oil lamp"), (a.Name, a.Kind, a.Description)),
            a => Assert.Equal(("the attic", VisualAssetKind.Other), (a.Name, a.Kind)));   // unknown kind → Other
    }

    [Fact]
    public void ParseCharacterVisuals_ReadsUsualOutfit()
    {
        var v = Assert.Single(IllustrationPlannerAgent.ParseCharacterVisuals(
            """[{"name":"Boris","visualPrompt":"a stocky man","usualOutfit":"wearing a grey wool coat"}]"""));
        Assert.Equal("wearing a grey wool coat", v.UsualOutfit);
    }

    [Fact]
    public void PortraitPrompt_AppendsUsualOutfit()
    {
        Assert.Equal("Head and shoulders. A stocky man, wearing a grey wool coat.",
            IllustrationPlannerAgent.PortraitPrompt("Head and shoulders.", "A stocky man.", "wearing a grey wool coat."));
    }

    [Fact]
    public void CharacterVisuals_NamesEachCharactersUsualOutfit()
    {
        var cards = new[] { new CharacterCard { Id = 2, Name = "Boris", VisualPrompt = "a stocky man" } };
        var assets = new[] { new VisualAsset { Name = "Boris — usual outfit", CharacterCardId = 2, Description = "wearing a coat" } };
        Assert.Equal("- Boris: a stocky man (usual outfit: asset \"Boris — usual outfit\")",
            PromptTemplates.CharacterVisuals(cards, assets));
    }

    [Fact]
    public void ChapterIllustrationsUser_UsesConfiguredBaseCountAndListsAssets()
    {
        var text = PromptTemplates.ChapterIllustrationsUser(
            new Book { Title = "T", Language = "English" }, new Chapter { Number = 1, Title = "C" }, ["One."],
            "(none)", "(none)", imagesPerChapter: 3,
            visualAssets: PromptTemplates.VisualAssets([new VisualAsset { Name = "the lamp", Kind = VisualAssetKind.Object, Description = "a brass lamp" }]));
        Assert.Contains("Plan 3 illustrations", text);
        Assert.Contains("not a hard limit", text);
        Assert.Contains("- \"the lamp\" (object): a brass lamp", text);
    }

    [Fact]
    public void Compose_PrependsArtDirectionAndMergesNegatives()
    {
        var book = new Book { IllustrationStyle = "Ink wash, muted palette.", IllustrationNegativePrompt = "text, watermark" };
        var ill = new Illustration { Prompt = "A gate in fog", NegativePrompt = "people." };
        Assert.Equal("Ink wash, muted palette. A gate in fog", IllustrationRenderer.ComposePrompt(book, ill));
        Assert.Equal("text, watermark, people", IllustrationRenderer.ComposeNegative(book, ill));

        var plain = new Book();
        Assert.Equal("A gate in fog", IllustrationRenderer.ComposePrompt(plain, ill));
        Assert.Equal("people", IllustrationRenderer.ComposeNegative(plain, ill));
    }

    [Fact]
    public void IllustratorPrompt_TellsTheModelWhetherNegativesApply()
    {
        var book = new Book { Title = "Fog", Genre = "Mystery" };
        var atCfg1 = AgentBase.InterpolateSystemPrompt(DefaultPrompts.Illustrator, book, new StoryBible(),
            visualStyle: "ink", negativePrompts: PromptTemplates.NegativePromptSupport(1.0));
        Assert.Contains("ignores them", atCfg1);
        Assert.Contains("Never rely on the negative prompt", atCfg1);
        Assert.DoesNotContain("{NEGATIVE_PROMPTS}", atCfg1);
        Assert.Contains("applies them", PromptTemplates.NegativePromptSupport(2.0));
    }

    [Fact]
    public void EveryIllustrationRequest_SaysNotToRelyOnNegatives()
    {
        var book = new Book { Title = "Fog", Genre = "Mystery", Language = "English" };
        var chapter = new Chapter { Number = 1, Title = "Gate" };
        string[] requests =
        [
            PromptTemplates.CharacterVisualsUser(book, [new CharacterCard { Name = "Anna" }], "(none)"),
            PromptTemplates.StyleSheetUser(book, null, "ink wash"),
            PromptTemplates.ChapterIllustrationsUser(book, chapter, ["One."], "(none)", "(none)"),
            PromptTemplates.CoverUser(book, null, "", "(none)"),
        ];
        Assert.All(requests, r => Assert.Contains("Do not rely on negative prompts", r));
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

public class UnslothStudioImageGeneratorTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    static UnslothStudioImageGeneratorTests() => UnslothStudioImageGenerator.LoadPollInterval = TimeSpan.FromMilliseconds(1);

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

    private static (UnslothStudioImageGenerator Generator, FakeHandler Handler) Create(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        var handler = new FakeHandler(respond);
        return (new UnslothStudioImageGenerator((timeout, baseAddress) => new HttpClient(handler) { BaseAddress = baseAddress }), handler);
    }

    private static HttpResponseMessage JsonResponse(string json, HttpStatusCode code = HttpStatusCode.OK) =>
        new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static LlmConfiguration Settings(string? model = null) => new()
    {
        ImageProvider = ImageProvider.UnslothStudio,
        ImageEndpoint = "http://gpu:8888/v1",
        ImageApiKey = "sk-unsloth-test",
        ImageModel = model,
        ImageSteps = 8,
        ImageCfg = 1.0,
    };

    private static Func<HttpRequestMessage, HttpResponseMessage> LoadedServer => req => req.RequestUri!.AbsolutePath switch
    {
        "/api/inference/images/status" => JsonResponse("""{"loaded":true,"repo_id":"unsloth/Z-Image-Turbo-GGUF"}"""),
        "/api/inference/images/generate" => JsonResponse(
            """{"images":[{"id":"img1","url":"/api/inference/images/gallery/img1/file","seed":7}]}"""),
        "/api/inference/images/gallery/img1/file" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Png) },
        _ => new HttpResponseMessage(HttpStatusCode.NotFound),
    };

    [Fact]
    public async Task Generate_PostsNativeRequestWithSeedAndKey_ThenFetchesGalleryImage()
    {
        var (gen, handler) = Create(LoadedServer);

        var image = await gen.GenerateAsync(new ImageGenRequest("a cat", "blurry", 7, 768, 1152), Settings());

        Assert.Equal(Png, image.Data);
        Assert.Equal(7, image.Seed);
        var generate = handler.Calls.Single(c => c.Request.RequestUri!.AbsolutePath == "/api/inference/images/generate");
        Assert.Equal("Bearer", generate.Request.Headers.Authorization?.Scheme);
        Assert.Equal("sk-unsloth-test", generate.Request.Headers.Authorization?.Parameter);
        using var body = JsonDocument.Parse(generate.Body!);
        Assert.Equal("a cat", body.RootElement.GetProperty("prompt").GetString());
        Assert.Equal("blurry", body.RootElement.GetProperty("negative_prompt").GetString());
        Assert.Equal(7, body.RootElement.GetProperty("seed").GetInt64());
        Assert.Equal(8, body.RootElement.GetProperty("steps").GetInt32());
        Assert.Equal(1.0, body.RootElement.GetProperty("guidance").GetDouble());
        Assert.Equal(1152, body.RootElement.GetProperty("height").GetInt32());
        Assert.DoesNotContain(handler.Calls, c => c.Request.RequestUri!.AbsolutePath.EndsWith("/images/load"));
    }

    [Fact]
    public async Task Generate_UnsetFields_AreOmittedSoStudioDefaultsApply()
    {
        var (gen, handler) = Create(LoadedServer);
        var settings = Settings();
        settings.ImageSteps = null;
        settings.ImageCfg = null;

        await gen.GenerateAsync(new ImageGenRequest("a cat", "", 7, 768, 1152), settings);

        using var body = JsonDocument.Parse(handler.Calls.Single(c => c.Request.Method == HttpMethod.Post).Body!);
        Assert.False(body.RootElement.TryGetProperty("negative_prompt", out _));
        Assert.False(body.RootElement.TryGetProperty("steps", out _));
        Assert.False(body.RootElement.TryGetProperty("guidance", out _));
    }

    [Fact]
    public async Task Generate_NoModelLoaded_LoadsConfiguredModelAndWaits()
    {
        var statusCalls = 0;
        var (gen, handler) = Create(req => req.RequestUri!.AbsolutePath switch
        {
            // Not loaded before the load, still loading on the first poll, then ready.
            "/api/inference/images/status" => JsonResponse(++statusCalls >= 3 ? """{"loaded":true}""" : """{"loaded":false}"""),
            "/api/inference/images/load" => JsonResponse("""{"loaded":false}"""),
            "/api/inference/images/load-progress" => JsonResponse("""{"phase":"finalizing"}"""),
            _ => LoadedServer(req),
        });

        var image = await gen.GenerateAsync(new ImageGenRequest("a cat", "", 7, 768, 1152),
            Settings("unsloth/Z-Image-Turbo-GGUF:z-image-turbo-Q8_0.gguf"));

        Assert.Equal(Png, image.Data);
        using var load = JsonDocument.Parse(handler.Calls.Single(c => c.Request.RequestUri!.AbsolutePath == "/api/inference/images/load").Body!);
        Assert.Equal("unsloth/Z-Image-Turbo-GGUF", load.RootElement.GetProperty("model_path").GetString());
        Assert.Equal("z-image-turbo-Q8_0.gguf", load.RootElement.GetProperty("gguf_filename").GetString());
    }

    [Fact]
    public async Task Generate_NoModelLoadedAndNoneConfigured_Throws()
    {
        var (gen, _) = Create(req => req.RequestUri!.AbsolutePath == "/api/inference/images/status"
            ? JsonResponse("""{"loaded":false}""")
            : LoadedServer(req));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gen.GenerateAsync(new ImageGenRequest("x", "", 1, 256, 256), Settings()));
        Assert.Contains("no image model loaded", ex.Message);
    }

    [Fact]
    public async Task Generate_LoadFails_SurfacesStudioError()
    {
        var (gen, _) = Create(req => req.RequestUri!.AbsolutePath switch
        {
            "/api/inference/images/status" => JsonResponse("""{"loaded":false}"""),
            "/api/inference/images/load" => JsonResponse("""{"loaded":false}"""),
            "/api/inference/images/load-progress" => JsonResponse("""{"phase":"error","error":"out of memory"}"""),
            _ => LoadedServer(req),
        });

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gen.GenerateAsync(new ImageGenRequest("x", "", 1, 256, 256), Settings("unsloth/Z-Image-Turbo")));
        Assert.Contains("out of memory", ex.Message);
    }

    [Fact]
    public async Task Generate_OpenAiErrorEnvelope_SurfacesMessage()
    {
        var (gen, _) = Create(req => req.RequestUri!.AbsolutePath == "/api/inference/images/generate"
            ? JsonResponse("""{"detail":{"error":{"message":"GPU is busy","type":"gpu_busy"}}}""", HttpStatusCode.Conflict)
            : LoadedServer(req));

        var ex = await Assert.ThrowsAsync<HttpRequestException>(() =>
            gen.GenerateAsync(new ImageGenRequest("x", "", 1, 256, 256), Settings()));
        Assert.Contains("GPU is busy", ex.Message);
    }

    [Fact]
    public async Task Health_IdleWithConfiguredModel_IsReady()
    {
        var (gen, _) = Create(_ => JsonResponse("""{"loaded":false}"""));

        var health = await gen.CheckHealthAsync(Settings("unsloth/Z-Image-Turbo"));

        Assert.True(health.Ready);
        Assert.Equal("idle", health.Status);
    }

    [Fact]
    public async Task Health_Unauthorized_PointsAtApiKey()
    {
        var (gen, _) = Create(_ => JsonResponse("""{"detail":"Invalid API key"}""", HttpStatusCode.Unauthorized));

        var health = await gen.CheckHealthAsync(Settings());

        Assert.False(health.Ready);
        Assert.Equal("unauthorized", health.Status);
    }

    [Theory]
    [InlineData("http://gpu:8888", "http://gpu:8888")]
    [InlineData("http://gpu:8888/v1/", "http://gpu:8888")]
    [InlineData("https://studio.example/proxy/api/inference", "https://studio.example/proxy")]
    public void BaseEndpoint_StripsApiSuffixes(string endpoint, string expected) =>
        Assert.Equal(expected, UnslothStudioImageGenerator.BaseEndpoint(endpoint));

    [Theory]
    [InlineData("unsloth/Z-Image-Turbo", "unsloth/Z-Image-Turbo", null)]
    [InlineData("unsloth/Z-Image-Turbo-GGUF:z-image-turbo-Q4_K_M.gguf", "unsloth/Z-Image-Turbo-GGUF", "z-image-turbo-Q4_K_M.gguf")]
    [InlineData(@"C:\models\z-image", @"C:\models\z-image", null)]
    public void ParseModel_SplitsOnlyACheckpointFile(string model, string path, string? file)
    {
        var parsed = UnslothStudioImageGenerator.ParseModel(model)!;
        Assert.Equal(path, parsed.ModelPath);
        Assert.Equal(file, parsed.GgufFilename);
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
        var art = new BookExportService.ExportArt(cover, new[] { (10, scene) }.ToLookup(x => x.Item1, x => x.Item2));
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

public class ExportImageCompressorTests
{
    private static byte[] NoisyPng(int width, int height)
    {
        using var bitmap = new SkiaSharp.SKBitmap(width, height);
        var rng = new Random(42);
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                bitmap.SetPixel(x, y, new SkiaSharp.SKColor((byte)(x % 256), (byte)(y % 256), (byte)rng.Next(256)));
        using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
        return image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100).ToArray();
    }

    [Fact]
    public void Compress_ReencodesPngAsSmallerJpeg()
    {
        var png = NoisyPng(400, 300);
        var (data, type) = ExportImageCompressor.Compress(png, "image/png");
        Assert.Equal("image/jpeg", type);
        Assert.True(data.Length < png.Length / 2, $"{data.Length} vs {png.Length}");
        using var decoded = SkiaSharp.SKBitmap.Decode(data);
        Assert.Equal((400, 300), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void Compress_ScalesDownOversizedImages()
    {
        var (data, _) = ExportImageCompressor.Compress(NoisyPng(3200, 800), "image/png");
        using var decoded = SkiaSharp.SKBitmap.Decode(data);
        Assert.Equal((ExportImageCompressor.MaxDimension, 400), (decoded.Width, decoded.Height));
    }

    [Fact]
    public void Compress_UndecodableBytes_ShipUnchanged()
    {
        var junk = new byte[] { 1, 2, 3, 4 };
        Assert.Equal((junk, "image/png"), ExportImageCompressor.Compress(junk, "image/png"));
    }
}
