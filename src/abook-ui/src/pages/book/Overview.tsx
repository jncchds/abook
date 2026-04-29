import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { updateBook } from '../../api'

export default function Overview() {
  const { book, setBook, isRunning, isPhaseComplete, handleCompletePhase, handleReopenPhase, handleClearPhase } = useBookContext()

  const [editingBook, setEditingBook] = useState(false)
  const [bookEditTitle, setBookEditTitle] = useState('')
  const [bookEditPremise, setBookEditPremise] = useState('')
  const [bookEditGenre, setBookEditGenre] = useState('')
  const [bookEditTargetChapters, setBookEditTargetChapters] = useState(0)
  const [bookEditHumanAssisted, setBookEditHumanAssisted] = useState(false)

  if (!book) return null

  const handleSaveBookEdit = async () => {
    const r = await updateBook(book.id, {
      title: bookEditTitle,
      premise: bookEditPremise,
      genre: bookEditGenre,
      targetChapterCount: bookEditTargetChapters,
      status: book.status as never,
      language: book.language,
      humanAssisted: bookEditHumanAssisted,
    })
    setBook(r.data)
    setEditingBook(false)
  }

  return (
    <div className="book-overview">
      {editingBook ? (
        <div className="book-edit-form">
          <label>Title<input value={bookEditTitle} onChange={e => setBookEditTitle(e.target.value)} /></label>
          <label>Genre<input value={bookEditGenre} onChange={e => setBookEditGenre(e.target.value)} /></label>
          <label>Target chapters<input type="number" min={1} value={bookEditTargetChapters} onChange={e => setBookEditTargetChapters(+e.target.value)} /></label>
          <label>Premise / Plot<textarea rows={6} value={bookEditPremise} onChange={e => setBookEditPremise(e.target.value)} /></label>
          <label style={{ flexDirection: 'row', gap: '0.5rem', alignItems: 'center' }}>
            <input type="checkbox" checked={bookEditHumanAssisted} onChange={e => setBookEditHumanAssisted(e.target.checked)} />
            Human-assisted generation
          </label>
          <div className="book-edit-actions">
            <button onClick={handleSaveBookEdit}>Save</button>
            <button className="btn-ghost" onClick={() => setEditingBook(false)}>Cancel</button>
          </div>
        </div>
      ) : (
        <>
          <div className="book-overview-header">
            <h2>{book.title}</h2>
            {!isRunning && (
              <button className="btn-edit-book" onClick={() => { setBookEditTitle(book.title); setBookEditPremise(book.premise); setBookEditGenre(book.genre); setBookEditTargetChapters(book.targetChapterCount); setBookEditHumanAssisted(book.humanAssisted ?? false); setEditingBook(true) }} title="Edit book details">✎ Edit</button>
            )}
          </div>
          <p><strong>Premise:</strong> {book.premise}</p>
          <p><strong>Genre:</strong> {book.genre} · <strong>Language:</strong> {book.language} · <strong>Target chapters:</strong> {book.targetChapterCount}{book.humanAssisted ? ' · 🤝 Human-assisted' : ''}</p>
          <div className="phase-actions" style={{ marginTop: '1rem' }}>
            {isPhaseComplete('chapters') ? (
              <>
                <span className="phase-status-badge phase-complete">✅ Chapters Complete</span>
                <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('chapters')}>↺ Reopen</button>
              </>
            ) : (book.chapters ?? []).length > 0 ? (
              <>
                <span className="phase-status-badge phase-not-started">⬜ Chapters Not Started</span>
                <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('chapters')}>✓ Complete</button>
              </>
            ) : null}
            {(book.chapters ?? []).length > 0 && (
              <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('chapters', () => setBook(prev => prev ? { ...prev, chapters: [] } : prev))}>🗑 Clear Chapters</button>
            )}
          </div>

        </>
      )}
    </div>
  )
}
