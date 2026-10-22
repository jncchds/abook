import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import axios from 'axios'
import { updateBook } from '../../api'

export default function Overview() {
  const { book, setBook, canEdit } = useBookContext()

  const [editingBook, setEditingBook] = useState(false)
  const [bookEditTitle, setBookEditTitle] = useState('')
  const [bookEditPremise, setBookEditPremise] = useState('')
  const [bookEditGenre, setBookEditGenre] = useState('')
  const [bookEditTargetChapters, setBookEditTargetChapters] = useState(0)
  const [error, setError] = useState<string | null>(null)

  if (!book) return null
  const interactive = book.mode === 'Interactive'
  const premiseLocked = interactive && !!book.storyBibleLockedAt

  const handleSaveBookEdit = async () => {
    setError(null)
    try {
      const r = await updateBook(book.id, {
        title: bookEditTitle,
        premise: bookEditPremise,
        genre: bookEditGenre,
        targetChapterCount: bookEditTargetChapters,
        status: book.status as never,
        language: book.language,
        mode: book.mode,
      })
      setBook(r.data)
      setEditingBook(false)
    } catch (err) {
      setError(axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err))
    }
  }

  return (
    <div>
      {editingBook ? (
        <div className="book-edit-form">
          <label>Title<input value={bookEditTitle} onChange={e => setBookEditTitle(e.target.value)} /></label>
          <label>Genre<input value={bookEditGenre} onChange={e => setBookEditGenre(e.target.value)} placeholder="Fantasy, Adventure" />
            <span className="hint">Comma-separated list of genres</span>
          </label>
          {!interactive && (
            <label>Target chapters<input type="number" min={1} value={bookEditTargetChapters} onChange={e => setBookEditTargetChapters(+e.target.value)} /></label>
          )}
          <label>Premise / Plot
            <textarea rows={6} value={bookEditPremise} onChange={e => setBookEditPremise(e.target.value)} disabled={premiseLocked} />
            {premiseLocked && <span className="hint">🔒 Locked while writing — unlock the Story Bible in Book Settings to change it.</span>}
          </label>
          {error && <p className="error-msg" role="alert">{error}</p>}
          <div className="book-edit-actions">
            <button onClick={handleSaveBookEdit}>Save</button>
            <button className="btn-ghost" onClick={() => setEditingBook(false)}>Cancel</button>
          </div>
        </div>
      ) : (
        <>
          <div className="page-header">
            <h2>{book.title}</h2>
            {canEdit && (
              <button className="btn-edit-book" onClick={() => { setBookEditTitle(book.title); setBookEditPremise(book.premise); setBookEditGenre(book.genre); setBookEditTargetChapters(book.targetChapterCount); setError(null); setEditingBook(true) }} title="Edit book details">✎ Edit</button>
            )}
          </div>
          {book.baseBookId && (
            <p className="book-lineage-note">
              ↪ Based on book #{book.baseBookId}
              {book.settingsCopiedAt ? ` · settings copied ${new Date(book.settingsCopiedAt).toLocaleString()}` : ''}
            </p>
          )}
          <p><strong>Premise:</strong> {book.premise}</p>
          <p><strong>Genre:</strong> {book.genre} · <strong>Language:</strong> {book.language} · {interactive
            ? <><strong>Mode:</strong> ✍ Interactive{book.storyBibleLockedAt ? ' · 🔒 writing' : ' · 🌱 foundation'}</>
            : <><strong>Target chapters:</strong> {book.targetChapterCount}</>}</p>


        </>
      )}
    </div>
  )
}
