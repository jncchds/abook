import { useEffect, useState } from 'react'
import { useNavigate, useSearchParams } from 'react-router-dom'
import type { Book } from '../api'
import { createBook, deleteBook, getBooks } from '../api'

export default function Dashboard() {
  const [books, setBooks] = useState<Book[]>([])
  const [form, setForm] = useState({ title: '', premise: '', genre: '', targetChapterCount: 10, language: 'English' })
  const navigate = useNavigate()
  const [searchParams, setSearchParams] = useSearchParams()
  const showForm = searchParams.get('new') === '1'

  useEffect(() => {
    getBooks().then(r => setBooks(r.data)).catch(console.error)
  }, [])

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault()
    const { data } = await createBook(form)
    navigate(`/books/${data.id}`)
  }

  const handleDelete = async (id: number) => {
    if (!confirm('Delete this book?')) return
    await deleteBook(id)
    setBooks(prev => prev.filter(b => b.id !== id))
  }

  const statusClass = (s: string) => `status status-${s.toLowerCase()}`

  return showForm ? (
    <form className="card" style={{ maxWidth: 560 }} onSubmit={handleCreate}>
      <h2>New Book</h2>
      <label>Title<input value={form.title} onChange={e => setForm(f => ({ ...f, title: e.target.value }))} required autoFocus /></label>
      <label>Genre<input value={form.genre} onChange={e => setForm(f => ({ ...f, genre: e.target.value }))} /></label>
      <label>Language<input value={form.language} onChange={e => setForm(f => ({ ...f, language: e.target.value }))} placeholder="English" /></label>
      <label>Target chapters<input type="number" min={1} max={100} value={form.targetChapterCount} onChange={e => setForm(f => ({ ...f, targetChapterCount: +e.target.value }))} /></label>
      <label>Premise<textarea rows={4} value={form.premise} onChange={e => setForm(f => ({ ...f, premise: e.target.value }))} required /></label>
      <div className="actions">
        <button type="submit">Create</button>
        <button type="button" className="btn-ghost" onClick={() => setSearchParams({})}>Cancel</button>
      </div>
    </form>
  ) : (
    <div className="book-list">
      {books.map(b => (
        <div key={b.id} className="book-list-card">
          <div className="book-list-card-left">
            <h3 onClick={() => navigate(`/books/${b.id}`)}>{b.title}</h3>
            <div className="blc-meta">
              {b.genre && <span className="blc-genre">{b.genre}</span>}
              {b.language && <span className="blc-lang">{b.language}</span>}
              <span className={statusClass(b.status)}>{b.status}</span>
            </div>
            {b.premise && <p className="blc-premise">{b.premise}</p>}
          </div>
          <div className="book-list-card-right">
            <span className="blc-chapters">{b.targetChapterCount} chapters</span>
            <button onClick={() => navigate(`/books/${b.id}`)}>Open →</button>
            <button className="btn-icon btn-danger" title="Delete" onClick={() => handleDelete(b.id)}>✕</button>
          </div>
        </div>
      ))}
      {books.length === 0 && (
        <p className="empty">No books yet. Click <strong>➕ New Book</strong> to get started.</p>
      )}
    </div>
  )
}
