import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { updateBook } from '../../api'
import ToggleField from '../../components/ToggleField'

export default function Overview() {
  const { book, setBook, canEdit } = useBookContext()

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
    <div>
      {editingBook ? (
        <div className="book-edit-form">
          <label>Title<input value={bookEditTitle} onChange={e => setBookEditTitle(e.target.value)} /></label>
          <label>Genre<input value={bookEditGenre} onChange={e => setBookEditGenre(e.target.value)} placeholder="Fantasy, Adventure" />
            <span className="hint">Comma-separated list of genres</span>
          </label>
          <label>Target chapters<input type="number" min={1} value={bookEditTargetChapters} onChange={e => setBookEditTargetChapters(+e.target.value)} /></label>
          <label>Premise / Plot<textarea rows={6} value={bookEditPremise} onChange={e => setBookEditPremise(e.target.value)} /></label>
          <ToggleField
            checked={bookEditHumanAssisted}
            onChange={setBookEditHumanAssisted}
            label="Human-assisted generation"
            hint="Pauses after each planning phase, and after each chapter's mechanical fixes."
          />
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
              <button className="btn-edit-book" onClick={() => { setBookEditTitle(book.title); setBookEditPremise(book.premise); setBookEditGenre(book.genre); setBookEditTargetChapters(book.targetChapterCount); setBookEditHumanAssisted(book.humanAssisted ?? false); setEditingBook(true) }} title="Edit book details">✎ Edit</button>
            )}
          </div>
          {book.baseBookId && (
            <p className="book-lineage-note">
              ↪ Based on book #{book.baseBookId}
              {book.settingsCopiedAt ? ` · settings copied ${new Date(book.settingsCopiedAt).toLocaleString()}` : ''}
            </p>
          )}
          <p><strong>Premise:</strong> {book.premise}</p>
          <p><strong>Genre:</strong> {book.genre} · <strong>Language:</strong> {book.language} · <strong>Target chapters:</strong> {book.targetChapterCount}{book.humanAssisted ? ' · 🤝 Human-assisted' : ''}</p>


        </>
      )}
    </div>
  )
}
