import { useEffect } from 'react'
import { useParams, useNavigate, useOutletContext } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import type { LibraryOutletContext } from '../layouts/LibraryLayout'

/** Redirects to first chapter when navigating to /library/:bookId */
export function PublicBookIndex() {
  const { bookId } = useParams<{ bookId: string }>()
  const navigate = useNavigate()
  const { book, bookLoading } = useOutletContext<LibraryOutletContext>()

  useEffect(() => {
    if (!book) return
    if (book.chapters.length > 0) {
      navigate(`/library/${bookId}/chapters/${book.chapters[0].id}`, { replace: true })
    }
  }, [book, bookId, navigate])

  if (bookLoading) return <p className="empty">Loading…</p>
  if (!book) return <p className="empty">Book not found.</p>
  if (book.chapters.length === 0) return <p className="empty">No chapters available yet.</p>
  return null
}

/** Renders a single chapter by chapterId param */
export default function PublicBookReader() {
  const { bookId, chapterId } = useParams<{ bookId: string; chapterId: string }>()
  const navigate = useNavigate()
  const { book, bookLoading } = useOutletContext<LibraryOutletContext>()

  const id = Number(bookId)
  const currentChapterId = Number(chapterId)

  if (bookLoading) return <p className="empty">Loading…</p>
  if (!book) return <p className="empty">Book not found.</p>

  const chapters = book.chapters
  const currentChapter = chapters.find(c => c.id === currentChapterId) ?? chapters[0]
  const currentIndex = chapters.findIndex(c => c.id === currentChapter?.id)
  const prevChapter = currentIndex > 0 ? chapters[currentIndex - 1] : null
  const nextChapter = currentIndex < chapters.length - 1 ? chapters[currentIndex + 1] : null

  if (!currentChapter) return <p className="empty">Chapter not found.</p>

  return (
    <div className="reader-content">
      <div className="chapter-header">
        <h2>Chapter {currentChapter.number}{currentChapter.title ? ': ' + currentChapter.title : ''}</h2>
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
          <button className="btn-ghost reader-prev-btn" onClick={() => navigate(`/library/${id}/chapters/${prevChapter.id}`)}>
            ← Ch {prevChapter.number}: {prevChapter.title || 'Chapter ' + prevChapter.number}
          </button>
        ) : <span />}
        {nextChapter ? (
          <button className="btn-ghost reader-next-btn" onClick={() => navigate(`/library/${id}/chapters/${nextChapter.id}`)}>
            Ch {nextChapter.number}: {nextChapter.title || 'Chapter ' + nextChapter.number} →
          </button>
        ) : <span />}
      </div>
    </div>
  )
}
