import { useEffect, useState } from 'react'
import { Link, useNavigate } from 'react-router-dom'
import type { Book } from '../api'
import { createBook, deleteBook, getBooks, authLogout } from '../api'
import { useAuth } from '../hooks/useAuth'

export default function Dashboard() {
  const [books, setBooks] = useState<Book[]>([])
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState({ title: '', premise: '', genre: '', targetChapterCount: 10, language: 'English' })
  const navigate = useNavigate()
  const { user, setUser } = useAuth()

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

  const handleLogout = async () => {
    await authLogout()
    setUser(null)
    navigate('/login')
  }

  return (
    <div className="container">
      <div className="header">
        <h1>ABook</h1>
        <div className="header-actions">
          <button onClick={() => setShowForm(v => !v)}>+ New Book</button>
          {user?.isAdmin && <Link to="/admin/users" className="btn">👥 Users</Link>}
          <Link to="/presets" className="btn-secondary btn">🔑 Presets</Link>
          <button className="btn-secondary" onClick={handleLogout}>Sign Out ({user?.username})</button>
        </div>
      </div>

      {showForm && (
        <form className="card" onSubmit={handleCreate}>
          <h2>New Book</h2>
          <label>Title<input value={form.title} onChange={e => setForm(f => ({ ...f, title: e.target.value }))} required /></label>
          <label>Genre<input value={form.genre} onChange={e => setForm(f => ({ ...f, genre: e.target.value }))} /></label>
          <label>Language
            <input value={form.language} onChange={e => setForm(f => ({ ...f, language: e.target.value }))} placeholder="English" />
          </label>
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
            <p className="genre">{b.genre} · {b.language}</p>
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
