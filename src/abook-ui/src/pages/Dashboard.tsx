import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { Book } from '../api'
import { createBook, deleteBook, getBooks } from '../api'

export default function Dashboard() {
  const [books, setBooks] = useState<Book[]>([])
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState({ title: '', premise: '', genre: '', targetChapterCount: 10 })
  const navigate = useNavigate()

  useEffect(() => {
    getBooks().then(r => setBooks(r.data))
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

  return (
    <div className="container">
      <div className="header">
        <h1>ABook</h1>
        <button onClick={() => setShowForm(v => !v)}>+ New Book</button>
      </div>

      {showForm && (
        <form className="card" onSubmit={handleCreate}>
          <h2>New Book</h2>
          <label>Title<input value={form.title} onChange={e => setForm(f => ({ ...f, title: e.target.value }))} required /></label>
          <label>Genre<input value={form.genre} onChange={e => setForm(f => ({ ...f, genre: e.target.value }))} /></label>
          <label>Target chapters<input type="number" min={1} max={100} value={form.targetChapterCount} onChange={e => setForm(f => ({ ...f, targetChapterCount: +e.target.value }))} /></label>
          <label>Premise<textarea rows={4} value={form.premise} onChange={e => setForm(f => ({ ...f, premise: e.target.value }))} required /></label>
          <div className="actions">
            <button type="submit">Create</button>
            <button type="button" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}

      <div className="book-grid">
        {books.map(b => (
          <div key={b.id} className="card book-card">
            <Link to={`/books/${b.id}`}><h3>{b.title}</h3></Link>
            <p className="genre">{b.genre}</p>
            <p className="premise">{b.premise.slice(0, 120)}{b.premise.length > 120 ? '…' : ''}</p>
            <div className="meta">
              <span className={`status status-${b.status.toLowerCase()}`}>{b.status}</span>
              <button className="delete-btn" onClick={() => handleDelete(b.id)}>Delete</button>
            </div>
          </div>
        ))}
        {books.length === 0 && <p className="empty">No books yet. Create one to get started.</p>}
      </div>
    </div>
  )
}
