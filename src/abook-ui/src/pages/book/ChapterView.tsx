import { useState } from 'react'
import { useParams } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../../contexts/BookContext'
import { updateChapter, clearChapterContent, getChapterVersions, getChapterVersion, activateChapterVersion } from '../../api'
import type { ChapterVersionMeta, ChapterVersionFull } from '../../api'
import { chapterStatusColor } from '../../utils/chapterStatus'
import { useRestoreStream } from '../../hooks/useRestoreStream'

export default function ChapterView() {
  const { chapterId } = useParams<{ chapterId: string }>()
  const { book, setBook, streamBuffer, streamingChapterId, isRunning, setStreamBuffer, setStreamingChapterId } = useBookContext()

  const [editingChapter, setEditingChapter] = useState(false)
  const [chapterEditTitle, setChapterEditTitle] = useState('')
  const [chapterEditOutline, setChapterEditOutline] = useState('')
  const [showHistory, setShowHistory] = useState(false)
  const [versions, setVersions] = useState<ChapterVersionMeta[]>([])
  const [previewVersion, setPreviewVersion] = useState<ChapterVersionFull | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [activating, setActivating] = useState(false)

  const chapter = book?.chapters?.find(c => c.id === Number(chapterId))

  // Restore in-progress stream on hard-refresh (must be before early returns)
  useRestoreStream(book?.id, isRunning, streamBuffer, undefined, chapter?.id, (content) => {
    setStreamBuffer(content)
    if (chapter) setStreamingChapterId(chapter.id)
  })

  if (!book) return null
  if (!chapter) return <p className="empty">Chapter not found.</p>

  const bookId = book.id
  const statusColor = chapterStatusColor

  const handleSaveChapterEdit = async () => {
    const r = await updateChapter(bookId, chapter.id, {
      title: chapterEditTitle,
      outline: chapterEditOutline,
      content: chapter.content,
      status: chapter.status as never,
    })
    setBook(prev => prev ? { ...prev, chapters: (prev.chapters ?? []).map(c => c.id === r.data.id ? r.data : c) } : prev)
    setEditingChapter(false)
  }

  const handleClearChapter = async () => {
    if (!confirm(`Clear all content for Chapter ${chapter.number}: "${chapter.title}"? This cannot be undone.`)) return
    const r = await clearChapterContent(bookId, chapter)
    setBook(prev => prev ? {
      ...prev,
      chapters: (prev.chapters ?? []).map(c => c.id === r.data.id ? r.data : c)
    } : prev)
  }

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try {
      const r = await getChapterVersions(bookId, chapter.id)
      setVersions(r.data)
    } finally {
      setLoadingHistory(false)
    }
    setShowHistory(true)
    setPreviewVersion(null)
  }

  const handlePreview = async (versionId: number) => {
    if (previewVersion?.id === versionId) { setPreviewVersion(null); return }
    const r = await getChapterVersion(bookId, chapter.id, versionId)
    setPreviewVersion(r.data)
  }

  const handleActivate = async (versionId: number) => {
    if (!confirm('Activate this version? It will replace the current chapter content.')) return
    setActivating(true)
    try {
      const r = await activateChapterVersion(bookId, chapter.id, versionId)
      setBook(prev => prev ? {
        ...prev,
        chapters: (prev.chapters ?? []).map(c => c.id === chapter.id ? r.data.chapter : c)
      } : prev)
      setVersions(prev => prev.map(v => ({ ...v, isActive: v.id === versionId })))
      setShowHistory(false)
    } finally {
      setActivating(false)
    }
  }

  if (showHistory) {
    return (
      <div className="chapter-view">
        <div className="history-panel-header">
          <h3>📜 Chapter {chapter.number} History</h3>
          <button className="btn-sm btn-ghost" onClick={() => setShowHistory(false)}>✕ Close</button>
        </div>
        {loadingHistory && <p className="empty">Loading…</p>}
        {!loadingHistory && versions.length === 0 && <p className="empty">No versions yet.</p>}
        <div className="history-list">
          {versions.map(v => (
            <div key={v.id} className={`history-item ${previewVersion?.id === v.id ? 'history-item-active' : ''}`}>
              <div className="history-item-meta">
                {v.isActive && <span className="history-source-badge">active</span>}
                <span className="history-reason">v{v.versionNumber} — {v.createdBy}</span>
                <span className="history-date">{new Date(v.createdAt).toLocaleString()}</span>
                <span className="history-date">{v.wordCount.toLocaleString()} words</span>
              </div>
              <div className="history-item-actions">
                <button className="btn-sm btn-ghost" onClick={() => handlePreview(v.id)}>
                  {previewVersion?.id === v.id ? 'Hide' : 'Preview'}
                </button>
                {!v.isActive && (
                  <button className="btn-sm" disabled={activating} onClick={() => handleActivate(v.id)}>Activate</button>
                )}
              </div>
              {previewVersion?.id === v.id && (
                <div className="history-preview history-preview-content">
                  <p><strong>{previewVersion.title}</strong></p>
                  {previewVersion.outline && <p><em>{previewVersion.outline}</em></p>}
                  <ReactMarkdown>{previewVersion.content?.slice(0, 800) + (previewVersion.content?.length > 800 ? '…' : '')}</ReactMarkdown>
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="chapter-view">
      {editingChapter ? (
        <div className="chapter-edit-form">
          <label>Title<input value={chapterEditTitle} onChange={e => setChapterEditTitle(e.target.value)} /></label>
          <label>Outline<textarea rows={4} value={chapterEditOutline} onChange={e => setChapterEditOutline(e.target.value)} /></label>
          <div className="chapter-edit-actions">
            <button onClick={handleSaveChapterEdit}>Save</button>
            <button className="btn-ghost" onClick={() => setEditingChapter(false)}>Cancel</button>
          </div>
        </div>
      ) : (
        <>
          <div className="chapter-header">
            <h2>Chapter {chapter.number}: {chapter.title}</h2>
            <span className="ch-status-badge" style={{ background: statusColor(chapter.status) }}>{chapter.status}</span>
            {!isRunning && (
              <button className="btn-edit-chapter" onClick={() => { setChapterEditTitle(chapter.title); setChapterEditOutline(chapter.outline ?? ''); setEditingChapter(true) }} title="Edit chapter title and outline">✎ Edit</button>
            )}
            <button className="btn-sm btn-ghost" onClick={handleOpenHistory} title="View version history">📜 History</button>
            {chapter.content?.trim() && (
              <button className="btn-clear-chapter" disabled={isRunning} onClick={handleClearChapter} title="Clear chapter content and reset status to Outlined">↺ Clear</button>
            )}
          </div>
          {chapter.outline && (
            <div className="outline"><strong>Outline:</strong> {chapter.outline}</div>
          )}
          {(chapter.povCharacter || chapter.foreshadowingNotes || chapter.payoffNotes) && (
            <div className="chapter-meta-fields">
              {chapter.povCharacter && <span className="ch-meta-item"><strong>POV:</strong> {chapter.povCharacter}</span>}
              {chapter.foreshadowingNotes && <span className="ch-meta-item"><strong>Foreshadowing:</strong> {chapter.foreshadowingNotes}</span>}
              {chapter.payoffNotes && <span className="ch-meta-item"><strong>Payoff:</strong> {chapter.payoffNotes}</span>}
            </div>
          )}
          <div className="chapter-content">
            {streamBuffer && streamingChapterId === chapter.id ? (
              <ReactMarkdown>{streamBuffer}</ReactMarkdown>
            ) : chapter.content ? (
              <ReactMarkdown>{chapter.content}</ReactMarkdown>
            ) : (
              <p className="empty">Waiting to be written by the agents…</p>
            )}
          </div>
        </>
      )}
    </div>
  )
}
