import type { Book } from '../api'

function escapeHtml(str: string): string {
  return str
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;')
}

function inlineMd(text: string): string {
  return text
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/\*\*\*(.+?)\*\*\*/g, '<strong><em>$1</em></strong>')
    .replace(/___(.+?)___/g, '<strong><em>$1</em></strong>')
    .replace(/\*\*(.+?)\*\*/g, '<strong>$1</strong>')
    .replace(/__(.+?)__/g, '<strong>$1</strong>')
    .replace(/\*(.+?)\*/g, '<em>$1</em>')
    .replace(/_(.+?)_/g, '<em>$1</em>')
    .replace(/`(.+?)`/g, '<code>$1</code>')
    .replace(/~~(.+?)~~/g, '<del>$1</del>')
}

function markdownToHtml(md: string): string {
  const lines = md.split('\n')
  let html = ''
  let inParagraph = false

  for (const line of lines) {
    const headerMatch = line.match(/^(#{1,6})\s+(.+)$/)
    if (headerMatch) {
      if (inParagraph) { html += '</p>\n'; inParagraph = false }
      const level = headerMatch[1].length
      html += `<h${level}>${inlineMd(headerMatch[2])}</h${level}>\n`
      continue
    }

    if (/^[-*]{3,}$/.test(line.trim())) {
      if (inParagraph) { html += '</p>\n'; inParagraph = false }
      html += '<hr>\n'
      continue
    }

    if (!line.trim()) {
      if (inParagraph) { html += '</p>\n'; inParagraph = false }
      continue
    }

    if (!inParagraph) { html += '<p>'; inParagraph = true }
    else html += '<br>\n'
    html += inlineMd(line)
  }

  if (inParagraph) html += '</p>\n'
  return html
}

interface Preset { name: string; bg: string; fg: string }

const PRESETS: Preset[] = [
  { name: 'Light',      bg: '#ffffff', fg: '#1a1a1a' },
  { name: 'Dark',       bg: '#1c1c1e', fg: '#e8e8e8' },
  { name: 'Hi-Contrast',bg: '#000000', fg: '#ffff00' },
  { name: 'Sepia',      bg: '#f4ecd8', fg: '#5c4a1e' },
  { name: 'Solarized',  bg: '#002b36', fg: '#93a1a1' },
  { name: 'Retro',      bg: '#0d0d0d', fg: '#00e676' },
]

export function generateBookHtml(book: Book): string {
  const chapters = (book.chapters ?? []).filter(c => c.content?.trim())

  const tocItems = chapters.map(c =>
    `        <li><a href="#chapter-${c.number}">Chapter ${c.number}${c.title ? ': ' + escapeHtml(c.title) : ''}</a></li>`
  ).join('\n')

  const chaptersHtml = chapters.map(c => `
<section id="chapter-${c.number}" class="chapter">
  <h2>Chapter ${c.number}${c.title ? ': ' + escapeHtml(c.title) : ''}</h2>
  ${markdownToHtml(c.content ?? '')}
</section>`).join('\n')

  const presetsJson = JSON.stringify(PRESETS)

  const presetBtns = PRESETS.map((p, i) => {
    const border = p.fg === '#1a1a1a' ? '#aaaaaa' : p.fg
    return `<button class="preset-btn" data-index="${i}" style="background:${p.bg};color:${p.fg};border:2px solid ${border}" title="${p.name}">${p.name}</button>`
  }).join('\n    ')

  return `<!DOCTYPE html>
<html lang="${escapeHtml(book.language || 'en')}">
<head>
  <meta charset="UTF-8">
  <meta name="viewport" content="width=device-width, initial-scale=1.0">
  <title>${escapeHtml(book.title)}</title>
  <style>
    :root {
      --bg: #ffffff;
      --fg: #1a1a1a;
      --fs: 18px;
      --lh: 1.75;
      --max-w: 740px;
    }
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body {
      background: var(--bg);
      color: var(--fg);
      font-family: Georgia, 'Times New Roman', serif;
      font-size: var(--fs);
      line-height: var(--lh);
      transition: background 0.25s, color 0.25s;
    }
    #toolbar {
      position: sticky;
      top: 0;
      z-index: 100;
      display: flex;
      align-items: center;
      gap: 8px;
      flex-wrap: wrap;
      padding: 8px 20px;
      background: var(--bg);
      border-bottom: 1px solid rgba(128,128,128,0.25);
      font-family: system-ui, -apple-system, sans-serif;
      transition: background 0.25s;
    }
    .toolbar-label {
      font-size: 0.72em;
      opacity: 0.55;
      letter-spacing: 0.04em;
      text-transform: uppercase;
      margin-right: 2px;
    }
    .preset-btn {
      padding: 3px 10px;
      border-radius: 4px;
      cursor: pointer;
      font-size: 0.78em;
      font-family: system-ui, -apple-system, sans-serif;
      font-weight: 600;
      white-space: nowrap;
      transition: opacity 0.15s;
    }
    .preset-btn:hover { opacity: 0.8; }
    .toolbar-sep {
      width: 1px;
      height: 22px;
      background: rgba(128,128,128,0.3);
      margin: 0 4px;
    }
    .size-btn {
      padding: 3px 9px;
      border-radius: 4px;
      cursor: pointer;
      font-weight: bold;
      background: transparent;
      color: var(--fg);
      border: 1px solid rgba(128,128,128,0.4);
      font-family: system-ui, -apple-system, sans-serif;
      font-size: 0.85em;
      transition: opacity 0.15s;
    }
    .size-btn:hover { opacity: 0.65; }
    #font-size-label {
      font-size: 0.75em;
      min-width: 2.2em;
      text-align: center;
      font-family: system-ui, -apple-system, sans-serif;
      opacity: 0.6;
    }
    main {
      max-width: var(--max-w);
      margin: 0 auto;
      padding: 48px 24px 96px;
    }
    header.book-header {
      margin-bottom: 56px;
      text-align: center;
      padding-bottom: 32px;
      border-bottom: 2px solid rgba(128,128,128,0.15);
    }
    header.book-header h1 {
      font-size: 2.4em;
      margin-bottom: 10px;
      letter-spacing: -0.01em;
    }
    header.book-header .meta {
      opacity: 0.5;
      font-style: italic;
      font-size: 0.95em;
    }
    nav#toc {
      margin-bottom: 60px;
      padding: 24px 28px;
      border: 1px solid rgba(128,128,128,0.2);
      border-radius: 6px;
    }
    nav#toc h2 {
      font-size: 1.1em;
      margin-bottom: 14px;
      text-transform: uppercase;
      letter-spacing: 0.08em;
      opacity: 0.65;
      font-family: system-ui, -apple-system, sans-serif;
    }
    nav#toc ol { padding-left: 1.5em; }
    nav#toc li { margin: 7px 0; }
    nav#toc a {
      color: var(--fg);
      opacity: 0.82;
      text-decoration: none;
      border-bottom: 1px dotted rgba(128,128,128,0.5);
      transition: opacity 0.15s;
    }
    nav#toc a:hover { opacity: 1; }
    .chapter { margin-bottom: 72px; }
    .chapter h2 {
      font-size: 1.65em;
      margin-bottom: 28px;
      padding-bottom: 12px;
      border-bottom: 2px solid rgba(128,128,128,0.15);
    }
    .chapter h3 { font-size: 1.3em; margin: 28px 0 12px; }
    .chapter h4 { font-size: 1.1em; margin: 22px 0 10px; }
    .chapter p { margin-bottom: 1.1em; text-align: justify; }
    .chapter hr {
      border: none;
      border-top: 1px solid rgba(128,128,128,0.25);
      margin: 2.5em auto;
      width: 40%;
    }
    .chapter strong { font-weight: 700; }
    .chapter em { font-style: italic; }
    .chapter code {
      font-family: 'Courier New', Courier, monospace;
      font-size: 0.88em;
      opacity: 0.85;
    }
    .chapter del { text-decoration: line-through; opacity: 0.55; }
  </style>
</head>
<body>
  <div id="toolbar">
    <span class="toolbar-label">Theme</span>
    ${presetBtns}
    <div class="toolbar-sep"></div>
    <span class="toolbar-label">Size</span>
    <button class="size-btn" id="font-smaller" title="Decrease font size">A−</button>
    <span id="font-size-label">18</span>
    <button class="size-btn" id="font-larger" title="Increase font size">A+</button>
  </div>
  <main>
    <header class="book-header">
      <h1>${escapeHtml(book.title)}</h1>
      <p class="meta">${escapeHtml(book.genre)}${book.language ? ' &middot; ' + escapeHtml(book.language) : ''}</p>
    </header>
    <nav id="toc">
      <h2>Contents</h2>
      <ol>
${tocItems}
      </ol>
    </nav>
${chaptersHtml}
  </main>
  <script>
    (function () {
      var presets = ${presetsJson};
      var fontSize = 18;

      function applyPreset(index) {
        var p = presets[index];
        var root = document.documentElement;
        root.style.setProperty('--bg', p.bg);
        root.style.setProperty('--fg', p.fg);
        document.body.style.background = p.bg;
        document.body.style.color = p.fg;
        var toolbar = document.getElementById('toolbar');
        toolbar.style.background = p.bg;
        document.querySelectorAll('.size-btn').forEach(function (b) {
          b.style.color = p.fg;
        });
        document.getElementById('font-size-label').style.color = p.fg;
        try { localStorage.setItem('abook-preset', index); } catch(e) {}
      }

      function applyFontSize(size) {
        fontSize = Math.min(Math.max(size, 12), 36);
        document.documentElement.style.setProperty('--fs', fontSize + 'px');
        document.getElementById('font-size-label').textContent = fontSize;
        try { localStorage.setItem('abook-fontsize', fontSize); } catch(e) {}
      }

      document.querySelectorAll('.preset-btn').forEach(function (btn, i) {
        btn.addEventListener('click', function () { applyPreset(i); });
      });

      document.getElementById('font-smaller').addEventListener('click', function () {
        applyFontSize(fontSize - 2);
      });
      document.getElementById('font-larger').addEventListener('click', function () {
        applyFontSize(fontSize + 2);
      });

      try {
        var savedPreset = localStorage.getItem('abook-preset');
        if (savedPreset !== null) applyPreset(Number(savedPreset));
        var savedSize = localStorage.getItem('abook-fontsize');
        if (savedSize !== null) applyFontSize(Number(savedSize));
      } catch(e) {}
    })();
  </script>
</body>
</html>`
}

export function downloadBookAsHtml(book: Book): void {
  const html = generateBookHtml(book)
  const blob = new Blob([html], { type: 'text/html;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  const safeName = book.title
    .replace(/[^a-z0-9\s-]/gi, '')
    .trim()
    .replace(/\s+/g, '-')
    .toLowerCase() || 'book'
  a.download = `${safeName}.html`
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}
