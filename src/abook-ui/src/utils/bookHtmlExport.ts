import type { Book, StoryBible, CharacterCard, PlotThread, AgentMessage } from '../api'

export interface TokenStatEntry {
  id: number
  chapterId: number | null
  role: string
  prompt: number
  completion: number
  time: string
}

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

// ── Metadata export ──────────────────────────────────────────────

export function generateBookMetadataHtml(
  book: Book,
  storyBible: StoryBible | null,
  characters: CharacterCard[],
  plotThreads: PlotThread[],
  messages: AgentMessage[],
  tokenStats: TokenStatEntry[]
): string {
  const chapters = (book.chapters ?? []).slice().sort((a, b) => a.number - b.number)

  // ToC sections
  const tocSections: { id: string; label: string }[] = [
    { id: 'book-info', label: 'Book Information' },
    { id: 'chapter-outlines', label: 'Chapter Outlines' },
  ]
  if (storyBible?.settingDescription || storyBible?.timePeriod || storyBible?.themes)
    tocSections.push({ id: 'story-bible', label: 'Story Bible' })
  if (characters.length > 0)
    tocSections.push({ id: 'characters', label: `Characters (${characters.length})` })
  if (plotThreads.length > 0)
    tocSections.push({ id: 'plot-threads', label: `Plot Threads (${plotThreads.length})` })
  if (messages.length > 0)
    tocSections.push({ id: 'agent-messages', label: 'Agent Messages' })
  if (tokenStats.length > 0)
    tocSections.push({ id: 'token-stats', label: 'Token Statistics' })

  const tocItems = tocSections.map(s =>
    `        <li><a href="#${s.id}">${escapeHtml(s.label)}</a></li>`
  ).join('\n')

  // Book Information
  const bookInfoHtml = `
<section id="book-info" class="md-section">
  <h2>Book Information</h2>
  <dl class="prop-list">
    <dt>Title</dt><dd>${escapeHtml(book.title)}</dd>
    <dt>Genre</dt><dd>${escapeHtml(book.genre)}</dd>
    <dt>Language</dt><dd>${escapeHtml(book.language || 'en')}</dd>
    <dt>Target Chapters</dt><dd>${book.targetChapterCount}</dd>
    <dt>Status</dt><dd>${escapeHtml(book.status)}</dd>
    <dt>Created</dt><dd>${new Date(book.createdAt).toLocaleString()}</dd>
  </dl>
  ${book.premise ? `<div class="prop-block"><strong>Premise</strong><p>${escapeHtml(book.premise)}</p></div>` : ''}
</section>`

  // Chapter Outlines
  const chapterOutlinesHtml = `
<section id="chapter-outlines" class="md-section">
  <h2>Chapter Outlines</h2>
  ${chapters.length === 0 ? '<p class="empty-note">No chapters yet.</p>' : chapters.map(c => `
  <div class="outline-card">
    <h3>Chapter ${c.number}${c.title ? ': ' + escapeHtml(c.title) : ''}</h3>
    <div class="outline-meta">
      <span class="badge-status">${escapeHtml(c.status)}</span>
      ${c.povCharacter ? `<span class="meta-item"><strong>POV:</strong> ${escapeHtml(c.povCharacter)}</span>` : ''}
    </div>
    ${c.outline ? `<div class="outline-text">${escapeHtml(c.outline)}</div>` : ''}
    ${c.foreshadowingNotes ? `<div class="foretell"><strong>Foreshadowing:</strong> ${escapeHtml(c.foreshadowingNotes)}</div>` : ''}
    ${c.payoffNotes ? `<div class="foretell"><strong>Payoff:</strong> ${escapeHtml(c.payoffNotes)}</div>` : ''}
  </div>`).join('\n')}
</section>`

  // Story Bible
  const storyBibleHtml = (storyBible?.settingDescription || storyBible?.timePeriod || storyBible?.themes)
    ? `
<section id="story-bible" class="md-section">
  <h2>Story Bible</h2>
  <dl class="prop-list">
    ${storyBible.settingDescription ? `<dt>Setting</dt><dd>${escapeHtml(storyBible.settingDescription)}</dd>` : ''}
    ${storyBible.timePeriod ? `<dt>Time Period</dt><dd>${escapeHtml(storyBible.timePeriod)}</dd>` : ''}
    ${storyBible.themes ? `<dt>Themes</dt><dd>${escapeHtml(storyBible.themes)}</dd>` : ''}
    ${storyBible.toneAndStyle ? `<dt>Tone &amp; Style</dt><dd>${escapeHtml(storyBible.toneAndStyle)}</dd>` : ''}
  </dl>
  ${storyBible.worldRules ? `<div class="prop-block"><strong>World Rules</strong><pre class="pre-block">${escapeHtml(storyBible.worldRules)}</pre></div>` : ''}
  ${storyBible.notes ? `<div class="prop-block"><strong>Notes</strong><pre class="pre-block">${escapeHtml(storyBible.notes)}</pre></div>` : ''}
</section>` : ''

  // Characters
  const roleOrder: Record<string, number> = { Protagonist: 0, Antagonist: 1, Supporting: 2, Minor: 3 }
  const sortedChars = [...characters].sort((a, b) => (roleOrder[a.role] ?? 4) - (roleOrder[b.role] ?? 4))
  const charactersHtml = characters.length > 0
    ? `
<section id="characters" class="md-section">
  <h2>Characters</h2>
  ${sortedChars.map(ch => `
  <div class="char-block">
    <div class="char-header"><strong>${escapeHtml(ch.name)}</strong><span class="badge-role role-${(ch.role || '').toLowerCase()}">${escapeHtml(ch.role)}</span></div>
    <dl class="prop-list">
      ${ch.physicalDescription ? `<dt>Appearance</dt><dd>${escapeHtml(ch.physicalDescription)}</dd>` : ''}
      ${ch.personality ? `<dt>Personality</dt><dd>${escapeHtml(ch.personality)}</dd>` : ''}
      ${ch.backstory ? `<dt>Backstory</dt><dd>${escapeHtml(ch.backstory)}</dd>` : ''}
      ${ch.goalMotivation ? `<dt>Goal / Motivation</dt><dd>${escapeHtml(ch.goalMotivation)}</dd>` : ''}
      ${ch.arc ? `<dt>Arc</dt><dd>${escapeHtml(ch.arc)}</dd>` : ''}
      ${ch.notes ? `<dt>Notes</dt><dd>${escapeHtml(ch.notes)}</dd>` : ''}
      ${ch.firstAppearanceChapterNumber != null ? `<dt>First Appears</dt><dd>Chapter ${ch.firstAppearanceChapterNumber}</dd>` : ''}
    </dl>
  </div>`).join('\n')}
</section>` : ''

  // Plot Threads
  const plotThreadsHtml = plotThreads.length > 0
    ? `
<section id="plot-threads" class="md-section">
  <h2>Plot Threads</h2>
  ${plotThreads.map(t => `
  <div class="thread-block status-${(t.status || '').toLowerCase()}">
    <div class="thread-header">
      <strong>${escapeHtml(t.name)}</strong>
      <span class="badge-type">${escapeHtml(t.type)}</span>
      <span class="badge-thread-status status-${(t.status || '').toLowerCase()}">${escapeHtml(t.status)}</span>
    </div>
    ${t.description ? `<p class="thread-desc">${escapeHtml(t.description)}</p>` : ''}
    <div class="thread-meta">
      ${t.introducedChapterNumber != null ? `<span>Introduced: Ch.${t.introducedChapterNumber}</span>` : ''}
      ${t.resolvedChapterNumber != null ? `<span>Resolved: Ch.${t.resolvedChapterNumber}</span>` : ''}
    </div>
  </div>`).join('\n')}
</section>` : ''

  // Agent Messages — grouped by chapter
  const messagesByChapter = new Map<string, AgentMessage[]>()
  for (const m of messages) {
    const key = m.chapterId != null ? String(m.chapterId) : '__general__'
    if (!messagesByChapter.has(key)) messagesByChapter.set(key, [])
    messagesByChapter.get(key)!.push(m)
  }

  const chapterMap = new Map(chapters.map(c => [c.id, c]))

  const messageTypeColor: Record<string, string> = {
    Question: '#f59e0b', Answer: '#22c55e', Feedback: '#6366f1',
    SystemNote: '#475569', Content: '#64748b',
  }

  let messagesHtml = ''
  if (messages.length > 0) {
    const renderMessages = (msgs: AgentMessage[]) =>
      msgs.map(m => {
        const color = messageTypeColor[m.messageType] || '#64748b'
        return `<div class="msg-item" style="border-left-color:${color}">
          <div class="msg-meta">
            <strong>${escapeHtml(m.agentRole)}</strong>
            <span class="msg-type-label">[${escapeHtml(m.messageType)}]</span>
            <span class="msg-time">${new Date(m.createdAt).toLocaleString()}</span>
          </div>
          <div class="msg-body">${escapeHtml(m.content)}</div>
        </div>`
      }).join('\n')

    const chapterGroups: string[] = []

    // Per-chapter message groups ordered by chapter number
    for (const ch of chapters) {
      const msgs = messagesByChapter.get(String(ch.id))
      if (msgs && msgs.length > 0) {
        chapterGroups.push(`
  <div class="msg-group">
    <h3>Chapter ${ch.number}${ch.title ? ': ' + escapeHtml(ch.title) : ''}</h3>
    ${renderMessages(msgs)}
  </div>`)
      }
    }

    // General (no chapter) messages
    const generalMsgs = messagesByChapter.get('__general__')
    if (generalMsgs && generalMsgs.length > 0) {
      chapterGroups.push(`
  <div class="msg-group">
    <h3>General</h3>
    ${renderMessages(generalMsgs)}
  </div>`)
    }

    messagesHtml = `
<section id="agent-messages" class="md-section">
  <h2>Agent Messages</h2>
  ${chapterGroups.join('\n')}
</section>`
  }

  // Token Statistics
  let tokenStatsHtml = ''
  if (tokenStats.length > 0) {
    const total = tokenStats.reduce((acc, s) => acc + s.prompt + s.completion, 0)
    const rows = tokenStats.map(s => `
      <tr>
        <td>${escapeHtml(s.time)}</td>
        <td>${escapeHtml(s.role)}</td>
        <td>${s.chapterId != null ? 'Ch.' + (chapterMap.get(s.chapterId)?.number ?? s.chapterId) : '—'}</td>
        <td class="num">${s.prompt.toLocaleString()}</td>
        <td class="num">${s.completion.toLocaleString()}</td>
        <td class="num">${(s.prompt + s.completion).toLocaleString()}</td>
      </tr>`).join('\n')

    tokenStatsHtml = `
<section id="token-stats" class="md-section">
  <h2>Token Statistics</h2>
  <table class="stats-table">
    <thead>
      <tr><th>Time</th><th>Agent</th><th>Chapter</th><th>Prompt</th><th>Completion</th><th>Total</th></tr>
    </thead>
    <tbody>${rows}</tbody>
  </table>
  <p class="stats-total">Grand total: <strong>${total.toLocaleString()}</strong> tokens across ${tokenStats.length} call${tokenStats.length === 1 ? '' : 's'}</p>
</section>`
  }

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
  <title>${escapeHtml(book.title)} — Metadata</title>
  <style>
    :root { --bg: #ffffff; --fg: #1a1a1a; --fs: 18px; --lh: 1.75; --accent: #6366f1; --muted: #64748b; --border: rgba(128,128,128,0.15); }
    *, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }
    body { background: var(--bg); color: var(--fg); font-family: system-ui, -apple-system, sans-serif; font-size: var(--fs); line-height: var(--lh); transition: background 0.25s, color 0.25s; }
    #toolbar { position: sticky; top: 0; z-index: 100; display: flex; align-items: center; gap: 8px; flex-wrap: wrap; padding: 8px 20px; background: var(--bg); border-bottom: 1px solid var(--border); transition: background 0.25s; }
    .toolbar-label { font-size: 0.72em; opacity: 0.55; letter-spacing: 0.04em; text-transform: uppercase; margin-right: 2px; }
    .preset-btn { padding: 3px 10px; border-radius: 4px; cursor: pointer; font-size: 0.78em; font-weight: 600; white-space: nowrap; transition: opacity 0.15s; }
    .preset-btn:hover { opacity: 0.8; }
    .toolbar-sep { width: 1px; height: 22px; background: rgba(128,128,128,0.3); margin: 0 4px; }
    .size-btn { padding: 3px 9px; border-radius: 4px; cursor: pointer; font-weight: bold; background: transparent; color: var(--fg); border: 1px solid rgba(128,128,128,0.4); font-size: 0.85em; transition: opacity 0.15s; }
    .size-btn:hover { opacity: 0.65; }
    #font-size-label { font-size: 0.75em; min-width: 2.2em; text-align: center; opacity: 0.6; }
    main { max-width: 860px; margin: 0 auto; padding: 48px 24px 96px; }
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
    .md-section h3 { font-size: 1.05em; margin: 20px 0 8px; color: var(--fg); }
    .prop-list { display: grid; grid-template-columns: max-content 1fr; gap: 6px 16px; margin-bottom: 16px; font-size: 0.9em; }
    .prop-list dt { font-weight: 600; opacity: 0.6; white-space: nowrap; }
    .prop-list dd { word-break: break-word; }
    .prop-block { margin-top: 14px; }
    .prop-block strong { display: block; font-size: 0.82em; text-transform: uppercase; letter-spacing: 0.04em; opacity: 0.55; margin-bottom: 6px; }
    .prop-block p { font-size: 0.95em; line-height: 1.7; }
    .pre-block { background: rgba(128,128,128,0.07); border: 1px solid var(--border); border-radius: 6px; padding: 10px 14px; font-family: 'Courier New', monospace; font-size: 0.82em; white-space: pre-wrap; word-break: break-word; }
    .empty-note { opacity: 0.5; font-style: italic; }
    /* Chapter outlines */
    .outline-card { background: rgba(128,128,128,0.04); border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 14px; }
    .outline-meta { display: flex; gap: 10px; align-items: center; margin-bottom: 10px; flex-wrap: wrap; }
    .outline-text { font-size: 0.9em; line-height: 1.65; margin-bottom: 8px; }
    .foretell { font-size: 0.85em; opacity: 0.75; margin-top: 6px; }
    .meta-item { font-size: 0.82em; }
    /* Badges */
    .badge-status { font-size: 0.72em; padding: 2px 8px; border-radius: 999px; background: rgba(99,102,241,0.15); color: var(--accent); border: 1px solid rgba(99,102,241,0.3); font-weight: 600; }
    .badge-role { font-size: 0.72em; padding: 2px 8px; border-radius: 999px; font-weight: 600; }
    .role-protagonist { background: rgba(34,197,94,0.15); color: #16a34a; border: 1px solid rgba(34,197,94,0.3); }
    .role-antagonist { background: rgba(239,68,68,0.15); color: #b91c1c; border: 1px solid rgba(239,68,68,0.3); }
    .role-supporting { background: rgba(99,102,241,0.15); color: #4338ca; border: 1px solid rgba(99,102,241,0.3); }
    .role-minor { background: rgba(100,116,139,0.12); color: #475569; border: 1px solid rgba(100,116,139,0.3); }
    .badge-type { font-size: 0.72em; padding: 2px 8px; border-radius: 4px; background: rgba(99,102,241,0.12); color: var(--accent); font-weight: 600; }
    .badge-thread-status { font-size: 0.72em; padding: 2px 8px; border-radius: 999px; font-weight: 600; }
    .badge-thread-status.status-active { background: rgba(34,197,94,0.15); color: #16a34a; border: 1px solid rgba(34,197,94,0.3); }
    .badge-thread-status.status-resolved { background: rgba(100,116,139,0.12); color: #475569; border: 1px solid rgba(100,116,139,0.3); }
    .badge-thread-status.status-dormant { background: rgba(245,158,11,0.15); color: #b45309; border: 1px solid rgba(245,158,11,0.3); }
    /* Characters */
    .char-block { border: 1px solid var(--border); border-radius: 8px; padding: 16px; margin-bottom: 14px; }
    .char-header { display: flex; align-items: center; gap: 10px; margin-bottom: 12px; }
    .char-header strong { font-size: 1.05em; }
    /* Plot threads */
    .thread-block { border: 1px solid var(--border); border-radius: 8px; padding: 14px; margin-bottom: 12px; }
    .thread-header { display: flex; align-items: center; gap: 8px; margin-bottom: 8px; flex-wrap: wrap; }
    .thread-header strong { font-size: 1em; }
    .thread-desc { font-size: 0.9em; opacity: 0.8; margin-bottom: 8px; line-height: 1.6; }
    .thread-meta { display: flex; gap: 12px; font-size: 0.8em; opacity: 0.6; }
    /* Messages */
    .msg-group { margin-bottom: 24px; }
    .msg-item { border-left: 3px solid var(--muted); padding: 8px 12px; margin-bottom: 8px; background: rgba(128,128,128,0.04); border-radius: 0 6px 6px 0; }
    .msg-meta { display: flex; align-items: center; gap: 8px; margin-bottom: 4px; flex-wrap: wrap; }
    .msg-meta strong { font-size: 0.85em; }
    .msg-type-label { font-size: 0.75em; opacity: 0.6; }
    .msg-time { font-size: 0.72em; opacity: 0.5; margin-left: auto; }
    .msg-body { font-size: 0.88em; white-space: pre-wrap; word-break: break-word; opacity: 0.85; }
    /* Token stats */
    .stats-table { width: 100%; border-collapse: collapse; font-size: 0.85em; }
    .stats-table th { text-align: left; padding: 6px 10px; border-bottom: 2px solid var(--border); font-size: 0.78em; text-transform: uppercase; letter-spacing: 0.04em; opacity: 0.6; }
    .stats-table td { padding: 5px 10px; border-bottom: 1px solid var(--border); }
    .stats-table tr:nth-child(even) td { background: rgba(128,128,128,0.04); }
    .stats-table .num { text-align: right; font-variant-numeric: tabular-nums; }
    .stats-total { margin-top: 12px; font-size: 0.9em; text-align: right; opacity: 0.7; }
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
    <header class="doc-header">
      <h1>${escapeHtml(book.title)}</h1>
      <p class="subtitle">Metadata &amp; Planning Document &middot; ${escapeHtml(book.genre)}${book.language ? ' &middot; ' + escapeHtml(book.language) : ''}</p>
    </header>
    <nav id="toc">
      <h2>Contents</h2>
      <ol>
${tocItems}
      </ol>
    </nav>
${bookInfoHtml}
${storyBibleHtml}
${chapterOutlinesHtml}
${charactersHtml}
${plotThreadsHtml}
${messagesHtml}
${tokenStatsHtml}
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
      document.getElementById('font-larger').addEventListener('click', function() { applyFontSize(fontSize + 2); });
      try {
        var sp = localStorage.getItem('abook-meta-preset'); if (sp !== null) applyPreset(Number(sp));
        var sf = localStorage.getItem('abook-meta-fontsize'); if (sf !== null) applyFontSize(Number(sf));
      } catch(e) {}
    })();
  </script>
</body>
</html>`
}

export function downloadBookMetadataAsHtml(
  book: Book,
  storyBible: StoryBible | null,
  characters: CharacterCard[],
  plotThreads: PlotThread[],
  messages: AgentMessage[],
  tokenStats: TokenStatEntry[]
): void {
  const html = generateBookMetadataHtml(book, storyBible, characters, plotThreads, messages, tokenStats)
  const blob = new Blob([html], { type: 'text/html;charset=utf-8' })
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  const safeName = (book.title
    .replace(/[^a-z0-9\s-]/gi, '')
    .trim()
    .replace(/\s+/g, '-')
    .toLowerCase() || 'book') + '-metadata'
  a.download = `${safeName}.html`
  document.body.appendChild(a)
  a.click()
  document.body.removeChild(a)
  URL.revokeObjectURL(url)
}
