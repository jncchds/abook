import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useBookContext } from '../../contexts/BookContext'
import { createChapter, archiveChapter, restoreChapter, getChapterVersions, getChapterVersion } from '../../api'
import type { ChapterVersionMeta, ChapterVersionFull } from '../../api'
import { parsePlanningStream } from '../../utils/streamParsers'
import { chapterStatusColor } from '../../utils/chapterStatus'
import PhaseActionBar from '../../components/PhaseActionBar'
import { useRestoreStream } from '../../hooks/useRestoreStream'

export default function Chapters() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const navigate = useNavigate()

  const {
    book, setBook,
    isRunning,
    plannerBuffer, setPlannerBuffer, runStatus,
  } = useBookContext()

  useRestoreStream(book?.id, isRunning, plannerBuffer, 'ChaptersAgent', undefined,
    (content) => setPlannerBuffer(prev => prev.length >= content.length ? prev : content))

  const [addingChapter, setAddingChapter] = useState(false)
  const [newTitle, setNewTitle] = useState('')
  const [newOutline, setNewOutline] = useState('')
  const [showArchived, setShowArchived] = useState(false)

  // Per-chapter version history state (for archived section)
  const [itemHistoryChapterId, setItemHistoryChapterId] = useState<number | null>(null)
  const [itemVersions, setItemVersions] = useState<ChapterVersionMeta[]>([])
  const [loadingItemHistory, setLoadingItemHistory] = useState(false)
  const [itemPreviewVersion, setItemPreviewVersion] = useState<ChapterVersionFull | null>(null)

  if (!book) return null

  const chapters = book.chapters ?? []
  const activeChapters = chapters.filter(c => !c.isArchived)
  const archivedChapters = chapters.filter(c => c.isArchived)
  const planningChapters = parsePlanningStream(plannerBuffer)

  const handleAddChapter = async () => {
    if (!newTitle.trim()) return
    const nextNumber = activeChapters.length + 1
    const r = await createChapter(id, { number: nextNumber, title: newTitle.trim(), outline: newOutline.trim() })
    setBook(prev => prev ? { ...prev, chapters: [...(prev.chapters ?? []), r.data] } : prev)
    setNewTitle('')
    setNewOutline('')
    setAddingChapter(false)
    navigate(`/books/${id}/chapters/${r.data.id}`)
  }

  const handleArchive = async (chapterId: number) => {
    await archiveChapter(id, chapterId)
    setBook(prev => prev ? {
      ...prev,
      chapters: (prev.chapters ?? []).map(c => c.id === chapterId ? { ...c, isArchived: true } : c)
    } : prev)
  }

  const handleRestore = async (chapterId: number) => {
    const r = await restoreChapter(id, chapterId)
    setBook(prev => prev ? {
      ...prev,
      chapters: (prev.chapters ?? []).map(c => c.id === chapterId ? r.data : c)
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

  return (
    <div>
      <div className="page-header">
        <h2>Chapters ({activeChapters.length}{archivedChapters.length > 0 ? ' + ' + archivedChapters.length + ' archived' : ''})</h2>
        <button className="btn-sm" onClick={() => setAddingChapter(true)}>+ Add</button>
      </div>

      {/* Phase action bar */}
      <PhaseActionBar
        phase="chapters"
        onClear={() => setBook(prev => prev ? { ...prev, chapters: [] } : prev)}
        clearLabel="🗑 Clear All"
        style={{ marginBottom: '1rem' }}
      />

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
      {activeChapters.length === 0 && runStatus?.role !== 'ChaptersAgent' ? (
        <p className="empty">No chapters yet. Use <strong>Plan Book</strong> to generate outlines, or add one manually below.</p>
      ) : !(plannerBuffer && runStatus?.role === 'ChaptersAgent') && (
        <div className="book-list">
          {activeChapters.map(c => (
            <div key={c.id} className="book-list-card">
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
                {!isRunning && (
                  <button className="btn-archive" title="Archive chapter" onClick={() => handleArchive(c.id)}>🗄</button>
                )}
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Archived chapters */}
      {archivedChapters.length > 0 && (
        <div style={{ marginTop: '1.5rem' }}>
          <button className="btn-sm btn-ghost" onClick={() => setShowArchived(v => !v)}>
            {showArchived ? '▲ Hide archived' : '▼ Show archived (' + archivedChapters.length + ')'}
          </button>
          {showArchived && (
            <div className="book-list" style={{ marginTop: '0.5rem', opacity: 0.75 }}>
              {archivedChapters.map(c => (
                <div key={c.id} className="book-list-card">
                  <div className="book-list-card-left">
                    <h3>{c.number}. {c.title || 'Untitled'} <span className="history-source-badge">archived</span></h3>
                    <div className="blc-meta">
                      <span className="status" style={{ background: chapterStatusColor(c.status), color: '#fff', borderRadius: 4, padding: '1px 7px', fontSize: '0.75rem' }}>{c.status}</span>
                    </div>
                    {c.outline && <p className="blc-premise">{c.outline}</p>}
                    {itemHistoryChapterId === c.id && (
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
                    )}
                  </div>
                  <div className="book-list-card-right">
                    <button className="btn-sm btn-ghost" title="Version History" onClick={() => handleOpenItemHistory(c.id)}>📜</button>
                    <button className="btn-sm" title="Restore chapter" onClick={() => handleRestore(c.id)}>♻ Restore</button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}


    </div>
  )
}
