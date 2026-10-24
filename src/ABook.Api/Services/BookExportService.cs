using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using ABook.Core.Text;

namespace ABook.Api.Services;

/// <summary>
/// Generates HTML, FB2, EPUB, and Metadata exports for a book.
/// Extracted from ExportController so the public API endpoint can reuse the same logic.
/// </summary>
public class BookExportService
{
    private readonly IBookRepository _repo;

    public BookExportService(IBookRepository repo) => _repo = repo;

    /// <summary>Generates the requested export and returns (bytes, contentType, filename).</summary>
    public async Task<(byte[] Bytes, string ContentType, string Filename)> ExportAsync(int bookId, string format)
    {
        // Active details only — archived chapters are display-only history and never ship in an export.
        var book = await _repo.GetByIdWithActiveDetailsAsync(bookId)
            ?? throw new KeyNotFoundException($"Book {bookId} not found.");

        var bible    = await _repo.GetStoryBibleAsync(bookId);
        var safeName = SafeFilename(book.Title);

        if (format.Equals("metadata", StringComparison.OrdinalIgnoreCase))
            return (
                Encoding.UTF8.GetBytes(MetadataExport.Generate(await LoadMetadataAsync(book, bible))),
                "text/html; charset=utf-8",
                $"{safeName}-metadata.html");

        var art = await LoadArtAsync(book);
        return format.ToLowerInvariant() switch
        {
            "fb2"  => (Encoding.UTF8.GetBytes(GenerateFb2(book, bible, art)),
                      "application/x-fictionbook+xml; charset=utf-8",
                      $"{safeName}.fb2"),
            "epub" => (GenerateEpub(book, bible, art),
                      "application/epub+zip",
                      $"{safeName}.epub"),
            _      => (Encoding.UTF8.GetBytes(GenerateHtml(book, art)),
                      "text/html; charset=utf-8",
                      $"{safeName}.html"),
        };
    }

    /// <summary>
    /// One chapter as its own small book (html, fb2 or epub), with the chapter's illustrations and the cover —
    /// what the Telegram bot sends after each chapter edit.
    /// </summary>
    public async Task<(byte[] Bytes, string ContentType, string Filename)> ExportChapterAsync(int bookId, int chapterId, string format)
    {
        var book = await _repo.GetByIdWithActiveDetailsAsync(bookId)
            ?? throw new KeyNotFoundException($"Book {bookId} not found.");
        var chapter = book.Chapters.FirstOrDefault(c => c.Id == chapterId)
            ?? throw new KeyNotFoundException($"Chapter {chapterId} not found.");

        // A detached copy holding only this chapter: the loaded book is tracked, and changing its chapter
        // collection would read to EF as removing the other chapters.
        var single = new Book
        {
            Id = book.Id,
            Title = chapter.Title.Length > 0
                ? $"{book.Title} — Chapter {chapter.Number}: {chapter.Title}"
                : $"{book.Title} — Chapter {chapter.Number}",
            Premise = book.Premise,
            Genre = book.Genre,
            Language = book.Language,
            CreatedAt = book.CreatedAt,
            Chapters = [chapter],
        };
        var bible = await _repo.GetStoryBibleAsync(bookId);
        var art = await LoadArtAsync(book);
        art = new ExportArt(art.Cover, art.Scenes[chapterId].ToLookup(_ => chapterId));
        var name = $"{SafeFilename(book.Title)}-ch{chapter.Number:00}";

        return format.ToLowerInvariant() switch
        {
            "fb2"  => (Encoding.UTF8.GetBytes(GenerateFb2(single, bible, art)),
                      "application/x-fictionbook+xml; charset=utf-8", $"{name}.fb2"),
            "epub" => (GenerateEpub(single, bible, art), "application/epub+zip", $"{name}.epub"),
            _      => (Encoding.UTF8.GetBytes(GenerateHtml(single, art)), "text/html; charset=utf-8", $"{name}.html"),
        };
    }

    // ── Metadata ──────────────────────────────────────────────────────────────

    private async Task<MetadataSources> LoadMetadataAsync(Book book, StoryBible? bible)
    {
        var characters = (await _repo.GetAllCharacterCardsAsync(book.Id)).ToList();
        var characterVersions = new List<CharacterCardVersion>();
        foreach (var c in characters)
            characterVersions.AddRange(await _repo.GetCharacterVersionsAsync(book.Id, c.Id));

        var plotThreadVersions = new List<PlotThreadVersion>();
        foreach (var t in await _repo.GetAllPlotThreadsAsync(book.Id))
            plotThreadVersions.AddRange(await _repo.GetPlotThreadVersionsAsync(book.Id, t.Id));

        var chapterVersions = new List<ChapterVersion>();
        foreach (var c in book.Chapters)
            chapterVersions.AddRange(await _repo.GetChapterVersionsAsync(c.Id));

        var illustrations = await _repo.GetIllustrationsAsync(book.Id);
        var images = await _repo.GetBookImagesAsync(book.Id, illustrations.Where(i => i.ImageId.HasValue).Select(i => i.ImageId!.Value));

        return new MetadataSources
        {
            Book                = book,
            Bible               = bible,
            BookSnapshots       = (await _repo.GetBookSnapshotsAsync(book.Id)).ToList(),
            BibleSnapshots      = (await _repo.GetStoryBibleSnapshotsAsync(book.Id)).ToList(),
            Characters          = characters,
            CharacterVersions   = characterVersions,
            CharacterSnapshots  = (await _repo.GetCharactersSnapshotsAsync(book.Id)).ToList(),
            PlotThreadVersions  = plotThreadVersions,
            PlotThreadSnapshots = (await _repo.GetPlotThreadsSnapshotsAsync(book.Id)).ToList(),
            ChapterVersions     = chapterVersions,
            Clarifications      = await _repo.GetClarificationsAsync(book.Id, includeArchived: true),
            Messages            = (await _repo.GetMessagesAsync(book.Id)).ToList(),
            TokenUsage          = (await _repo.GetTokenUsageAsync(book.Id)).ToList(),
            Runs                = await _repo.GetRunsForBookAsync(book.Id),
            Illustrations       = illustrations,
            Images              = images.ToDictionary(i => i.Id),
            VisualAssets        = await _repo.GetVisualAssetsAsync(book.Id),
        };
    }

    // ── Illustrations ─────────────────────────────────────────────────────────

    /// <summary>
    /// Every illustration that has an image — including one whose plan was edited since and awaits a
    /// re-render, which ships its previous image. Scenes of archived chapters are left out.
    /// </summary>
    private async Task<ExportArt> LoadArtAsync(Book book)
    {
        var illustrations = (await _repo.GetIllustrationsAsync(book.Id)).Where(i => i.ImageId.HasValue).ToList();
        if (illustrations.Count == 0) return ExportArt.Empty;

        var chapters = book.Chapters.Where(c => !c.IsArchived).ToDictionary(c => c.Id);
        var images = (await _repo.GetBookImagesAsync(book.Id, illustrations.Select(i => i.ImageId!.Value)))
            .ToDictionary(i => i.Id);

        // Compressed once per image, however many places it appears in.
        var compressed = new Dictionary<int, (byte[] Data, string ContentType)>();
        ExportImage? Image(Illustration i, int paragraphIndex)
        {
            if (!images.TryGetValue(i.ImageId!.Value, out var img)) return null;
            if (!compressed.TryGetValue(img.Id, out var c))
                compressed[img.Id] = c = ExportImageCompressor.Compress(img.Data, img.ContentType);
            return new ExportImage($"img-{i.Id}", i.Caption, c.Data, c.ContentType, paragraphIndex);
        }

        var scenes = new List<(int ChapterId, ExportImage Image)>();
        foreach (var i in illustrations.Where(i => i.Kind == IllustrationKind.Scene))
        {
            if (i.ChapterId is not int chapterId || !chapters.TryGetValue(chapterId, out var chapter)) continue;
            var index = ParagraphAnchors.Resolve(ParagraphAnchors.Split(chapter.Content), i.AnchorText, i.ParagraphNumber);
            if (index >= 0 && Image(i, index) is { } image) scenes.Add((chapterId, image));
        }

        var cover = illustrations.FirstOrDefault(i => i.Kind == IllustrationKind.Cover) is { } c ? Image(c, -1) : null;
        return new ExportArt(cover, scenes.ToLookup(s => s.ChapterId, s => s.Image));
    }

    internal sealed record ExportImage(string Key, string Caption, byte[] Data, string ContentType, int ParagraphIndex)
    {
        public string Extension => ContentType switch { "image/jpeg" => "jpg", "image/webp" => "webp", _ => "png" };
        public string FileName => $"{Key}.{Extension}";
        public string DataUri => $"data:{ContentType};base64,{Convert.ToBase64String(Data)}";
    }

    internal sealed record ExportArt(ExportImage? Cover, ILookup<int, ExportImage> Scenes)
    {
        public static readonly ExportArt Empty =
            new(null, Array.Empty<(int, ExportImage)>().ToLookup(x => x.Item1, x => x.Item2));

        public IEnumerable<ExportImage> All =>
            (Cover is null ? [] : new[] { Cover }).Concat(Scenes.SelectMany(g => g));

        /// <summary>Emits the figures that follow a paragraph; null when the chapter has none.</summary>
        public Func<int, string>? FiguresFor(int chapterId, Func<ExportImage, string> figure)
        {
            if (!Scenes.Contains(chapterId)) return null;
            var byParagraph = Scenes[chapterId].ToLookup(i => i.ParagraphIndex);
            return index => string.Concat(byParagraph[index].Select(figure));
        }
    }

    // ── HTML ─────────────────────────────────────────────────────────────────

    private static readonly string HtmlCss = @"
    :root { --bg: #ffffff; --fg: #1a1a1a; --fs: 18px; --lh: 1.75; }
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body { background: var(--bg); color: var(--fg); font-family: Georgia, 'Times New Roman', serif; font-size: var(--fs); line-height: var(--lh); transition: background 0.25s, color 0.25s; }
    #toolbar { position: sticky; top: 0; z-index: 100; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 8px 20px; background: var(--bg); border-bottom: 1px solid rgba(128,128,128,0.25); font-family: system-ui, -apple-system, sans-serif; transition: background 0.25s; }
    .toolbar-label { font-size: 0.72em; opacity: 0.55; letter-spacing: 0.04em; text-transform: uppercase; margin-right: 2px; }
    .preset-btn { padding: 3px 10px; border-radius: 4px; cursor: pointer; font-size: 0.78em; font-weight: 600; white-space: nowrap; transition: opacity 0.15s; }
    .preset-btn:hover { opacity: 0.8; }
    .toolbar-sep { width: 1px; height: 22px; background: rgba(128,128,128,0.3); margin: 0 4px; }
    .size-btn { padding: 3px 9px; border-radius: 4px; cursor: pointer; font-weight: bold; background: transparent; color: var(--fg); border: 1px solid rgba(128,128,128,0.4); font-size: 0.85em; transition: opacity 0.15s; }
    .size-btn:hover { opacity: 0.65; }
    #font-size-label { font-size: 0.75em; min-width: 2.2em; text-align: center; opacity: 0.6; }
    main { padding: 48px 24px 96px; }
    header.book-header { margin-bottom: 56px; text-align: center; padding-bottom: 32px; border-bottom: 2px solid rgba(128,128,128,0.15); }
    header.book-header h1 { font-size: 2.4em; margin-bottom: 10px; letter-spacing: -0.01em; }
    header.book-header .meta { opacity: 0.5; font-style: italic; font-size: 0.95em; }
    nav#toc { margin-bottom: 60px; padding: 24px 28px; border: 1px solid rgba(128,128,128,0.2); border-radius: 6px; }
    nav#toc h2 { font-size: 1.1em; margin-bottom: 14px; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.65; }
    nav#toc ol { padding-left: 1.5em; }
    nav#toc li { margin: 7px 0; }
    nav#toc a { color: var(--fg); opacity: 0.82; text-decoration: none; border-bottom: 1px dotted rgba(128,128,128,0.5); transition: opacity 0.15s; }
    nav#toc a:hover { opacity: 1; }
    .chapter { margin-bottom: 72px; }
    .chapter h2 { font-size: 1.65em; margin-bottom: 28px; padding-bottom: 12px; border-bottom: 2px solid rgba(128,128,128,0.15); }
    .chapter h3 { font-size: 1.3em; margin: 28px 0 12px; }
    .chapter h4 { font-size: 1.1em; margin: 22px 0 10px; }
    .chapter p { margin-bottom: 1.1em; text-align: justify; }
    .chapter hr { border: none; border-top: 1px solid rgba(128,128,128,0.25); margin: 2.5em auto; width: 40%; }
    .chapter strong { font-weight: 700; }
    .chapter em { font-style: italic; }
    .chapter code { font-family: 'Courier New', Courier, monospace; font-size: 0.88em; opacity: 0.85; }
    .chapter del { text-decoration: line-through; opacity: 0.55; }
    figure.illustration { margin: 2em auto; text-align: center; }
    figure.illustration img { max-width: 100%; height: auto; border-radius: 4px; }
    figure.illustration figcaption { font-size: 0.85em; font-style: italic; opacity: 0.7; margin-top: 0.5em; }
    figure.cover img { max-width: min(100%, 560px); }";

    private static readonly string HtmlJs = @"    (function () {
      var presets = [
        {name:'Light',     bg:'#ffffff', fg:'#1a1a1a'},
        {name:'Dark',      bg:'#1c1c1e', fg:'#e8e8e8'},
        {name:'Hi-Contrast',bg:'#000000',fg:'#ffff00'},
        {name:'Sepia',     bg:'#f4ecd8', fg:'#5c4a1e'},
        {name:'Solarized', bg:'#002b36', fg:'#93a1a1'},
        {name:'Retro',     bg:'#0d0d0d', fg:'#00e676'}
      ];
      var fontSize = 18;
      function applyPreset(index) {
        var p = presets[index];
        document.documentElement.style.setProperty('--bg', p.bg);
        document.documentElement.style.setProperty('--fg', p.fg);
        document.body.style.background = p.bg;
        document.body.style.color = p.fg;
        document.getElementById('toolbar').style.background = p.bg;
        document.querySelectorAll('.size-btn').forEach(function(b) { b.style.color = p.fg; });
        document.getElementById('font-size-label').style.color = p.fg;
        try { localStorage.setItem('abook-preset', index); } catch(e) {}
      }
      function applyFontSize(size) {
        fontSize = Math.min(Math.max(size, 12), 36);
        document.documentElement.style.setProperty('--fs', fontSize + 'px');
        document.getElementById('font-size-label').textContent = fontSize;
        try { localStorage.setItem('abook-fontsize', fontSize); } catch(e) {}
      }
      document.querySelectorAll('.preset-btn').forEach(function(btn, i) {
        btn.addEventListener('click', function() { applyPreset(i); });
      });
      document.getElementById('font-smaller').addEventListener('click', function() { applyFontSize(fontSize - 2); });
      document.getElementById('font-larger').addEventListener('click',  function() { applyFontSize(fontSize + 2); });
      try {
        var sp = localStorage.getItem('abook-preset'); if (sp !== null) applyPreset(Number(sp));
        var sf = localStorage.getItem('abook-fontsize'); if (sf !== null) applyFontSize(Number(sf));
      } catch(e) {}
    })();";

    internal static string GenerateHtml(Book book, ExportArt? art = null)
    {
        art ??= ExportArt.Empty;
        var chapters = book.Chapters
            .Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Number)
            .ToList();

        var lang = GetLang2(book.Language);

        var toc = string.Join("\n", chapters.Select(c =>
        {
            var suffix = c.Title.Length > 0 ? $": {EscHtml(c.Title)}" : "";
            return $"        <li><a href=\"#chapter-{c.Number}\">Chapter {c.Number}{suffix}</a></li>";
        }));

        var chapHtml = string.Concat(chapters.Select(c =>
        {
            var suffix = c.Title.Length > 0 ? $": {EscHtml(c.Title)}" : "";
            return $"\n<section id=\"chapter-{c.Number}\" class=\"chapter\">\n" +
                   $"  <h2>Chapter {c.Number}{suffix}</h2>\n" +
                   $"  {MarkdownToHtml(c.Content, art.FiguresFor(c.Id, HtmlFigure))}\n</section>\n";
        }));

        var presets = new (string bg, string fg, string border, string name)[]
        {
            ("#ffffff", "#1a1a1a", "#aaaaaa", "Light"),
            ("#1c1c1e", "#e8e8e8", "#e8e8e8", "Dark"),
            ("#000000", "#ffff00", "#ffff00", "Hi-Contrast"),
            ("#f4ecd8", "#5c4a1e", "#5c4a1e", "Sepia"),
            ("#002b36", "#93a1a1", "#93a1a1", "Solarized"),
            ("#0d0d0d", "#00e676", "#00e676", "Retro"),
        };
        var presetBtns = string.Join("\n    ", presets.Select((p, i) =>
            $"<button class=\"preset-btn\" data-index=\"{i}\" " +
            $"style=\"background:{p.bg};color:{p.fg};border:2px solid {p.border}\" " +
            $"title=\"{p.name}\">{p.name}</button>"));

        var genreMeta = EscHtml(book.Genre) +
            (book.Language.Length > 0 ? " &middot; " + EscHtml(book.Language) : "");

        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html>\n");
        sb.Append($"<html lang=\"{lang}\">\n<head>\n");
        sb.Append("  <meta charset=\"UTF-8\">\n");
        sb.Append("  <meta name=\"viewport\" content=\"width=device-width, initial-scale=1.0\">\n");
        sb.Append($"  <title>{EscHtml(book.Title)}</title>\n");
        sb.Append("  <style>"); sb.Append(HtmlCss); sb.Append("\n  </style>\n</head>\n<body>\n");
        sb.Append("  <div id=\"toolbar\">\n");
        sb.Append("    <span class=\"toolbar-label\">Theme</span>\n");
        sb.Append($"    {presetBtns}\n");
        sb.Append("    <div class=\"toolbar-sep\"></div>\n");
        sb.Append("    <span class=\"toolbar-label\">Size</span>\n");
        sb.Append("    <button class=\"size-btn\" id=\"font-smaller\" title=\"Decrease font size\">A\u2212</button>\n");
        sb.Append("    <span id=\"font-size-label\">18</span>\n");
        sb.Append("    <button class=\"size-btn\" id=\"font-larger\" title=\"Increase font size\">A+</button>\n");
        sb.Append("  </div>\n  <main>\n");
        sb.Append("    <header class=\"book-header\">\n");
        sb.Append($"      <h1>{EscHtml(book.Title)}</h1>\n");
        sb.Append($"      <p class=\"meta\">{genreMeta}</p>\n");
        if (art.Cover is { } cover)
            sb.Append($"      <figure class=\"illustration cover\"><img src=\"{cover.DataUri}\" alt=\"{EscHtml(book.Title)}\"></figure>\n");
        sb.Append("    </header>\n    <nav id=\"toc\">\n      <h2>Contents</h2>\n      <ol>\n");
        sb.Append(toc);
        sb.Append("\n      </ol>\n    </nav>\n");
        sb.Append(chapHtml);
        sb.Append("  </main>\n  <script>\n");
        sb.Append(HtmlJs);
        sb.Append("\n  </script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    private static string HtmlFigure(ExportImage image) =>
        $"<figure class=\"illustration\"><img src=\"{image.DataUri}\" alt=\"{EscHtml(image.Caption)}\">" +
        (image.Caption.Length > 0 ? $"<figcaption>{EscHtml(image.Caption)}</figcaption>" : "") + "</figure>\n";

    // ── FB2 ───────────────────────────────────────────────────────────────────

    internal static string GenerateFb2(Book book, StoryBible? bible, ExportArt? art = null)
    {
        art ??= ExportArt.Empty;
        var chapters = book.Chapters
            .Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Number)
            .ToList();

        var lang = GetLang2(book.Language);
        var year = (book.CreatedAt != default ? book.CreatedAt : DateTime.UtcNow).Year.ToString();

        var annotParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(book.Premise))
            annotParts.Add(EscXml(book.Premise));
        if (!string.IsNullOrWhiteSpace(bible?.SettingDescription))
            annotParts.Add("Setting: " + EscXml(bible!.SettingDescription));
        if (!string.IsNullOrWhiteSpace(bible?.ToneAndStyle))
            annotParts.Add("Tone: " + EscXml(bible!.ToneAndStyle));
        var annotXml = annotParts.Count > 0
            ? "<annotation>" + string.Concat(annotParts.Select(p => $"<p>{p}</p>")) + "</annotation>"
            : "";

        var kwParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(book.Genre))    kwParts.Add(book.Genre);
        if (!string.IsNullOrWhiteSpace(bible?.Themes)) kwParts.Add(bible!.Themes);
        var kwXml = kwParts.Count > 0
            ? $"<keywords>{EscXml(string.Join(", ", kwParts))}</keywords>"
            : "";

        var sections = string.Concat(chapters.Select(c =>
        {
            var suffix = c.Title.Length > 0 ? ": " + EscXml(c.Title) : "";
            return $"\n  <section id=\"chapter-{c.Number}\">\n" +
                   $"    <title><p>Chapter {c.Number}{suffix}</p></title>\n" +
                   $"    {MarkdownToFb2(c.Content, art.FiguresFor(c.Id, Fb2Figure))}\n  </section>";
        }));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\" xmlns:l=\"http://www.w3.org/1999/xlink\">");
        sb.AppendLine("  <description>");
        sb.AppendLine("    <title-info>");
        sb.AppendLine($"      <genre>{MapGenreToFb2(book.Genre)}</genre>");
        sb.AppendLine("      <author><nickname>ABook</nickname></author>");
        sb.AppendLine($"      <book-title>{EscXml(book.Title)}</book-title>");
        if (art.Cover is { } cover)
            sb.AppendLine($"      <coverpage><image l:href=\"#{cover.FileName}\"/></coverpage>");
        sb.AppendLine($"      <lang>{lang}</lang>");
        if (annotXml.Length > 0) sb.AppendLine($"      {annotXml}");
        if (kwXml.Length > 0)    sb.AppendLine($"      {kwXml}");
        sb.AppendLine("    </title-info>");
        sb.AppendLine("    <publish-info>");
        sb.AppendLine("      <publisher>ABook</publisher>");
        sb.AppendLine($"      <year>{year}</year>");
        sb.AppendLine("    </publish-info>");
        sb.AppendLine("    <document-info>");
        sb.AppendLine("      <program-used>ABook</program-used>");
        sb.AppendLine($"      <date>{DateTime.UtcNow:yyyy-MM-dd}</date>");
        sb.AppendLine("    </document-info>");
        sb.AppendLine("  </description>");
        sb.AppendLine($"  <body>{sections}");
        sb.AppendLine("  </body>");
        foreach (var image in art.All)
            sb.AppendLine($"  <binary id=\"{image.FileName}\" content-type=\"{image.ContentType}\">{Convert.ToBase64String(image.Data)}</binary>");
        sb.AppendLine("</FictionBook>");
        return sb.ToString();
    }

    private static string Fb2Figure(ExportImage image) =>
        $"<image l:href=\"#{image.FileName}\"/>\n" +
        (image.Caption.Length > 0 ? $"<p><emphasis>{EscXml(image.Caption)}</emphasis></p>\n" : "");

    // ── EPUB ──────────────────────────────────────────────────────────────────

    internal static byte[] GenerateEpub(Book book, StoryBible? bible, ExportArt? art = null)
    {
        art ??= ExportArt.Empty;
        var chapters = book.Chapters
            .Where(c => !c.IsArchived && !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Number)
            .ToList();

        var lang     = GetLang2(book.Language);
        var uid      = Guid.NewGuid().ToString();
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var isoDate  = (book.CreatedAt != default ? book.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");

        const string chStyle =
            "body{font-family:serif;font-size:1em;line-height:1.6;margin:1em 2em}" +
            "p{margin-bottom:.8em;text-align:justify}h2,h3,h4{font-family:sans-serif}" +
            "figure{margin:1.5em 0;text-align:center}figure img{max-width:100%;height:auto}" +
            "figcaption{font-size:.85em;font-style:italic;margin-top:.4em}";

        var subjects = new List<string>();
        if (!string.IsNullOrWhiteSpace(book.Genre)) subjects.Add(book.Genre);
        if (!string.IsNullOrWhiteSpace(bible?.Themes))
            subjects.AddRange(bible!.Themes
                .Split(new[] { ',', ';', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim()).Where(t => t.Length > 0));
        var subjectTags = string.Concat(subjects.Select(s => $"<dc:subject>{EscHtml(s)}</dc:subject>"));
        var descTag = !string.IsNullOrWhiteSpace(book.Premise)
            ? $"<dc:description>{EscHtml(book.Premise)}</dc:description>"
            : "";

        var manifestItems = new List<string>
        {
            "<item id=\"nav\" href=\"nav.xhtml\" media-type=\"application/xhtml+xml\" properties=\"nav\"/>"
        };
        var spineItems   = new List<string>();
        var chapterFiles = new List<(string Name, string Content)>();

        foreach (var image in art.All)
            manifestItems.Add(
                $"<item id=\"{image.Key}\" href=\"images/{image.FileName}\" media-type=\"{image.ContentType}\"" +
                (image == art.Cover ? " properties=\"cover-image\"" : "") + "/>");
        if (art.Cover is { } cover)
        {
            var coverXhtml =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<!DOCTYPE html>" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
                $"<head><meta charset=\"UTF-8\"/><title>{EscHtml(book.Title)}</title>" +
                "<style>body{margin:0;text-align:center}img{max-width:100%;max-height:100vh}</style></head>" +
                $"<body epub:type=\"cover\"><img src=\"images/{cover.FileName}\" alt=\"{EscHtml(book.Title)}\"/></body></html>";
            chapterFiles.Add(("OEBPS/cover.xhtml", coverXhtml));
            manifestItems.Add("<item id=\"cover\" href=\"cover.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spineItems.Add("<itemref idref=\"cover\" linear=\"no\"/>");
        }

        foreach (var c in chapters)
        {
            var suffix  = c.Title.Length > 0 ? ": " + EscHtml(c.Title) : "";
            var heading = $"Chapter {c.Number}{suffix}";
            var xhtml   =
                "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
                "<!DOCTYPE html>" +
                "<html xmlns=\"http://www.w3.org/1999/xhtml\">" +
                $"<head><meta charset=\"UTF-8\"/><title>{heading}</title>" +
                $"<style>{chStyle}</style></head>" +
                $"<body><h2>{heading}</h2>{MarkdownToXhtml(c.Content, art.FiguresFor(c.Id, XhtmlFigure))}</body></html>";
            chapterFiles.Add(($"OEBPS/chapter-{c.Number}.xhtml", xhtml));
            manifestItems.Add($"<item id=\"c{c.Number}\" href=\"chapter-{c.Number}.xhtml\" media-type=\"application/xhtml+xml\"/>");
            spineItems.Add($"<itemref idref=\"c{c.Number}\"/>");
        }

        var navItems = string.Concat(chapters.Select(c =>
        {
            var suffix = c.Title.Length > 0 ? ": " + EscHtml(c.Title) : "";
            return $"<li><a href=\"chapter-{c.Number}.xhtml\">Chapter {c.Number}{suffix}</a></li>";
        }));
        var nav =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<!DOCTYPE html>" +
            "<html xmlns=\"http://www.w3.org/1999/xhtml\" xmlns:epub=\"http://www.idpf.org/2007/ops\">" +
            $"<head><meta charset=\"UTF-8\"/><title>{EscHtml(book.Title)}</title></head>" +
            $"<body><nav epub:type=\"toc\"><h1>Contents</h1><ol>{navItems}</ol></nav></body></html>";

        var opf =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<package xmlns=\"http://www.idpf.org/2007/opf\" version=\"3.0\" unique-identifier=\"uid\">" +
            "<metadata xmlns:dc=\"http://purl.org/dc/elements/1.1/\">" +
            $"<dc:identifier id=\"uid\">urn:uuid:{uid}</dc:identifier>" +
            $"<dc:title>{EscHtml(book.Title)}</dc:title>" +
            "<dc:creator>ABook</dc:creator>" +
            "<dc:publisher>ABook</dc:publisher>" +
            $"<dc:language>{lang}</dc:language>" +
            $"<dc:date>{isoDate}</dc:date>" +
            $"{descTag}{subjectTags}" +
            $"<meta property=\"dcterms:modified\">{modified}</meta>" +
            (art.Cover is { } coverMeta ? $"<meta name=\"cover\" content=\"{coverMeta.Key}\"/>" : "") +
            "</metadata>" +
            $"<manifest>{string.Concat(manifestItems)}</manifest>" +
            $"<spine>{string.Concat(spineItems)}</spine>" +
            "</package>";

        const string container =
            "<?xml version=\"1.0\" encoding=\"UTF-8\"?>" +
            "<container version=\"1.0\" xmlns=\"urn:oasis:names:tc:opendocument:xmlns:container\">" +
            "<rootfiles><rootfile full-path=\"OEBPS/content.opf\" media-type=\"application/oebps-package+xml\"/></rootfiles>" +
            "</container>";

        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddZipEntry(zip, "mimetype",               "application/epub+zip", CompressionLevel.NoCompression);
            AddZipEntry(zip, "META-INF/container.xml", container);
            AddZipEntry(zip, "OEBPS/content.opf",      opf);
            AddZipEntry(zip, "OEBPS/nav.xhtml",        nav);
            foreach (var (name, content) in chapterFiles)
                AddZipEntry(zip, name, content);
            // Images are already compressed.
            foreach (var image in art.All)
            {
                var entry = zip.CreateEntry($"OEBPS/images/{image.FileName}", CompressionLevel.NoCompression);
                using var stream = entry.Open();
                stream.Write(image.Data);
            }
        }
        return ms.ToArray();
    }

    private static string XhtmlFigure(ExportImage image) =>
        $"<figure><img src=\"images/{image.FileName}\" alt=\"{EscHtml(image.Caption)}\"/>" +
        (image.Caption.Length > 0 ? $"<figcaption>{EscHtml(image.Caption)}</figcaption>" : "") + "</figure>\n";

    private static void AddZipEntry(ZipArchive zip, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        using var sw = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        sw.Write(content);
    }

    // ── Markdown converters ───────────────────────────────────────────────────

    /// <summary>
    /// Converts paragraph by paragraph (<see cref="ParagraphAnchors.Split"/>) so figures can follow the
    /// paragraph they illustrate. Paragraphs hold no blank lines, so the output is the same as converting
    /// the whole text at once.
    /// </summary>
    private static string WithFigures(string md, Func<string, string> convert, Func<int, string>? afterParagraph)
    {
        if (afterParagraph is null) return convert(md);
        var sb = new StringBuilder();
        var paragraphs = ParagraphAnchors.Split(md);
        for (int i = 0; i < paragraphs.Count; i++)
        {
            sb.Append(convert(paragraphs[i]));
            sb.Append(afterParagraph(i));
        }
        return sb.ToString();
    }

    internal static string MarkdownToHtml(string md, Func<int, string>? afterParagraph) =>
        WithFigures(md, MarkdownToHtml, afterParagraph);

    private static string MarkdownToXhtml(string md, Func<int, string>? afterParagraph) =>
        WithFigures(md, MarkdownToXhtml, afterParagraph);

    private static string MarkdownToFb2(string md, Func<int, string>? afterParagraph) =>
        WithFigures(md, MarkdownToFb2, afterParagraph);

    internal static string MarkdownToHtml(string md)
    {
        var sb = new StringBuilder();
        var inP = false;
        foreach (var line in md.Split('\n'))
        {
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (hm.Success)
            {
                if (inP) { sb.Append("</p>\n"); inP = false; }
                sb.Append($"<h{hm.Groups[1].Length}>{InlineMd(hm.Groups[2].Value)}</h{hm.Groups[1].Length}>\n");
                continue;
            }
            if (Regex.IsMatch(line.Trim(), @"^[-*]{3,}$")) { if (inP) { sb.Append("</p>\n"); inP = false; } sb.Append("<hr>\n"); continue; }
            if (string.IsNullOrWhiteSpace(line)) { if (inP) { sb.Append("</p>\n"); inP = false; } continue; }
            if (!inP) { sb.Append("<p>"); inP = true; } else sb.Append("<br>\n");
            sb.Append(InlineMd(line));
        }
        if (inP) sb.Append("</p>\n");
        return sb.ToString();
    }

    private static string MarkdownToXhtml(string md)
    {
        var sb = new StringBuilder();
        var inP = false;
        foreach (var line in md.Split('\n'))
        {
            var hm = Regex.Match(line, @"^(#{1,6})\s+(.+)$");
            if (hm.Success)
            {
                if (inP) { sb.Append("</p>\n"); inP = false; }
                sb.Append($"<h{hm.Groups[1].Length}>{InlineMd(hm.Groups[2].Value)}</h{hm.Groups[1].Length}>\n");
                continue;
            }
            if (Regex.IsMatch(line.Trim(), @"^[-*]{3,}$")) { if (inP) { sb.Append("</p>\n"); inP = false; } sb.Append("<hr/>\n"); continue; }
            if (string.IsNullOrWhiteSpace(line)) { if (inP) { sb.Append("</p>\n"); inP = false; } continue; }
            if (!inP) { sb.Append("<p>"); inP = true; } else sb.Append("\n");
            sb.Append(InlineMd(line));
        }
        if (inP) sb.Append("</p>\n");
        return sb.ToString();
    }

    private static string MarkdownToFb2(string md)
    {
        var sb = new StringBuilder();
        var inP = false;
        foreach (var line in md.Split('\n'))
        {
            if (Regex.IsMatch(line, @"^#{1,6}\s+"))
            {
                if (inP) { sb.Append("</p>\n"); inP = false; }
                sb.Append($"<subtitle><p>{EscXml(Regex.Replace(line, @"^#{1,6}\s+", ""))}</p></subtitle>\n");
                continue;
            }
            if (Regex.IsMatch(line.Trim(), @"^[-*]{3,}$")) { if (inP) { sb.Append("</p>\n"); inP = false; } sb.Append("<empty-line/>\n"); continue; }
            if (string.IsNullOrWhiteSpace(line)) { if (inP) { sb.Append("</p>\n"); inP = false; } continue; }
            if (!inP) { sb.Append("<p>"); inP = true; }
            sb.Append(InlineFb2(line));
        }
        if (inP) sb.Append("</p>\n");
        return sb.ToString();
    }

    internal static string InlineMd(string text)
    {
        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        text = Regex.Replace(text, @"\*\*\*(.+?)\*\*\*", "<strong><em>$1</em></strong>");
        text = Regex.Replace(text, @"___(.+?)___",        "<strong><em>$1</em></strong>");
        text = Regex.Replace(text, @"\*\*(.+?)\*\*",     "<strong>$1</strong>");
        text = Regex.Replace(text, @"__(.+?)__",          "<strong>$1</strong>");
        text = Regex.Replace(text, @"\*(.+?)\*",          "<em>$1</em>");
        text = Regex.Replace(text, @"_(.+?)_",            "<em>$1</em>");
        text = Regex.Replace(text, @"`(.+?)`",            "<code>$1</code>");
        text = Regex.Replace(text, @"~~(.+?)~~",          "<del>$1</del>");
        return text;
    }

    private static string InlineFb2(string text)
    {
        text = text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
        text = Regex.Replace(text, @"\*\*\*(.+?)\*\*\*", "<strong><emphasis>$1</emphasis></strong>");
        text = Regex.Replace(text, @"___(.+?)___",        "<strong><emphasis>$1</emphasis></strong>");
        text = Regex.Replace(text, @"\*\*(.+?)\*\*",     "<strong>$1</strong>");
        text = Regex.Replace(text, @"__(.+?)__",          "<strong>$1</strong>");
        text = Regex.Replace(text, @"\*(.+?)\*",          "<emphasis>$1</emphasis>");
        text = Regex.Replace(text, @"_(.+?)_",            "<emphasis>$1</emphasis>");
        text = Regex.Replace(text, @"`(.+?)`",            "$1");
        text = Regex.Replace(text, @"~~(.+?)~~",          "$1");
        return text;
    }

    // ── Shared helpers ────────────────────────────────────────────────────────

    internal static string EscHtml(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscXml(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    internal static string GetLang2(string language)
    {
        var l = (language ?? "").Trim();
        return (l.Length >= 2 ? l[..2] : l.PadRight(2, 'x')).ToLowerInvariant();
    }

    public static string SafeFilename(string title)
    {
        var sb = new StringBuilder();
        foreach (var c in title ?? "")
            sb.Append(TranslitMap.TryGetValue(c, out var r) ? r : c.ToString());

        var normalized = sb.ToString().Normalize(System.Text.NormalizationForm.FormD);
        var clean = new StringBuilder();
        foreach (var c in normalized)
        {
            if (System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c)
                != System.Globalization.UnicodeCategory.NonSpacingMark)
                clean.Append(c);
        }

        var safe = Regex.Replace(clean.ToString(), @"[^a-z0-9\s\-]", "", RegexOptions.IgnoreCase)
                        .Trim().Replace(' ', '-').ToLowerInvariant();
        return safe.Length > 0 ? safe : "book";
    }

    private static readonly Dictionary<char, string> TranslitMap = new()
    {
        ['а']="a",['б']="b",['в']="v",['г']="g",['д']="d",
        ['е']="e",['ё']="yo",['ж']="zh",['з']="z",['и']="i",
        ['й']="j",['к']="k",['л']="l",['м']="m",['н']="n",
        ['о']="o",['п']="p",['р']="r",['с']="s",['т']="t",
        ['у']="u",['ф']="f",['х']="kh",['ц']="ts",['ч']="ch",
        ['ш']="sh",['щ']="shch",['ъ']="",['ь']="",['э']="e",['ю']="yu",['я']="ya",
        ['А']="A",['Б']="B",['В']="V",['Г']="G",['Д']="D",
        ['Е']="E",['Ё']="Yo",['Ж']="Zh",['З']="Z",['И']="I",
        ['Й']="J",['К']="K",['Л']="L",['М']="M",['Н']="N",
        ['О']="O",['П']="P",['Р']="R",['С']="S",['Т']="T",
        ['У']="U",['Ф']="F",['Х']="Kh",['Ц']="Ts",['Ч']="Ch",
        ['Ш']="Sh",['Щ']="Shch",['Ъ']="",['Ь']="",['Э']="E",['Ю']="Yu",['Я']="Ya",
        ['α']="a",['β']="b",['γ']="g",['δ']="d",['ε']="e",['ζ']="z",['η']="e",['θ']="th",
        ['ι']="i",['κ']="k",['λ']="l",['μ']="m",['ν']="n",['ξ']="x",['ο']="o",['π']="p",
        ['ρ']="r",['σ']="s",['ς']="s",['τ']="t",['υ']="y",['φ']="ph",['χ']="ch",['ψ']="ps",['ω']="o",
        ['Α']="A",['Β']="B",['Γ']="G",['Δ']="D",['Ε']="E",['Ζ']="Z",['Η']="E",['Θ']="Th",
        ['Ι']="I",['Κ']="K",['Λ']="L",['Μ']="M",['Ν']="N",['Ξ']="X",['Ο']="O",['Π']="P",
        ['Ρ']="R",['Σ']="S",['Τ']="T",['Υ']="Y",['Φ']="Ph",['Χ']="Ch",['Ψ']="Ps",['Ω']="O",
    };

    private static string MapGenreToFb2(string genre)
    {
        var g = (genre ?? "").ToLowerInvariant();
        if (g.Contains("fantasy"))                             return "sf_fantasy";
        if (g.Contains("science") || g.Contains("sci-fi"))    return "sf";
        if (g.Contains("romance"))                             return "love";
        if (g.Contains("mystery") || g.Contains("detective")) return "detective";
        if (g.Contains("horror"))                              return "horror";
        if (g.Contains("thriller"))                            return "thriller";
        if (g.Contains("histor"))                              return "sf_history";
        if (g.Contains("child"))                               return "child";
        return "prose_contemporary";
    }
}
