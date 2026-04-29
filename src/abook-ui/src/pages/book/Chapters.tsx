import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../../contexts/BookContext'
import { createChapter } from '../../api'

const STATUS_COLOR: Record<string, string> = {
  Outlined: '#94a3b8',
  Writing:  '#f59e0b',
  Review:   '#3b82f6',
  Editing:  '#a855f7',
  Done:     '#22c55e',
}

export default function Chapters() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const navigate = useNavigate()

  const {
    book, setBook,
    isRunning, isPhaseComplete,
    handleCompletePhase, handleReopenPhase, handleClearPhase,
    streamBuffer, streamingChapterId,
  } = useBookContext()

  const [addingChapter, setAddingChapter] = useState(false)
  const [newTitle, setNewTitle] = useState('')
  const [newOutline, setNewOutline] = useState('')

  if (!book) return null

  const chapters = book.chapters ?? []

  const handleAddChapter = async () => {
    if (!newTitle.trim()) return
    const nextNumber = chapters.length + 1
    const r = await createChapter(id, { number: nextNumber, title: newTitle.trim(), outline: newOutline.trim() })
    setBook(prev => prev ? { ...prev, chapters: [...(prev.chapters ?? []), r.data] } : prev)
    setNewTitle('')
    setNewOutline('')
    setAddingChapter(false)
    navigate(`/books/${id}/chapters/${r.data.id}`)
  }

  return (
    <div className="view-content">
      <div className="view-header">
        <h2>📚 Chapters ({chapters.length})</h2>
      </div>

      {/* Phase action bar */}
      <div className="phase-actions" style={{ marginBottom: '1rem' }}>
        {isPhaseComplete('chapters') ? (
          <>
            <span className="phase-status-badge phase-complete">✅ Complete</span>
            <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('chapters')}>↺ Reopen</button>
          </>
        ) : (
          <>
            <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
            <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('chapters')}>✓ Complete</button>
          </>
        )}
        <button
          className="btn-sm btn-danger phase-action-btn"
          onClick={() => handleClearPhase('chapters', () =>
            setBook(prev => prev ? { ...prev, chapters: [] } : prev)
          )}
        >🗑 Clear All</button>
      </div>

      {/* Chapter cards */}
      {chapters.length === 0 ? (
        <p className="empty">No chapters yet. Use <strong>Plan Book</strong> to generate outlines, or add one manually below.</p>
      ) : (
        <div className="book-list">
          {chapters.map(c => (
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
                    style={{ background: STATUS_COLOR[c.status] ?? STATUS_COLOR['Outlined'], color: '#fff', borderRadius: 4, padding: '1px 7px', fontSize: '0.75rem' }}
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
                {streamBuffer && streamingChapterId === c.id && (
                  <div className="chapter-content" style={{ marginTop: '0.75rem' }}>
                    <ReactMarkdown>{streamBuffer}</ReactMarkdown>
                  </div>
                )}
              </div>
              <div className="book-list-card-right">
                <button onClick={() => navigate(`/books/${id}/chapters/${c.id}`)}>Open →</button>
              </div>
            </div>
          ))}
        </div>
      )}

      {/* Add chapter */}
      {!isRunning && (
        <div style={{ marginTop: '1.5rem' }}>
          {addingChapter ? (
            <div className="card" style={{ maxWidth: 560 }}>
              <h3>Add Chapter</h3>
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
                <button onClick={handleAddChapter} disabled={!newTitle.trim()}>Add</button>
                <button className="btn-ghost" onClick={() => { setAddingChapter(false); setNewTitle(''); setNewOutline('') }}>Cancel</button>
              </div>
            </div>
          ) : (
            <button className="btn-sm" onClick={() => setAddingChapter(true)}>+ Add Chapter</button>
          )}
        </div>
      )}
    </div>
  )
}
