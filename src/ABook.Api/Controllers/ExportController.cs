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

        if (format.Equals("metadata", StringComparison.OrdinalIgnoreCase))
        {
            var characters  = await _repo.GetCharacterCardsAsync(bookId);
            var plotThreads = await _repo.GetPlotThreadsAsync(bookId);
            var messages    = await _repo.GetMessagesAsync(bookId);
            var tokenUsage  = await _repo.GetTokenUsageAsync(bookId);
            return File(
                Encoding.UTF8.GetBytes(GenerateMetadataHtml(book, bible, characters, plotThreads, messages, tokenUsage)),
                "text/html; charset=utf-8",
                $"{safeName}-metadata.html");
        }

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

    // ── Metadata HTML ─────────────────────────────────────────────────────────

    private static readonly string MetaCss = @"
    :root { --bg: #ffffff; --fg: #1a1a1a; --fs: 18px; --lh: 1.75; --accent: #6366f1; --muted: #64748b; --border: rgba(128,128,128,0.15); }
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body { background: var(--bg); color: var(--fg); font-family: system-ui, -apple-system, sans-serif; font-size: var(--fs); line-height: var(--lh); transition: background 0.25s, color 0.25s; }
    #toolbar { position: sticky; top: 0; z-index: 100; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 8px 20px; background: var(--bg); border-bottom: 1px solid var(--border); font-family: system-ui,-apple-system,sans-serif; transition: background 0.25s; }
    .toolbar-label { font-size: 0.72em; opacity: 0.55; letter-spacing: 0.04em; text-transform: uppercase; margin-right: 2px; }
    .preset-btn { padding: 3px 10px; border-radius: 4px; cursor: pointer; font-size: 0.78em; font-weight: 600; white-space: nowrap; transition: opacity 0.15s; }
    .preset-btn:hover { opacity: 0.8; }
    .toolbar-sep { width: 1px; height: 22px; background: rgba(128,128,128,0.3); margin: 0 4px; }
    .size-btn { padding: 3px 9px; border-radius: 4px; cursor: pointer; font-weight: bold; background: transparent; color: var(--fg); border: 1px solid rgba(128,128,128,0.4); font-size: 0.85em; transition: opacity 0.15s; }
    .size-btn:hover { opacity: 0.65; }
    #font-size-label { font-size: 0.75em; min-width: 2.2em; text-align: center; opacity: 0.6; }
    main { padding: 48px 24px 96px; }
    header.doc-header { margin-bottom: 56px; padding-bottom: 24px; border-bottom: 2px solid var(--border); }
    header.doc-header h1 { font-size: 2em; margin-bottom: 6px; }
    header.doc-header .subtitle { opacity: 0.5; font-size: 0.9em; font-style: italic; }
    nav#toc { margin-bottom: 60px; padding: 24px 28px; border: 1px solid var(--border); border-radius: 6px; }
    nav#toc h2 { font-size: 1.1em; margin-bottom: 14px; text-transform: uppercase; letter-spacing: 0.08em; opacity: 0.65; }
    nav#toc ol { padding-left: 1.5em; }
    nav#toc li { margin: 7px 0; }
    nav#toc a { color: var(--fg); opacity: 0.82; text-decoration: none; border-bottom: 1px dotted rgba(128,128,128,0.5); transition: opacity 0.15s; }
    nav#toc a:hover { opacity: 1; }
    .md-section { margin-bottom: 56px; }
    .md-section h2 { font-size: 1.35em; margin-bottom: 20px; padding-bottom: 10px; border-bottom: 2px solid var(--border); }
    .md-section h3 { font-size: 1.05em; margin: 20px 0 8px; }
    .prop-list { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin-bottom: 16px; font-size: 0.9em; }
    .prop-list dt { font-weight: 600; opacity: 0.6; white-space: nowrap; }
    .prop-list dd { word-break: break-word; }
    .prop-block { margin-top: 14px; }
    .prop-block strong { display: block; font-size: 0.82em; text-transform: uppercase; letter-spacing: 0.04em; opacity: 0.55; margin-bottom: 6px; }
    .prop-block p { font-size: 0.95em; line-height: 1.7; }
    .pre-block { background: rgba(128,128,128,0.07); border: 1px solid var(--border); border-radius: 6px; padding: 10px 14px; font-family: 'Courier New', monospace; font-size: 0.82em; white-space: pre-wrap; word-break: break-word; }
    .empty-note { opacity: 0.5; font-style: italic; }
    .outline-card { background: rgba(128,128,128,0.04); border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 14px; }
    .outline-meta { display: flex; gap: 10px; align-items: center; margin-bottom: 10px; flex-wrap: wrap; }
    .outline-text { font-size: 0.9em; line-height: 1.65; margin-bottom: 8px; }
    .foretell { font-size: 0.85em; opacity: 0.75; margin-top: 6px; }
    .badge-status { font-size: 0.72em; padding: 2px 8px; border-radius: 999px; background: rgba(99,102,241,0.15); color: var(--accent); border: 1px solid rgba(99,102,241,0.3); font-weight: 600; }
    .badge-role { font-size: 0.72em; padding: 2px 8px; border-radius: 999px; font-weight: 600; }
    .role-protagonist { background: rgba(34,197,94,0.15); color: #16a34a; border: 1px solid rgba(34,197,94,0.3); }
    .role-antagonist { background: rgba(239,68,68,0.15); color: #b91c1c; border: 1px solid rgba(239,68,68,0.3); }
    .role-supporting { background: rgba(99,102,241,0.15); color: #4338ca; border: 1px solid rgba(99,102,241,0.3); }
    .role-minor { background: rgba(100,116,139,0.12); color: #475569; border: 1px solid rgba(100,116,139,0.3); }
    .char-block { border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 14px; }
    .char-header { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
    .char-header strong { font-size: 1.05em; }
    .badge-type { font-size: 0.72em; padding: 2px 8px; border-radius: 4px; background: rgba(99,102,241,0.12); color: var(--accent); font-weight: 600; }
    .badge-thread-status { font-size: 0.72em; padding: 2px 8px; border-radius: 999px; font-weight: 600; }
    .badge-thread-status.status-active { background: rgba(34,197,94,0.15); color: #16a34a; border: 1px solid rgba(34,197,94,0.3); }
    .badge-thread-status.status-resolved { background: rgba(100,116,139,0.12); color: #475569; border: 1px solid rgba(100,116,139,0.3); }
    .badge-thread-status.status-dormant { background: rgba(245,158,11,0.15); color: #b45309; border: 1px solid rgba(245,158,11,0.3); }
    .thread-block { border: 1px solid var(--border); border-radius: 8px; padding: 14px; margin-bottom: 12px; }
    .thread-header { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; flex-wrap: wrap; }
    .thread-header strong { font-size: 1em; }
    .thread-desc { font-size: 0.9em; opacity: 0.8; margin-bottom: 8px; line-height: 1.6; }
    .thread-meta { display: flex; gap: 12px; font-size: 0.8em; opacity: 0.6; }
    .msg-group { margin-bottom: 24px; }
    .msg-item { border-left: 3px solid var(--muted); padding: 8px 12px; margin-bottom: 8px; background: rgba(128,128,128,0.04); border-radius: 0 6px 6px 0; }
    .msg-meta { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; flex-wrap: wrap; }
    .msg-meta strong { font-size: 0.85em; }
    .msg-type-label { font-size: 0.75em; opacity: 0.6; }
    .msg-time { font-size: 0.72em; opacity: 0.5; margin-left: auto; }
    .msg-body { font-size: 0.88em; white-space: pre-wrap; word-break: break-word; opacity: 0.85; }
    .stats-table { width: 100%; border-collapse: collapse; font-size: 0.85em; }
    .stats-table th { text-align: left; padding: 6px 10px; border-bottom: 2px solid var(--border); font-size: 0.78em; text-transform: uppercase; letter-spacing: 0.04em; opacity: 0.6; }
    .stats-table td { padding: 5px 10px; border-bottom: 1px solid var(--border); }
    .stats-table tr:nth-child(even) td { background: rgba(128,128,128,0.04); }
    .stats-table .num { text-align: right; font-variant-numeric: tabular-nums; }
    .stats-total { margin-top: 12px; font-size: 0.9em; text-align: right; opacity: 0.7; }";

    private static readonly string MetaJs = @"    (function () {
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
        try { localStorage.setItem('abook-meta-preset', index); } catch(e) {}
      }
      function applyFontSize(size) {
        fontSize = Math.min(Math.max(size, 12), 36);
        document.documentElement.style.setProperty('--fs', fontSize + 'px');
        document.getElementById('font-size-label').textContent = fontSize;
        try { localStorage.setItem('abook-meta-fontsize', fontSize); } catch(e) {}
      }
      document.querySelectorAll('.preset-btn').forEach(function(btn, i) {
        btn.addEventListener('click', function() { applyPreset(i); });
      });
      document.getElementById('font-smaller').addEventListener('click', function() { applyFontSize(fontSize - 2); });
      document.getElementById('font-larger').addEventListener('click',  function() { applyFontSize(fontSize + 2); });
      try {
        var sp = localStorage.getItem('abook-meta-preset'); if (sp !== null) applyPreset(Number(sp));
        var sf = localStorage.getItem('abook-meta-fontsize'); if (sf !== null) applyFontSize(Number(sf));
      } catch(e) {}
    })();";

    private static string GenerateMetadataHtml(
        Book book,
        StoryBible? bible,
        IEnumerable<CharacterCard> characters,
        IEnumerable<PlotThread> plotThreads,
        IEnumerable<AgentMessage> messages,
        IEnumerable<TokenUsageRecord> tokenUsage)
    {
        var lang     = GetLang2(book.Language);
        var chapters = book.Chapters.OrderBy(c => c.Number).ToList();
        var charList = characters.OrderBy(c => c.Role).ToList();
        var threadList = plotThreads.ToList();
        var msgList  = messages.ToList();
        var tuList   = tokenUsage.ToList();

        var chapterMap = chapters.ToDictionary(c => c.Id);

        // ── ToC ──────────────────────────────────────────────────────────────
        var tocParts = new List<string>
        {
            "        <li><a href=\"#book-info\">Book Information</a></li>",
            "        <li><a href=\"#chapter-outlines\">Chapter Outlines</a></li>",
        };
        if (bible is not null && (!string.IsNullOrWhiteSpace(bible.SettingDescription) || !string.IsNullOrWhiteSpace(bible.Themes)))
            tocParts.Add("        <li><a href=\"#story-bible\">Story Bible</a></li>");
        if (charList.Count > 0)
            tocParts.Add($"        <li><a href=\"#characters\">Characters ({charList.Count})</a></li>");
        if (threadList.Count > 0)
            tocParts.Add($"        <li><a href=\"#plot-threads\">Plot Threads ({threadList.Count})</a></li>");
        if (msgList.Count > 0)
            tocParts.Add("        <li><a href=\"#agent-messages\">Agent Messages</a></li>");
        if (tuList.Count > 0)
            tocParts.Add("        <li><a href=\"#token-stats\">Token Statistics</a></li>");
        var toc = string.Join("\n", tocParts);

        // ── Book Info ────────────────────────────────────────────────────────
        var bookInfoHtml = $@"
<section id=""book-info"" class=""md-section"">
  <h2>Book Information</h2>
  <dl class=""prop-list"">
    <dt>Title</dt><dd>{EscHtml(book.Title)}</dd>
    <dt>Genre</dt><dd>{EscHtml(book.Genre)}</dd>
    <dt>Language</dt><dd>{EscHtml(book.Language.Length > 0 ? book.Language : "en")}</dd>
    <dt>Target Chapters</dt><dd>{book.TargetChapterCount}</dd>
    <dt>Status</dt><dd>{EscHtml(book.Status.ToString())}</dd>
    <dt>Created</dt><dd>{book.CreatedAt:yyyy-MM-dd HH:mm}</dd>
  </dl>
  {(book.Premise.Length > 0 ? $"<div class=\"prop-block\"><strong>Premise</strong><p>{EscHtml(book.Premise)}</p></div>" : "")}
</section>";

        // ── Chapter Outlines ──────────────────────────────────────────────────
        var outlineCards = chapters.Count == 0
            ? "<p class=\"empty-note\">No chapters yet.</p>"
            : string.Concat(chapters.Select(c =>
            {
                var titleSuffix = c.Title.Length > 0 ? $": {EscHtml(c.Title)}" : "";
                return $@"
  <div class=""outline-card"">
    <h3>Chapter {c.Number}{titleSuffix}</h3>
    <div class=""outline-meta"">
      <span class=""badge-status"">{EscHtml(c.Status.ToString())}</span>
      {(c.PovCharacter?.Length > 0 ? $"<span style=\"font-size:0.82em\"><strong>POV:</strong> {EscHtml(c.PovCharacter)}</span>" : "")}
    </div>
    {(c.Outline?.Length > 0 ? $"<div class=\"outline-text\">{EscHtml(c.Outline)}</div>" : "")}
    {(c.ForeshadowingNotes?.Length > 0 ? $"<div class=\"foretell\"><strong>Foreshadowing:</strong> {EscHtml(c.ForeshadowingNotes)}</div>" : "")}
    {(c.PayoffNotes?.Length > 0 ? $"<div class=\"foretell\"><strong>Payoff:</strong> {EscHtml(c.PayoffNotes)}</div>" : "")}
  </div>";
            }));
        var chapterOutlinesHtml = $@"
<section id=""chapter-outlines"" class=""md-section"">
  <h2>Chapter Outlines</h2>
  {outlineCards}
</section>";

        // ── Story Bible ──────────────────────────────────────────────────────
        var storyBibleHtml = "";
        if (bible is not null && (!string.IsNullOrWhiteSpace(bible.SettingDescription) || !string.IsNullOrWhiteSpace(bible.Themes)))
        {
            storyBibleHtml = $@"
<section id=""story-bible"" class=""md-section"">
  <h2>Story Bible</h2>
  <dl class=""prop-list"">
    {(bible.SettingDescription?.Length > 0 ? $"<dt>Setting</dt><dd>{EscHtml(bible.SettingDescription)}</dd>" : "")}
    {(bible.TimePeriod?.Length > 0 ? $"<dt>Time Period</dt><dd>{EscHtml(bible.TimePeriod)}</dd>" : "")}
    {(bible.Themes?.Length > 0 ? $"<dt>Themes</dt><dd>{EscHtml(bible.Themes)}</dd>" : "")}
    {(bible.ToneAndStyle?.Length > 0 ? $"<dt>Tone &amp; Style</dt><dd>{EscHtml(bible.ToneAndStyle)}</dd>" : "")}
  </dl>
  {(bible.WorldRules?.Length > 0 ? $"<div class=\"prop-block\"><strong>World Rules</strong><pre class=\"pre-block\">{EscHtml(bible.WorldRules)}</pre></div>" : "")}
  {(bible.Notes?.Length > 0 ? $"<div class=\"prop-block\"><strong>Notes</strong><pre class=\"pre-block\">{EscHtml(bible.Notes)}</pre></div>" : "")}
</section>";
        }

        // ── Characters ───────────────────────────────────────────────────────
        var charactersHtml = "";
        if (charList.Count > 0)
        {
            var cards = string.Concat(charList.Select(ch =>
            {
                var roleLower = ch.Role.ToString().ToLowerInvariant();
                return $@"
  <div class=""char-block"">
    <div class=""char-header""><strong>{EscHtml(ch.Name)}</strong><span class=""badge-role role-{roleLower}"">{EscHtml(ch.Role.ToString())}</span></div>
    <dl class=""prop-list"">
      {(ch.PhysicalDescription.Length > 0 ? $"<dt>Appearance</dt><dd>{EscHtml(ch.PhysicalDescription)}</dd>" : "")}
      {(ch.Personality.Length > 0 ? $"<dt>Personality</dt><dd>{EscHtml(ch.Personality)}</dd>" : "")}
      {(ch.Backstory.Length > 0 ? $"<dt>Backstory</dt><dd>{EscHtml(ch.Backstory)}</dd>" : "")}
      {(ch.GoalMotivation.Length > 0 ? $"<dt>Goal / Motivation</dt><dd>{EscHtml(ch.GoalMotivation)}</dd>" : "")}
      {(ch.Arc.Length > 0 ? $"<dt>Arc</dt><dd>{EscHtml(ch.Arc)}</dd>" : "")}
      {(ch.Notes.Length > 0 ? $"<dt>Notes</dt><dd>{EscHtml(ch.Notes)}</dd>" : "")}
      {(ch.FirstAppearanceChapterNumber.HasValue ? $"<dt>First Appears</dt><dd>Chapter {ch.FirstAppearanceChapterNumber}</dd>" : "")}
    </dl>
  </div>";
            }));
            charactersHtml = $@"
<section id=""characters"" class=""md-section"">
  <h2>Characters</h2>
  {cards}
</section>";
        }

        // ── Plot Threads ─────────────────────────────────────────────────────
        var plotThreadsHtml = "";
        if (threadList.Count > 0)
        {
            var threads = string.Concat(threadList.Select(t =>
            {
                var statusLower = t.Status.ToString().ToLowerInvariant();
                return $@"
  <div class=""thread-block"">
    <div class=""thread-header"">
      <strong>{EscHtml(t.Name)}</strong>
      <span class=""badge-type"">{EscHtml(t.Type.ToString())}</span>
      <span class=""badge-thread-status status-{statusLower}"">{EscHtml(t.Status.ToString())}</span>
    </div>
    {(t.Description.Length > 0 ? $"<p class=\"thread-desc\">{EscHtml(t.Description)}</p>" : "")}
    <div class=""thread-meta"">
      {(t.IntroducedChapterNumber.HasValue ? $"<span>Introduced: Ch.{t.IntroducedChapterNumber}</span>" : "")}
      {(t.ResolvedChapterNumber.HasValue ? $"<span>Resolved: Ch.{t.ResolvedChapterNumber}</span>" : "")}
    </div>
  </div>";
            }));
            plotThreadsHtml = $@"
<section id=""plot-threads"" class=""md-section"">
  <h2>Plot Threads</h2>
  {threads}
</section>";
        }

        // ── Agent Messages ───────────────────────────────────────────────────
        var messagesHtml = "";
        if (msgList.Count > 0)
        {
            static string MsgColor(MessageType t) => t switch
            {
                MessageType.Question   => "#f59e0b",
                MessageType.Answer     => "#22c55e",
                MessageType.Feedback   => "#6366f1",
                MessageType.SystemNote => "#475569",
                _                      => "#64748b",
            };

            string RenderMsgs(IEnumerable<AgentMessage> msgs) =>
                string.Concat(msgs.Select(m => $@"
    <div class=""msg-item"" style=""border-left-color:{MsgColor(m.MessageType)}"">
      <div class=""msg-meta"">
        <strong>{EscHtml(m.AgentRole.ToString())}</strong>
        <span class=""msg-type-label"">[{EscHtml(m.MessageType.ToString())}]</span>
        <span class=""msg-time"">{m.CreatedAt:yyyy-MM-dd HH:mm}</span>
      </div>
      <div class=""msg-body"">{EscHtml(m.Content)}</div>
    </div>"));

            var grouped = msgList.GroupBy(m => m.ChapterId).ToList();
            var groups  = new StringBuilder();

            // Per-chapter groups ordered by chapter number
            foreach (var ch in chapters)
            {
                var group = grouped.FirstOrDefault(g => g.Key == ch.Id);
                if (group is null) continue;
                var titleSuffix = ch.Title.Length > 0 ? $": {EscHtml(ch.Title)}" : "";
                groups.Append($@"
  <div class=""msg-group"">
    <h3>Chapter {ch.Number}{titleSuffix}</h3>
    {RenderMsgs(group)}
  </div>");
            }
            // General messages (no chapter)
            var general = grouped.FirstOrDefault(g => g.Key is null);
            if (general is not null)
                groups.Append($@"
  <div class=""msg-group"">
    <h3>General</h3>
    {RenderMsgs(general)}
  </div>");

            messagesHtml = $@"
<section id=""agent-messages"" class=""md-section"">
  <h2>Agent Messages</h2>
  {groups}
</section>";
        }

        // ── Token Stats ──────────────────────────────────────────────────────
        var tokenStatsHtml = "";
        if (tuList.Count > 0)
        {
            var total = tuList.Sum(r => (long)r.PromptTokens + r.CompletionTokens);
            var rows = string.Concat(tuList.Select(r =>
            {
                var chLabel = r.ChapterId.HasValue && chapterMap.TryGetValue(r.ChapterId.Value, out var ch)
                    ? $"Ch.{ch.Number}"
                    : r.ChapterId.HasValue ? $"Ch.{r.ChapterId}" : "—";
                return $@"
      <tr>
        <td>{r.CreatedAt:yyyy-MM-dd HH:mm}</td>
        <td>{EscHtml(r.AgentRole.ToString())}</td>
        <td>{EscHtml(chLabel)}</td>
        <td class=""num"">{r.PromptTokens:N0}</td>
        <td class=""num"">{r.CompletionTokens:N0}</td>
        <td class=""num"">{(r.PromptTokens + r.CompletionTokens):N0}</td>
      </tr>";
            }));
            var callWord = tuList.Count == 1 ? "call" : "calls";
            tokenStatsHtml = $@"
<section id=""token-stats"" class=""md-section"">
  <h2>Token Statistics</h2>
  <table class=""stats-table"">
    <thead><tr><th>Time</th><th>Agent</th><th>Chapter</th><th>Prompt</th><th>Completion</th><th>Total</th></tr></thead>
    <tbody>{rows}</tbody>
  </table>
  <p class=""stats-total"">Grand total: <strong>{total:N0}</strong> tokens across {tuList.Count} {callWord}</p>
</section>";
        }

        // ── Preset buttons ───────────────────────────────────────────────────
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
        sb.Append($"  <title>{EscHtml(book.Title)} \u2014 Metadata</title>\n");
        sb.Append("  <style>"); sb.Append(MetaCss); sb.Append("\n  </style>\n</head>\n<body>\n");
        sb.Append("  <div id=\"toolbar\">\n");
        sb.Append("    <span class=\"toolbar-label\">Theme</span>\n");
        sb.Append($"    {presetBtns}\n");
        sb.Append("    <div class=\"toolbar-sep\"></div>\n");
        sb.Append("    <span class=\"toolbar-label\">Size</span>\n");
        sb.Append("    <button class=\"size-btn\" id=\"font-smaller\" title=\"Decrease font size\">A\u2212</button>\n");
        sb.Append("    <span id=\"font-size-label\">18</span>\n");
        sb.Append("    <button class=\"size-btn\" id=\"font-larger\" title=\"Increase font size\">A+</button>\n");
        sb.Append("  </div>\n  <main>\n");
        sb.Append("    <header class=\"doc-header\">\n");
        sb.Append($"      <h1>{EscHtml(book.Title)}</h1>\n");
        sb.Append($"      <p class=\"subtitle\">Metadata &amp; Planning Document &middot; {genreMeta}</p>\n");
        sb.Append("    </header>\n    <nav id=\"toc\">\n      <h2>Contents</h2>\n      <ol>\n");
        sb.Append(toc);
        sb.Append("\n      </ol>\n    </nav>\n");
        sb.Append(bookInfoHtml);
        sb.Append(storyBibleHtml);
        sb.Append(chapterOutlinesHtml);
        sb.Append(charactersHtml);
        sb.Append(plotThreadsHtml);
        sb.Append(messagesHtml);
        sb.Append(tokenStatsHtml);
        sb.Append("\n  </main>\n  <script>\n");
        sb.Append(MetaJs);
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
        // Transliterate non-Latin scripts to ASCII equivalents
        var sb = new StringBuilder();
        foreach (var c in title ?? "")
            sb.Append(TranslitMap.TryGetValue(c, out var r) ? r : c.ToString());

        // Decompose accented Latin characters (é → e + combining mark) then strip combining marks
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
        // Cyrillic (Russian / Ukrainian / Belarusian)
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
        // Greek
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
