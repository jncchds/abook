import { useState } from 'react'
import { useNavigate, useParams } from 'react-router-dom'
import { useBookContext } from '../../contexts/BookContext'
import { createChapter } from '../../api'
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

  useRestoreStream(book?.id, isRunning, plannerBuffer, 'ChaptersAgent', undefined, setPlannerBuffer)

  const [addingChapter, setAddingChapter] = useState(false)
  const [newTitle, setNewTitle] = useState('')
  const [newOutline, setNewOutline] = useState('')

  if (!book) return null

  const chapters = book.chapters ?? []
  const planningChapters = parsePlanningStream(plannerBuffer)

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

      {/* Chapter cards */}
      {chapters.length === 0 && runStatus?.role !== 'ChaptersAgent' ? (
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
