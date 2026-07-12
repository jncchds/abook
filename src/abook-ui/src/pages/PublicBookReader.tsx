import { useEffect, useState } from 'react'
import { useParams, useNavigate, Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import { getPublicConfig, getPublicBook } from '../api'
import type { PublicBookDetail, PublicChapter } from '../api'
import { useAuth } from '../hooks/useAuth'

export default function PublicBookReader() {
  const { bookId } = useParams<{ bookId: string }>()
  const navigate = useNavigate()
  const { user } = useAuth()
  const id = Number(bookId)

  const [isPublicMode, setIsPublicMode] = useState<boolean | null>(null)
  const [book, setBook] = useState<PublicBookDetail | null>(null)
  const [loading, setLoading] = useState(true)
  const [selectedChapterId, setSelectedChapterId] = useState<number | null>(null)

  useEffect(() => {
    getPublicConfig()
      .then(r => setIsPublicMode(r.data.isPublicMode))
      .catch(() => setIsPublicMode(false))
  }, [])

  useEffect(() => {
    if (isPublicMode === null) return
    if (!isPublicMode && !user) { setLoading(false); return }

    setLoading(true)
    getPublicBook(id)
      .then(r => {
        setBook(r.data)
        if (r.data.chapters.length > 0) setSelectedChapterId(r.data.chapters[0].id)
      })
      .catch(console.error)
      .finally(() => setLoading(false))
  }, [id, isPublicMode, user])

  if (isPublicMode === null || loading) return <div className="reader-page"><p className="empty">Loading…</p></div>

  if (!isPublicMode && !user) {
    return (
      <div className="reader-page">
        <header className="reader-header">
          <Link to="/library" className="reader-back">← Library</Link>
        </header>
        <div className="library-login-prompt">
          <p>Log in to read this book.</p>
          <button className="btn" onClick={() => navigate('/login')}>Log In</button>
        </div>
      </div>
    )
  }

  if (!book) return <div className="reader-page"><p className="empty">Book not found.</p></div>

  const chapters = book.chapters
  const currentChapter: PublicChapter | undefined = chapters.find(c => c.id === selectedChapterId) ?? chapters[0]
  const currentIndex = chapters.findIndex(c => c.id === currentChapter?.id)
  const prevChapter = currentIndex > 0 ? chapters[currentIndex - 1] : null
  const nextChapter = currentIndex < chapters.length - 1 ? chapters[currentIndex + 1] : null

  return (
    <div className="reader-page">
      <header className="reader-header">
        <div className="reader-header-left">
          <Link to="/library" className="reader-back">← Library</Link>
          <span className="reader-breadcrumb">/ {book.title}</span>
        </div>
        <div className="reader-downloads">
          <span style={{ fontSize: '0.8rem', color: 'var(--text-muted)', marginRight: '0.5rem' }}>Download:</span>
          <button className="btn-sm btn-ghost" onClick={() => { window.location.href = `/api/public/books/${id}/export?format=html` }}>HTML</button>
          <button className="btn-sm btn-ghost" onClick={() => { window.location.href = `/api/public/books/${id}/export?format=fb2` }}>FB2</button>
          <button className="btn-sm btn-ghost" onClick={() => { window.location.href = `/api/public/books/${id}/export?format=epub` }}>EPUB</button>
        </div>
      </header>

      <div className="reader-body">
        {/* Chapter nav panel */}
        <nav className="reader-nav">
          <div className="reader-book-info">
            <span className="reader-book-title">{book.title}</span>
            {book.genre && <span className="reader-book-genre">{book.genre}</span>}
          </div>
          <div className="reader-chapter-list">
            {chapters.map(c => (
              <button
                key={c.id}
                className={`reader-ch-btn${c.id === currentChapter?.id ? ' active' : ''}`}
                onClick={() => setSelectedChapterId(c.id)}
              >
                <span className="reader-ch-num">{c.number}.</span>
                <span className="reader-ch-title">{c.title || 'Chapter ' + c.number}</span>
              </button>
            ))}
          </div>
        </nav>

        {/* Content area */}
        <main className="reader-content">
          {currentChapter ? (
            <>
              <div className="chapter-header">
                <h2>Chapter {currentChapter.number}: {currentChapter.title}</h2>
              </div>
              {currentChapter.outline && (
                <div className="outline" style={{ marginBottom: '1.5rem' }}>
                  <em>{currentChapter.outline}</em>
                </div>
              )}
              <div className="chapter-content">
                <ReactMarkdown>{currentChapter.content}</ReactMarkdown>
              </div>
              <div className="reader-chapter-nav">
                {prevChapter ? (
                  <button className="btn-ghost reader-prev-btn" onClick={() => setSelectedChapterId(prevChapter.id)}>
                    ← Ch {prevChapter.number}: {prevChapter.title || 'Chapter ' + prevChapter.number}
                  </button>
                ) : <span />}
                {nextChapter ? (
                  <button className="btn-ghost reader-next-btn" onClick={() => setSelectedChapterId(nextChapter.id)}>
                    Ch {nextChapter.number}: {nextChapter.title || 'Chapter ' + nextChapter.number} →
                  </button>
                ) : <span />}
              </div>
            </>
          ) : (
            <p className="empty">No chapters available.</p>
          )}
        </main>
      </div>
    </div>
  )
}
