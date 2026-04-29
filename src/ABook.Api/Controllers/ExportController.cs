using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using ABook.Core.Interfaces;
using ABook.Core.Models;
using Microsoft.AspNetCore.Mvc;

namespace ABook.Api.Controllers;

[ApiController]
[Route("api/books/{bookId:int}/export")]
public class ExportController : ControllerBase
{
    private readonly IBookRepository _repo;
    public ExportController(IBookRepository repo) => _repo = repo;

    private int? CurrentUserId =>
        User.Identity?.IsAuthenticated == true
            ? int.Parse(User.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)!.Value)
            : (int?)null;

    [HttpGet]
    public async Task<IActionResult> Export(int bookId, [FromQuery] string format = "html")
    {
        var book = await _repo.GetByIdWithDetailsAsync(bookId);
        if (book is null) return NotFound();
        if (book.UserId is not null && book.UserId != CurrentUserId) return Forbid();

        var bible = await _repo.GetStoryBibleAsync(bookId);
        var safeName = SafeFilename(book.Title);

        return format.ToLowerInvariant() switch
        {
            "fb2"  => File(Encoding.UTF8.GetBytes(GenerateFb2(book, bible)),
                          "application/x-fictionbook+xml; charset=utf-8",
                          $"{safeName}.fb2"),
            "epub" => File(GenerateEpub(book, bible),
                          "application/epub+zip",
                          $"{safeName}.epub"),
            _      => File(Encoding.UTF8.GetBytes(GenerateHtml(book)),
                          "text/html; charset=utf-8",
                          $"{safeName}.html"),
        };
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
    main { padding: 48px 24px 96px; max-width: 760px; margin: 0 auto; }
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
    .chapter del { text-decoration: line-through; opacity: 0.55; }";

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

    private static string GenerateHtml(Book book)
    {
        var chapters = book.Chapters
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
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
                   $"  {MarkdownToHtml(c.Content)}\n</section>\n";
        }));

        // Preset buttons
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
        sb.Append("    </header>\n    <nav id=\"toc\">\n      <h2>Contents</h2>\n      <ol>\n");
        sb.Append(toc);
        sb.Append("\n      </ol>\n    </nav>\n");
        sb.Append(chapHtml);
        sb.Append("  </main>\n  <script>\n");
        sb.Append(HtmlJs);
        sb.Append("\n  </script>\n</body>\n</html>\n");
        return sb.ToString();
    }

    // ── FB2 ───────────────────────────────────────────────────────────────────

    private static string GenerateFb2(Book book, StoryBible? bible)
    {
        var chapters = book.Chapters
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Number)
            .ToList();

        var lang  = GetLang2(book.Language);
        var year  = (book.CreatedAt != default ? book.CreatedAt : DateTime.UtcNow).Year.ToString();

        // Annotation: premise + setting + tone
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

        // Keywords: genre + themes
        var kwParts = new List<string>();
        if (!string.IsNullOrWhiteSpace(book.Genre))   kwParts.Add(book.Genre);
        if (!string.IsNullOrWhiteSpace(bible?.Themes)) kwParts.Add(bible!.Themes);
        var kwXml = kwParts.Count > 0
            ? $"<keywords>{EscXml(string.Join(", ", kwParts))}</keywords>"
            : "";

        var sections = string.Concat(chapters.Select(c =>
        {
            var suffix = c.Title.Length > 0 ? ": " + EscXml(c.Title) : "";
            return $"\n  <section id=\"chapter-{c.Number}\">\n" +
                   $"    <title><p>Chapter {c.Number}{suffix}</p></title>\n" +
                   $"    {MarkdownToFb2(c.Content)}\n  </section>";
        }));

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-8\"?>");
        sb.AppendLine("<FictionBook xmlns=\"http://www.gribuser.ru/xml/fictionbook/2.0\" xmlns:l=\"http://www.w3.org/1999/xlink\">");
        sb.AppendLine("  <description>");
        sb.AppendLine("    <title-info>");
        sb.AppendLine($"      <genre>{MapGenreToFb2(book.Genre)}</genre>");
        sb.AppendLine("      <author><nickname>ABook</nickname></author>");
        sb.AppendLine($"      <book-title>{EscXml(book.Title)}</book-title>");
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
        sb.AppendLine("</FictionBook>");
        return sb.ToString();
    }

    // ── EPUB ──────────────────────────────────────────────────────────────────

    private static byte[] GenerateEpub(Book book, StoryBible? bible)
    {
        var chapters = book.Chapters
            .Where(c => !string.IsNullOrWhiteSpace(c.Content))
            .OrderBy(c => c.Number)
            .ToList();

        var lang     = GetLang2(book.Language);
        var uid      = Guid.NewGuid().ToString();
        var modified = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ");
        var isoDate  = (book.CreatedAt != default ? book.CreatedAt : DateTime.UtcNow).ToString("yyyy-MM-dd");

        const string chStyle =
            "body{font-family:serif;font-size:1em;line-height:1.6;margin:1em 2em}" +
            "p{margin-bottom:.8em;text-align:justify}h2,h3,h4{font-family:sans-serif}";

        // Dublin Core subject tags
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
                $"<body><h2>{heading}</h2>{MarkdownToXhtml(c.Content)}</body></html>";
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
            // mimetype MUST be the first entry and MUST be stored (CompressionLevel.NoCompression = method 0)
            AddZipEntry(zip, "mimetype",               "application/epub+zip", CompressionLevel.NoCompression);
            AddZipEntry(zip, "META-INF/container.xml", container);
            AddZipEntry(zip, "OEBPS/content.opf",      opf);
            AddZipEntry(zip, "OEBPS/nav.xhtml",        nav);
            foreach (var (name, content) in chapterFiles)
                AddZipEntry(zip, name, content);
        }
        return ms.ToArray();
    }

    private static void AddZipEntry(ZipArchive zip, string name, string content,
        CompressionLevel level = CompressionLevel.Optimal)
    {
        var entry = zip.CreateEntry(name, level);
        // Write without BOM — EPUB readers are strict about encoding markers
        using var sw = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        sw.Write(content);
    }

    // ── Markdown converters ───────────────────────────────────────────────────

    private static string MarkdownToHtml(string md)
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

    // ── Inline formatters ─────────────────────────────────────────────────────

    private static string InlineMd(string text)
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

    private static string EscHtml(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string EscXml(string? text) =>
        (text ?? "").Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    private static string GetLang2(string language)
    {
        var l = (language ?? "").Trim();
        return (l.Length >= 2 ? l[..2] : l.PadRight(2, 'x')).ToLowerInvariant();
    }

    private static string SafeFilename(string title)
    {
        var safe = Regex.Replace(title ?? "", @"[^a-z0-9\s\-]", "", RegexOptions.IgnoreCase)
                        .Trim().Replace(' ', '-').ToLowerInvariant();
        return safe.Length > 0 ? safe : "book";
    }

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
