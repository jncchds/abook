import { useState, useEffect } from 'react'
import { useParams } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../../contexts/BookContext'
import { updateChapter, clearChapterContent, getStreamBuffer } from '../../api'

export default function ChapterView() {
  const { chapterId } = useParams<{ chapterId: string }>()
  const { book, setBook, streamBuffer, streamingChapterId, isRunning, setStreamBuffer, setStreamingChapterId } = useBookContext()

  const [editingChapter, setEditingChapter] = useState(false)
  const [chapterEditTitle, setChapterEditTitle] = useState('')
  const [chapterEditOutline, setChapterEditOutline] = useState('')

  if (!book) return null

  const chapter = book.chapters?.find(c => c.id === Number(chapterId))
  if (!chapter) return <p className="empty">Chapter not found.</p>

  const bookId = book.id

  // Restore in-progress stream on hard-refresh
  // eslint-disable-next-line react-hooks/rules-of-hooks
  useEffect(() => {
    if (!isRunning || streamBuffer) return
    getStreamBuffer(bookId, undefined, chapter.id).then(r => {
      if (r.data.content) { setStreamBuffer(r.data.content); setStreamingChapterId(chapter.id) }
    }).catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [chapter.id])

  const statusColor = (s: string) => ({
    Outlined: '#94a3b8', Writing: '#f59e0b', Review: '#3b82f6',
    Editing: '#a855f7', Done: '#22c55e'
  })[s] ?? '#94a3b8'

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
