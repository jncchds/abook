import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import axios from 'axios'
import { useBookContext } from '../../contexts/BookContext'
import { createChapter, archiveChapter, restoreChapter, clearChapterContent, getChapterVersions, getChapterVersion, getChapters, moveChapter, inBookOrder } from '../../api'
import type { Chapter, ChapterVersionMeta, ChapterVersionFull } from '../../api'
import { parsePlanningStream } from '../../utils/streamParsers'
import { chapterStatusColor } from '../../utils/chapterStatus'
import PhaseActionBar from '../../components/PhaseActionBar'

/** Where the add form inserts: at the end, at the start, or right after a chapter (its id). */
type AddPlace = 'end' | 'start' | number

const errorText = (err: unknown) =>
  axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err)

export default function Chapters() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const navigate = useNavigate()

  const {
    book, setBook,
    canEdit,
    plannerBuffer, runStatus,
  } = useBookContext()

  const [addingAt, setAddingAt] = useState<AddPlace | null>(null)
  const [newTitle, setNewTitle] = useState('')
  const [newOutline, setNewOutline] = useState('')
  const [showArchived, setShowArchived] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  // Per-chapter version history state (archived chapters)
  const [itemHistoryChapterId, setItemHistoryChapterId] = useState<number | null>(null)
  const [itemVersions, setItemVersions] = useState<ChapterVersionMeta[]>([])
  const [loadingItemHistory, setLoadingItemHistory] = useState(false)
  const [itemPreviewVersion, setItemPreviewVersion] = useState<ChapterVersionFull | null>(null)

  if (!book) return null

  const chapters = inBookOrder(book.chapters ?? [])
  const activeChapters = chapters.filter(c => !c.isArchived)
  const archivedCount = chapters.length - activeChapters.length
  // Archived chapters are shown in their place, between the chapters around them.
  const visible = showArchived ? chapters : activeChapters
  const planningChapters = parsePlanningStream(plannerBuffer)

  const setChapters = (list: Chapter[]) => setBook(prev => prev ? { ...prev, chapters: list } : prev)
  const reloadChapters = async () => setChapters((await getChapters(id)).data)

  /** Runs a structure change; numbers of other chapters change too, so the list is reloaded. */
  const change = async (action: () => Promise<unknown>) => {
    setBusy(true)
    setError(null)
    try {
      await action()
      await reloadChapters()
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
        afterChapterId: typeof place === 'number' ? place : null,
        atStart: place === 'start',
      })
      createdId = r.data.id
    })
    closeAdd()
    if (createdId !== null && place === 'end') navigate(`/books/${id}/chapters/${createdId}`)
  }

  /** Moves a chapter one step up or down among the chapters shown. */
  const handleMove = (chapter: Chapter, direction: -1 | 1) => {
    const i = visible.findIndex(c => c.id === chapter.id)
    const after = direction < 0
      ? (i >= 2 ? visible[i - 2].id : null)
      : visible[i + 1]?.id
    if (direction > 0 && after === undefined) return
    return change(() => moveChapter(id, chapter.id, after ?? null))
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

  const addForm = (place: AddPlace) => addingAt === place && (
    <div className="card" style={{ maxWidth: 560, margin: '0.5rem 0 1rem' }}>
      <h3>{place === 'end' ? 'Add chapter at the end'
        : place === 'start' ? 'Add chapter at the start'
        : `Insert chapter after Chapter ${chapters.find(c => c.id === place)?.number ?? ''}`}</h3>
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

  const moveButtons = (c: Chapter, i: number) => canEdit && (
    <>
      <button className="btn-sm btn-ghost" title="Move up" disabled={busy || i === 0} onClick={() => handleMove(c, -1)}>↑</button>
      <button className="btn-sm btn-ghost" title="Move down" disabled={busy || i === visible.length - 1} onClick={() => handleMove(c, 1)}>↓</button>
    </>
  )

  return (
    <div>
      <div className="page-header">
        <h2>Chapters ({activeChapters.length}{archivedCount > 0 ? ' + ' + archivedCount + ' archived' : ''})</h2>
        {canEdit && <button className="btn-sm" onClick={() => setAddingAt('end')}>+ Add</button>}
      </div>

      {/* Phase action bar */}
      <PhaseActionBar
        phase="chapters"
        onClear={() => setBook(prev => prev ? { ...prev, chapters: (prev.chapters ?? []).map(c => ({ ...c, isArchived: true })) } : prev)}
        clearLabel="🗑 Clear All"
        style={{ marginBottom: '1rem' }}
      />

      {error && <p className="error-msg" role="alert">{error}</p>}

      {archivedCount > 0 && (
        <button className="btn-sm btn-ghost" style={{ marginBottom: '0.5rem' }} onClick={() => setShowArchived(v => !v)}>
          {showArchived ? '▲ Hide archived' : '▼ Show archived in place (' + archivedCount + ')'}
        </button>
      )}

      {addForm('end')}

      {/* Live planning stream preview */}
      {runStatus?.role === 'ChaptersAgent' && plannerBuffer && planningChapters.length > 0 && (
        <div className="planning-preview">
          <div className="book-list">
            {planningChapters.map(c => (
              <div key={c.number} className="book-list-card">
                <div className="book-list-card-left">
                  <h3>{c.number}. {c.title}</h3>
                  <div className="blc-meta">
                    <span className="status" style={{ background: chapterStatusColor('Outlined'), color: '#fff', borderRadius: 4, padding: '1px 7px', fontSize: '0.75rem' }}>Outlined</span>
                  </div>
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
          {canEdit && visible.length > 0 && (
            <div>
              <button className="btn-sm btn-ghost" title="Insert a chapter before the first one" onClick={() => setAddingAt('start')}>＋ Insert at the start</button>
              {addForm('start')}
            </div>
          )}
          {visible.map((c, i) => c.isArchived ? (
            <div key={c.id} className="book-list-card" style={{ opacity: 0.6 }}>
              <div className="book-list-card-left">
                <h3>{c.title || 'Untitled'} <span className="history-source-badge">archived · was Ch. {c.number}</span></h3>
                <div className="blc-meta">
                  <span className="status" style={{ background: chapterStatusColor(c.status), color: '#fff', borderRadius: 4, padding: '1px 7px', fontSize: '0.75rem' }}>{c.status}</span>
                </div>
                {c.outline && <p className="blc-premise">{c.outline}</p>}
                {historyPanel(c)}
              </div>
              <div className="book-list-card-right">
                {moveButtons(c, i)}
                <button className="btn-sm btn-ghost" title="Version History" onClick={() => handleOpenItemHistory(c.id)}>📜</button>
                <button className="btn-sm" title="Bring the chapter back here" disabled={busy} onClick={() => handleRestore(c.id)}>♻ Restore</button>
              </div>
            </div>
          ) : (
            <div key={c.id}>
              <div className="book-list-card">
                <div className="book-list-card-left">
                  <h3
                    style={{ cursor: 'pointer' }}
                    onClick={() => navigate(`/books/${id}/chapters/${c.id}`)}
                  >
                    {c.number}. {c.title || 'Untitled'}
                  </h3>
                  <div className="blc-meta">
                    <span
                      className="status"
                      style={{ background: chapterStatusColor(c.status), color: '#fff', borderRadius: 4, padding: '1px 7px', fontSize: '0.75rem' }}
                    >
                      {c.status}
                    </span>
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
                  {canEdit && (
                    <>
                      {moveButtons(c, i)}
                      <button className="btn-sm btn-ghost" title="Insert a new chapter after this one" onClick={() => setAddingAt(c.id)}>＋ After</button>
                      <button className="btn-sm btn-ghost" title="Split this chapter in two by its outline" onClick={() => navigate(`/books/${id}/chapters/${c.id}?split=1`)}>✂ Split</button>
                      <button className="btn-sm btn-ghost" title="Clear chapter content (keep outline)" onClick={() => handleClearChapter(c.id)}>🗑 Clear</button>
                      <button className="btn-archive" title="Archive chapter (it keeps its place)" disabled={busy} onClick={() => handleArchive(c.id)}>🗄</button>
                    </>
                  )}
                </div>
              </div>
              {addForm(c.id)}
            </div>
          ))}
        </div>
      )}
    </div>
  )
}
