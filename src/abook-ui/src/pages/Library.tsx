import { useEffect, useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { getPublicConfig, getPublicGenres, getPublicBooks } from '../api'
import type { PublicBookItem } from '../api'
import { useAuth } from '../hooks/useAuth'

export default function Library() {
  const { user } = useAuth()
  const navigate = useNavigate()

  const [isPublicMode, setIsPublicMode] = useState<boolean | null>(null)
  const [genres, setGenres] = useState<string[]>([])

  const [authorFilter, setAuthorFilter] = useState('')
  const [genreFilter, setGenreFilter] = useState('')
  const [chapterCountFilter, setChapterCountFilter] = useState('')
  const [appliedFilters, setAppliedFilters] = useState({ author: '', genre: '', chapterCount: '' })
  const [page, setPage] = useState(1)

  const [books, setBooks] = useState<PublicBookItem[]>([])
  const [totalPages, setTotalPages] = useState(1)
  const [totalCount, setTotalCount] = useState(0)
  const [loading, setLoading] = useState(false)

  useEffect(() => {
    getPublicConfig()
      .then(r => setIsPublicMode(r.data.isPublicMode))
      .catch(() => setIsPublicMode(false))
    getPublicGenres()
      .then(r => setGenres(r.data))
      .catch(() => setGenres([]))
  }, [])

  useEffect(() => {
    if (isPublicMode === null) return
    if (!isPublicMode && !user) return // login prompt will show

    setLoading(true)
    const params: Record<string, string | number> = { page }
    if (appliedFilters.author) params.author = appliedFilters.author
    if (appliedFilters.genre) params.genre = appliedFilters.genre
    if (appliedFilters.chapterCount) params.chapterCount = Number(appliedFilters.chapterCount)

    getPublicBooks(params)
      .then(r => {
        setBooks(r.data.items)
        setTotalPages(r.data.totalPages)
        setTotalCount(r.data.totalCount)
      })
      .catch(console.error)
      .finally(() => setLoading(false))
  }, [isPublicMode, user, appliedFilters, page])

  const handleApplyFilters = (e: React.FormEvent) => {
    e.preventDefault()
    setPage(1)
    setAppliedFilters({
      author: authorFilter,
      genre: genreFilter,
      chapterCount: chapterCountFilter,
    })
  }

  const handleClearFilters = () => {
    setAuthorFilter('')
    setGenreFilter('')
    setChapterCountFilter('')
    setPage(1)
    setAppliedFilters({ author: '', genre: '', chapterCount: '' })
  }

  if (isPublicMode === null) return <p className="empty">Loading…</p>

  if (!isPublicMode && !user) {
    return (
      <div className="library-login-prompt">
        <p>Log in to browse books.</p>
        <button className="btn" onClick={() => navigate('/login')}>Log In</button>
      </div>
    )
  }

  return (
    <div>
      <div className="page-header"><h2>Library</h2></div>

      <form className="library-filters card" onSubmit={handleApplyFilters}>
        <label>
          Author
          <input
            value={authorFilter}
            onChange={e => setAuthorFilter(e.target.value)}
            placeholder="Search by author…"
          />
        </label>
        <label>
          Genre
          <select value={genreFilter} onChange={e => setGenreFilter(e.target.value)}>
            <option value="">All genres</option>
            {genres.map(g => (
              <option key={g} value={g}>{g}</option>
            ))}
          </select>
        </label>
        <label>
          Chapter count
          <input
            type="number"
            min={1}
            value={chapterCountFilter}
            onChange={e => setChapterCountFilter(e.target.value)}
            placeholder="Exact count…"
            style={{ width: '8rem' }}
          />
        </label>
        <div className="actions">
          <button type="submit" className="btn">Search</button>
          <button type="button" className="btn-ghost" onClick={handleClearFilters}>Clear</button>
        </div>
      </form>

      {loading ? (
        <p className="empty">Loading…</p>
      ) : books.length === 0 ? (
        <p className="empty">No books found.</p>
      ) : (
        <>
          <p className="library-count">{totalCount} book{totalCount !== 1 ? 's' : ''} found</p>
          <div className="book-list">
            {books.map(b => (
              <div key={b.id} className="book-list-card" onClick={() => navigate(`/library/${b.id}`)} style={{ cursor: 'pointer' }}>
                <div className="book-list-card-left">
                  <h3>{b.title}</h3>
                  <div className="blc-meta">
                    {b.genre && b.genre.split(',').map(g => g.trim()).filter(Boolean).map(g => (
                      <span key={g} className="blc-genre">{g}</span>
                    ))}
                    {b.language && <span className="blc-lang">{b.language}</span>}
                    <span className={`status status-${b.status.toLowerCase()}`}>{b.status}</span>
                  </div>
                  <div className="blc-meta" style={{ marginTop: '0.25rem' }}>
                    <span style={{ fontSize: '0.8rem', color: 'var(--text-muted)' }}>by {b.authorDisplayName}</span>
                  </div>
                </div>
                <div className="book-list-card-right">
                  <span className="blc-chapters">{b.writtenChapterCount} chapters written</span>
                  <button className="btn" onClick={e => { e.stopPropagation(); navigate(`/library/${b.id}`) }}>Read →</button>
                </div>
              </div>
            ))}
          </div>

          {totalPages > 1 && (
            <div className="library-pagination">
              <button className="btn-ghost" disabled={page <= 1} onClick={() => setPage(p => Math.max(1, p - 1))}>← Prev</button>
              <span>Page {page} of {totalPages}</span>
              <button className="btn-ghost" disabled={page >= totalPages} onClick={() => setPage(p => Math.min(totalPages, p + 1))}>Next →</button>
            </div>
          )}
        </>
      )}
    </div>
  )
}
