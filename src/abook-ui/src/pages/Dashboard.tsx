import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import type { Book } from '../api'
import { createBook, deleteBook, getBooks } from '../api'

export default function Dashboard() {
  const [books, setBooks] = useState<Book[]>([])
  const [form, setForm] = useState({ title: '', premise: '', genre: '', targetChapterCount: 10, language: 'English', humanAssisted: false })
  const [baseBookId, setBaseBookId] = useState<number | undefined>(undefined)
  const navigate = useNavigate()
  const [showForm, setShowForm] = useState(false)

  useEffect(() => {
    getBooks().then(r => setBooks(r.data)).catch(console.error)
  }, [])

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault()
    const { data } = await createBook({ ...form, baseBookId })
    navigate(`/books/${data.id}`)
  }

  const handleDelete = async (id: number) => {
    if (!confirm('Delete this book?')) return
    await deleteBook(id)
    setBooks(prev => prev.filter(b => b.id !== id))
  }

  const statusClass = (s: string) => `status status-${s.toLowerCase()}`

  return (
    <>
      <div className="page-header">
        <h2>My Books</h2>
        <button className="btn-sm" onClick={() => setShowForm(v => !v)}>{showForm ? '✕ Cancel' : '+ New Book'}</button>
      </div>

      {showForm && (
        <form className="card settings-form" style={{ marginBottom: '1.5rem' }} onSubmit={handleCreate}>
          <h2>New Book</h2>
          <label>Title<input value={form.title} onChange={e => setForm(f => ({ ...f, title: e.target.value }))} required autoFocus /></label>
          <label>Genre<input value={form.genre} onChange={e => setForm(f => ({ ...f, genre: e.target.value }))} placeholder="Fantasy, Adventure" />
            <span className="hint">Comma-separated list of genres</span>
          </label>
          <label>
            Base book (optional)
            <select
              value={baseBookId ?? ''}
              onChange={e => setBaseBookId(e.target.value ? Number(e.target.value) : undefined)}
            >
              <option value="">— None —</option>
              {books.map(b => (
                <option key={b.id} value={b.id}>{b.title}</option>
              ))}
            </select>
            <span className="hint">Copies settings (language, human-assisted mode, prompts, target chapters) and book-scoped LLM config.</span>
          </label>
          <label>Language<input value={form.language} onChange={e => setForm(f => ({ ...f, language: e.target.value }))} placeholder="English" /></label>
          <label>Target chapters<input type="number" min={1} max={100} value={form.targetChapterCount} onChange={e => setForm(f => ({ ...f, targetChapterCount: +e.target.value }))} /></label>
          <label>Premise<textarea rows={4} value={form.premise} onChange={e => setForm(f => ({ ...f, premise: e.target.value }))} required /></label>
          <label style={{ flexDirection: 'row', gap: '0.5rem', alignItems: 'center' }}>
            <input type="checkbox" checked={form.humanAssisted} onChange={e => setForm(f => ({ ...f, humanAssisted: e.target.checked }))} />
            Human-assisted generation
          </label>
          <div className="actions">
            <button type="submit">Create</button>
            <button type="button" className="btn-ghost" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}

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
              {b.baseBookId && (
                <p className="blc-lineage">↪ Based on: {books.find(x => x.id === b.baseBookId)?.title ?? `Book #${b.baseBookId}`}</p>
              )}
            </div>
            <div className="book-list-card-right">
              <span className="blc-chapters">{b.targetChapterCount} chapters</span>
              <button onClick={() => navigate(`/books/${b.id}`)}>Open →</button>
              <button className="btn-icon btn-danger" title="Delete" onClick={() => handleDelete(b.id)}>✕</button>
            </div>
          </div>
        ))}
        {books.length === 0 && !showForm && (
          <p className="empty">No books yet. Click <strong>+ New Book</strong> to get started.</p>
        )}
      </div>
    </>
  )
}
