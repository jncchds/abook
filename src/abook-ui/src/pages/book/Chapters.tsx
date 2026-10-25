import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import axios from 'axios'
import { useBookContext } from '../../contexts/BookContext'
import {
  createChapter, archiveChapter, restoreChapter, clearChapterContent, getChapterVersions, getChapterVersion, moveChapter,
  moveChapterToSection, moveSection, archiveSection, restoreSection, updateSection, inBookOrder, sectionsInOrder, nameList,
  breakDownSection,
} from '../../api'
import type { Chapter, Section, ChapterVersionMeta, ChapterVersionFull } from '../../api'
import { parsePlanningStream } from '../../utils/streamParsers'
import { chapterStatusColor } from '../../utils/chapterStatus'
import PhaseActionBar from '../../components/PhaseActionBar'

/** Where the add form inserts. */
type AddPlace =
  | { kind: 'end' }
  | { kind: 'start' }
  /** A new section of its own after the chapter's section. */
  | { kind: 'after'; chapterId: number }
  /** In the chapter's section, right after it. */
  | { kind: 'inSection'; chapterId: number }

const placeKey = (p: AddPlace) => p.kind === 'end' || p.kind === 'start' ? p.kind : `${p.kind}:${p.chapterId}`

const errorText = (err: unknown) =>
  axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err)

interface SectionForm {
  title: string; outline: string; povCharacter: string; charactersInvolved: string; plotThreads: string
  foreshadowingNotes: string; payoffNotes: string
}

/** One section with the chapters shown in it. */
interface Group { section: Section | null; chapters: Chapter[] }

export default function Chapters() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const navigate = useNavigate()

  const {
    book, setBook, reloadStructure,
    canEdit, isRunning,
    plannerBuffer, runStatus, handleSectionRun,
  } = useBookContext()

  const [addingAt, setAddingAt] = useState<AddPlace | null>(null)
  const [newTitle, setNewTitle] = useState('')
  const [newOutline, setNewOutline] = useState('')
  const [showArchived, setShowArchived] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [editingSectionId, setEditingSectionId] = useState<number | null>(null)
  const [sectionForm, setSectionForm] = useState<SectionForm | null>(null)

  // Per-chapter version history state (archived chapters)
  const [itemHistoryChapterId, setItemHistoryChapterId] = useState<number | null>(null)
  const [itemVersions, setItemVersions] = useState<ChapterVersionMeta[]>([])
  const [loadingItemHistory, setLoadingItemHistory] = useState(false)
  const [itemPreviewVersion, setItemPreviewVersion] = useState<ChapterVersionFull | null>(null)

  if (!book) return null

  const chapters = inBookOrder(book.chapters ?? [])
  const sections = sectionsInOrder(book.sections ?? [])
  const activeChapters = chapters.filter(c => !c.isArchived)
  const archivedCount = chapters.length - activeChapters.length
  const visibleChapters = showArchived ? chapters : activeChapters

  // Sections in order, each with the chapters shown in it; chapters without a section (not reloaded yet) on their own.
  const groups: Group[] = []
  const sectionById = new Map(sections.map(s => [s.id, s]))
  for (const c of visibleChapters) {
    const s = c.sectionId != null ? sectionById.get(c.sectionId) ?? null : null
    const last = groups[groups.length - 1]
    if (last && s && last.section?.id === s.id) last.chapters.push(c)
    else groups.push({ section: s, chapters: [c] })
  }
  // A section counts as "several chapters" when it holds more than one chapter at all (archived included).
  const chapterCount = (s: Section | null) => s ? chapters.filter(c => c.sectionId === s.id).length : 1
  const anyMulti = sections.some(s => chapterCount(s) > 1)
  const activeSectionCount = sections.filter(s => !s.isArchived).length
  const planningChapters = parsePlanningStream(plannerBuffer)

  /** Runs a structure change; numbers of other chapters and sections change too, so everything is reloaded. */
  const change = async (action: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
      await reloadStructure()
    } catch (err) {
      setError(errorText(err))
    } finally {
      setBusy(false)
    }
  }

  const closeAdd = () => { setAddingAt(null); setNewTitle(''); setNewOutline('') }

  const handleAddChapter = async () => {
    if (!newTitle.trim() || addingAt === null) return
    const place = addingAt
    let createdId: number | null = null
    await change(async () => {
      const r = await createChapter(id, {
        title: newTitle.trim(),
        outline: newOutline.trim(),
        afterChapterId: place.kind === 'after' || place.kind === 'inSection' ? place.chapterId : null,
        atStart: place.kind === 'start',
        sameSection: place.kind === 'inSection',
      })
      createdId = r.data.id
    })
    closeAdd()
    if (createdId !== null && place.kind === 'end') navigate(`/books/${id}/chapters/${createdId}`)
  }

  /** Moves a whole section one step among the sections shown. */
  const handleMoveSection = (section: Section, direction: -1 | 1) => {
    const shown = groups.map(g => g.section).filter((s): s is Section => s !== null)
    const i = shown.findIndex(s => s.id === section.id)
    if (direction > 0 && i + 1 >= shown.length) return
    const after = direction < 0 ? (i >= 2 ? shown[i - 2].id : null) : shown[i + 1].id
    return change(() => moveSection(id, section.id, after))
  }

  /**
   * Moves a chapter one step. Alone in its section, it moves its section; inside a section of several chapters it
   * moves within it, and past its first or last chapter into the neighbouring section.
   */
  const handleMoveChapter = (group: Group, chapter: Chapter, direction: -1 | 1) => {
    if (!group.section) {
      // Not reloaded with its section yet: the server moves it (with its section) after the chapter two up / one down.
      const i = visibleChapters.findIndex(c => c.id === chapter.id)
      const after = direction < 0 ? (i >= 2 ? visibleChapters[i - 2].id : null) : visibleChapters[i + 1]?.id
      if (direction > 0 && after === undefined) return
      return change(() => moveChapter(id, chapter.id, after ?? null))
    }
    if (chapterCount(group.section) <= 1) return handleMoveSection(group.section, direction)
    const i = group.chapters.findIndex(c => c.id === chapter.id)
    const gi = groups.indexOf(group)
    if (direction < 0) {
      if (i > 0) return change(() => moveChapterToSection(id, chapter.id, group.section!.id, i >= 2 ? group.chapters[i - 2].id : null))
      const prev = groups[gi - 1]
      if (!prev?.section) return
      return change(() => moveChapterToSection(id, chapter.id, prev.section!.id, prev.chapters[prev.chapters.length - 1].id))
    }
    if (i < group.chapters.length - 1)
      return change(() => moveChapterToSection(id, chapter.id, group.section!.id, group.chapters[i + 1].id))
    const next = groups[gi + 1]
    if (!next?.section) return
    return change(() => moveChapterToSection(id, chapter.id, next.section!.id, null))
  }

  /** Starts a run that divides a section into chapters; the chapters arrive through the live updates. */
  const handleBreakDown = async (s: Section) => {
    const answer = prompt(`Break Section ${s.number} "${s.title}" into how many chapters?\nLeave empty to let the planner decide (2–4).`, '')
    if (answer === null) return
    const n = answer.trim() ? Number(answer) : undefined
    if (n !== undefined && (!Number.isInteger(n) || n < 1 || n > 10)) { setError('Enter a whole number from 1 to 10.'); return }
    setError(null)
    try { await breakDownSection(id, s.id, n) } catch (err) { setError(errorText(err)) }
  }
  /** A section can be broken down while none of its chapters is written. */
  const canBreakDown = (s: Section | null) => !!s && !s.isArchived && !isRunning
    && chapters.filter(c => c.sectionId === s.id && !c.isArchived).every(c => !c.content?.trim())

  /**
   * Autonomous books: write a section as Write Book does (pre-check, write, check, fixes — broken down first when
   * the planner expects several chapters), or check its written chapters. A one-chapter section shows them on its chapter.
   */
  const sectionRunButtons = (s: Section) => {
    if (book.mode === 'Interactive' || isRunning || s.isArchived) return null
    const own = chapters.filter(c => c.sectionId === s.id && !c.isArchived)
    const single = own.length === 1
    const toWrite = own.filter(c => c.status !== 'Done').length
    const written = own.filter(c => c.content?.trim() && (c.status === 'Done' || c.status === 'Review')).length
    return (
      <>
        {toWrite > 0 && (
          <button className="btn-sm" onClick={() => handleSectionRun('write', s.id)}
            title={single
              ? 'Pre-check, write, check and fix this chapter, as Write Book does'
              : `Pre-check, write, check and fix the section's ${toWrite} unfinished chapter${toWrite === 1 ? '' : 's'} in order, as Write Book does`}>
            ▶ {single ? 'Write' : 'Write section'}
          </button>
        )}
        {written > 0 && (
          <button className="btn-sm btn-ghost" onClick={() => handleSectionRun('check', s.id)}
            title={single
              ? 'Check this chapter against the Story Bible, characters and the chapters before it; findings arrive in Agent Messages'
              : `Check the section's ${written} written chapter${written === 1 ? '' : 's'}, one at a time; findings arrive in Agent Messages`}>
            🔍 Check
          </button>
        )}
      </>
    )
  }

  const handleArchive = (chapterId: number) => change(() => archiveChapter(id, chapterId))
  const handleRestore = (chapterId: number) => change(() => restoreChapter(id, chapterId))

  const handleClearChapter = async (chapterId: number) => {
    if (!confirm('Clear this chapter\'s content? The outline will be kept.')) return
    const ch = book.chapters?.find(c => c.id === chapterId)
    if (!ch) return
    await clearChapterContent(id, ch)
    setBook(prev => prev ? {
      ...prev,
      chapters: (prev.chapters ?? []).map(c =>
        c.id === chapterId ? { ...c, content: '', status: 'Outlined' } : c
      )
    } : prev)
  }

  const startEditSection = (s: Section) => {
    setEditingSectionId(s.id)
    setSectionForm({
      title: s.title, outline: s.outline, povCharacter: s.povCharacter,
      charactersInvolved: nameList(s.charactersInvolvedJson), plotThreads: nameList(s.plotThreadsJson),
      foreshadowingNotes: s.foreshadowingNotes, payoffNotes: s.payoffNotes,
    })
  }
  const saveSection = async () => {
    if (editingSectionId === null || !sectionForm) return
    const sid = editingSectionId
    await change(() => updateSection(id, sid, sectionForm))
    setEditingSectionId(null)
    setSectionForm(null)
  }

  const handleOpenItemHistory = async (chapterId: number) => {
    if (itemHistoryChapterId === chapterId) { setItemHistoryChapterId(null); return }
    setLoadingItemHistory(true)
    try { const r = await getChapterVersions(id, chapterId); setItemVersions(r.data) }
    finally { setLoadingItemHistory(false) }
    setItemHistoryChapterId(chapterId)
    setItemPreviewVersion(null)
  }

  const handleItemPreview = async (chapterId: number, versionId: number) => {
    if (itemPreviewVersion?.id === versionId) { setItemPreviewVersion(null); return }
    const r = await getChapterVersion(id, chapterId, versionId)
    setItemPreviewVersion(r.data)
  }

  const addForm = (place: AddPlace) => addingAt !== null && placeKey(addingAt) === placeKey(place) && (
    <div className="card" style={{ maxWidth: 560, margin: '0.5rem 0 1rem' }}>
      <h3>{place.kind === 'end' ? 'Add a section at the end'
        : place.kind === 'start' ? 'Add a section at the start'
        : place.kind === 'after' ? `New section after Chapter ${chapters.find(c => c.id === place.chapterId)?.number ?? ''}`
        : `New chapter after Chapter ${chapters.find(c => c.id === place.chapterId)?.number ?? ''}, in the same section`}</h3>
      {place.kind !== 'inSection' && <p className="hint">It gets one chapter with this title and outline.</p>}
      <label>
        Title
        <input
          autoFocus
          value={newTitle}
          onChange={e => setNewTitle(e.target.value)}
          onKeyDown={e => e.key === 'Enter' && handleAddChapter()}
        />
      </label>
      <label>
        Outline
        <textarea rows={3} value={newOutline} onChange={e => setNewOutline(e.target.value)} />
      </label>
      <div className="actions">
        <button onClick={handleAddChapter} disabled={!newTitle.trim() || busy}>Add</button>
        <button className="btn-ghost" onClick={closeAdd}>Cancel</button>
      </div>
    </div>
  )

  const historyPanel = (c: Chapter) => itemHistoryChapterId === c.id && (
    <div className="item-history-panel">
      <div className="history-panel-header" style={{ marginTop: '0.75rem' }}>
        <strong>📜 Version History</strong>
        <button className="btn-sm btn-ghost" onClick={() => { setItemHistoryChapterId(null); setItemPreviewVersion(null) }}>✕</button>
      </div>
      {loadingItemHistory && <p className="empty">Loading…</p>}
      {!loadingItemHistory && itemVersions.length === 0 && <p className="empty">No versions yet.</p>}
      <div className="history-list">
        {itemVersions.map(v => (
          <div key={v.id} className="history-item">
            <div className="history-item-meta">
              <span className="history-source-badge">{v.isActive ? 'active' : `v${v.versionNumber}`}</span>
              <span className="history-reason">{v.createdBy}</span>
              <span className="history-date">{new Date(v.createdAt).toLocaleString()}</span>
              <span className="history-date">{v.wordCount.toLocaleString()} words</span>
            </div>
            <div className="history-item-actions">
              <button className="btn-sm btn-ghost" onClick={() => handleItemPreview(c.id, v.id)}>
                {itemPreviewVersion?.id === v.id ? 'Hide' : 'Preview'}
              </button>
            </div>
            {itemPreviewVersion?.id === v.id && (
              <div className="history-preview" style={{ marginTop: '0.5rem' }}>
                <p><strong>{itemPreviewVersion.title}</strong></p>
                {itemPreviewVersion.outline && <p><em>{itemPreviewVersion.outline}</em></p>}
                <p style={{ whiteSpace: 'pre-wrap', fontSize: '0.85rem' }}>
                  {itemPreviewVersion.content?.slice(0, 500)}{(itemPreviewVersion.content?.length ?? 0) > 500 ? '…' : ''}
                </p>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )

  const statusBadge = (status: string) => (
    <span className="status" style={{ background: chapterStatusColor(status), color: '#fff', borderRadius: 4, padding: '1px 7px', fontSize: '0.75rem' }}>
      {status}
    </span>
  )

  /** A chapter card. `single`: the chapter is its section's only one (the card stands for the section). */
  const chapterCard = (group: Group, c: Chapter, single: boolean) => {
    const gi = groups.indexOf(group)
    const i = group.chapters.findIndex(x => x.id === c.id)
    const first = single ? gi === 0 : i === 0 && !groups.slice(0, gi).some(g => g.section)
    const last = single ? gi === groups.length - 1 : i === group.chapters.length - 1 && !groups.slice(gi + 1).some(g => g.section)
    const moveButtons = canEdit && (
      <>
        <button className="btn-sm btn-ghost" title={single ? 'Move up' : 'Move up (past the first chapter: into the section before)'}
          disabled={busy || first} onClick={() => handleMoveChapter(group, c, -1)}>↑</button>
        <button className="btn-sm btn-ghost" title={single ? 'Move down' : 'Move down (past the last chapter: into the section after)'}
          disabled={busy || last} onClick={() => handleMoveChapter(group, c, 1)}>↓</button>
      </>
    )
    const sectionBadge = single && anyMulti && group.section && !group.section.isArchived
      ? <span className="section-badge" title="Section">§{group.section.number}</span> : null

    if (c.isArchived) return (
      <div key={c.id} className="book-list-card" style={{ opacity: 0.6 }}>
        <div className="book-list-card-left">
          <h3>{c.title || 'Untitled'} <span className="history-source-badge">archived · was Ch. {c.number}</span></h3>
          <div className="blc-meta">{statusBadge(c.status)}</div>
          {c.outline && <p className="blc-premise">{c.outline}</p>}
          {historyPanel(c)}
        </div>
        <div className="book-list-card-right">
          {moveButtons}
          <button className="btn-sm btn-ghost" title="Version History" onClick={() => handleOpenItemHistory(c.id)}>📜</button>
          <button className="btn-sm" title="Bring the chapter back here" disabled={busy} onClick={() => handleRestore(c.id)}>♻ Restore</button>
        </div>
      </div>
    )

    return (
      <div key={c.id}>
        <div className="book-list-card">
          <div className="book-list-card-left">
            <h3 style={{ cursor: 'pointer' }} onClick={() => navigate(`/books/${id}/chapters/${c.id}`)}>
              {sectionBadge}{c.number}. {c.title || 'Untitled'}
            </h3>
            <div className="blc-meta">
              {statusBadge(c.status)}
              {c.povCharacter && <span className="blc-genre">POV: {c.povCharacter}</span>}
            </div>
            {c.outline && <p className="blc-premise">{c.outline}</p>}
            {c.foreshadowingNotes && (
              <p style={{ fontSize: '0.8rem', color: 'var(--text-muted)', marginTop: '0.25rem' }}>
                <em>Foreshadowing:</em> {c.foreshadowingNotes}
              </p>
            )}
            {c.payoffNotes && (
              <p style={{ fontSize: '0.8rem', color: 'var(--text-muted)' }}>
                <em>Payoff:</em> {c.payoffNotes}
              </p>
            )}
          </div>
          <div className="book-list-card-right">
            {single && group.section && sectionRunButtons(group.section)}
            {canEdit && (
              <>
                {moveButtons}
                <button className="btn-sm btn-ghost" title="Add a new section (with one chapter) after this one"
                  onClick={() => setAddingAt({ kind: 'after', chapterId: c.id })}>＋ After</button>
                <button className="btn-sm btn-ghost" title="Add another chapter to this section, right after this chapter"
                  onClick={() => setAddingAt({ kind: 'inSection', chapterId: c.id })}>＋ Chapter here</button>
                {single && canBreakDown(group.section) && (
                  <button className="btn-sm btn-ghost" title="Have the planner divide this section into several chapters"
                    onClick={() => handleBreakDown(group.section!)}>⋔ Break down</button>
                )}
                <button className="btn-sm btn-ghost" title="Split this chapter in two by its outline" onClick={() => navigate(`/books/${id}/chapters/${c.id}?split=1`)}>✂ Split</button>
                <button className="btn-sm btn-ghost" title="Clear chapter content (keep outline)" onClick={() => handleClearChapter(c.id)}>🗑 Clear</button>
                <button className="btn-archive" title={single ? 'Archive (with its section; it keeps its place)' : 'Archive chapter (it keeps its place)'}
                  disabled={busy} onClick={() => handleArchive(c.id)}>🗄</button>
              </>
            )}
          </div>
        </div>
        {addForm({ kind: 'after', chapterId: c.id })}
        {addForm({ kind: 'inSection', chapterId: c.id })}
      </div>
    )
  }

  const sectionGroup = (group: Group) => {
    const s = group.section!
    const shown = groups.map(g => g.section).filter((x): x is Section => x !== null)
    const si = shown.findIndex(x => x.id === s.id)
    const editing = editingSectionId === s.id && sectionForm
    const field = (key: keyof SectionForm, label: string, rows = 0) => (
      <label key={key}>
        {label}
        {rows > 0
          ? <textarea rows={rows} value={sectionForm![key]} onChange={e => setSectionForm(f => f ? { ...f, [key]: e.target.value } : f)} />
          : <input value={sectionForm![key]} onChange={e => setSectionForm(f => f ? { ...f, [key]: e.target.value } : f)} />}
      </label>
    )
    return (
      <div key={`s${s.id}`} className="section-group" style={s.isArchived ? { opacity: 0.6 } : undefined}>
        <div className="section-group-header">
          <div style={{ flex: 1, minWidth: 0 }}>
            <h3>
              <span className="section-badge">§{s.number}</span>{s.title || 'Untitled section'}
              {s.isArchived && <span className="history-source-badge" style={{ marginLeft: '0.4rem' }}>archived</span>}
            </h3>
            {!editing && s.outline && <p className="blc-premise" style={{ marginTop: '0.25rem' }}>{s.outline}</p>}
          </div>
          {!editing && <div>{sectionRunButtons(s)}</div>}
          {canEdit && !editing && (
            <div>
              <button className="btn-sm btn-ghost" title="Move the section up" disabled={busy || si <= 0} onClick={() => handleMoveSection(s, -1)}>↑</button>
              <button className="btn-sm btn-ghost" title="Move the section down" disabled={busy || si >= shown.length - 1} onClick={() => handleMoveSection(s, 1)}>↓</button>
              {!s.isArchived && <button className="btn-sm btn-ghost" title="Edit the section's overall plan" onClick={() => startEditSection(s)}>✎ Plan</button>}
              {canBreakDown(s) && (
                <button className="btn-sm btn-ghost" title="Have the planner divide the section into chapters again (none is written yet)"
                  onClick={() => handleBreakDown(s)}>⋔ Break down</button>
              )}
              {s.isArchived
                ? <button className="btn-sm" disabled={busy} onClick={() => change(() => restoreSection(id, s.id))}>♻ Restore section</button>
                : <button className="btn-archive" title="Archive the section with its chapters" disabled={busy}
                    onClick={() => confirm(`Archive section ${s.number} with its chapters? They keep their place and come back with it.`) && change(() => archiveSection(id, s.id))}>🗄</button>}
            </div>
          )}
        </div>
        {editing && (
          <div className="card" style={{ marginBottom: '0.75rem' }}>
            {field('title', 'Title')}
            {field('outline', 'Overall outline', 5)}
            {field('povCharacter', 'POV character')}
            {field('charactersInvolved', 'Characters (comma-separated)')}
            {field('plotThreads', 'Plot threads (comma-separated)')}
            {field('foreshadowingNotes', 'Foreshadowing', 2)}
            {field('payoffNotes', 'Payoff', 2)}
            <div className="actions">
              <button disabled={busy} onClick={saveSection}>Save</button>
              <button className="btn-ghost" onClick={() => { setEditingSectionId(null); setSectionForm(null) }}>Cancel</button>
            </div>
          </div>
        )}
        <div className="book-list">
          {group.chapters.map(c => chapterCard(group, c, false))}
        </div>
      </div>
    )
  }

  return (
    <div>
      <div className="page-header">
        <h2>
          Chapters ({activeChapters.length}{archivedCount > 0 ? ' + ' + archivedCount + ' archived' : ''})
          {anyMulti && <span className="hint" style={{ fontWeight: 'normal', marginLeft: '0.5rem' }}>in {activeSectionCount} sections</span>}
        </h2>
        {canEdit && <button className="btn-sm" onClick={() => setAddingAt({ kind: 'end' })}>+ Add</button>}
      </div>

      {/* Phase action bar */}
      <PhaseActionBar
        phase="chapters"
        onClear={() => { reloadStructure().catch(() => {}) }}
        clearLabel="🗑 Clear All"
        style={{ marginBottom: '1rem' }}
      />

      <p className="hint">
        The plan is made of <strong>sections</strong>; each starts with one chapter. A section can hold several chapters —
        add one with <strong>＋ Chapter here</strong> or <strong>✂ Split</strong> — and then has an overall plan of its own.
      </p>

      {error && <p className="error-msg" role="alert">{error}</p>}

      {archivedCount > 0 && (
        <button className="btn-sm btn-ghost" style={{ marginBottom: '0.5rem' }} onClick={() => setShowArchived(v => !v)}>
          {showArchived ? '▲ Hide archived' : '▼ Show archived in place (' + archivedCount + ')'}
        </button>
      )}

      {addForm({ kind: 'end' })}

      {/* Live planning stream preview */}
      {runStatus?.role === 'ChaptersAgent' && plannerBuffer && planningChapters.length > 0 && (
        <div className="planning-preview">
          <div className="book-list">
            {planningChapters.map(c => (
              <div key={c.number} className="book-list-card">
                <div className="book-list-card-left">
                  <h3>{c.number}. {c.title}</h3>
                  <div className="blc-meta">{statusBadge('Outlined')}</div>
                  {c.outline && <p className="blc-premise">{c.outline}</p>}
                </div>
              </div>
            ))}
          </div>
        </div>
      )}

      {/* Chapter cards — hidden while ChaptersAgent is actively streaming to avoid duplication */}
      {activeChapters.length === 0 && !showArchived && runStatus?.role !== 'ChaptersAgent' ? (
        <p className="empty">No chapters yet. Use <strong>Plan Book</strong> to generate outlines, or add one manually.</p>
      ) : !(plannerBuffer && runStatus?.role === 'ChaptersAgent') && (
        <div className="book-list">
          {canEdit && visibleChapters.length > 0 && (
            <div>
              <button className="btn-sm btn-ghost" title="Add a section before the first one" onClick={() => setAddingAt({ kind: 'start' })}>＋ Insert at the start</button>
              {addForm({ kind: 'start' })}
            </div>
          )}
          {groups.map(g => g.section && chapterCount(g.section) > 1
            ? sectionGroup(g)
            : g.chapters.map(c => chapterCard(g, c, true)))}
        </div>
      )}
    </div>
  )
}
