import { useEffect } from 'react'
import { useParams, useNavigate, useOutletContext } from 'react-router-dom'
import type { LibraryOutletContext } from '../layouts/LibraryLayout'
import ChapterContent from '../components/ChapterContent'
import IllustrationFigure from '../components/IllustrationFigure'
import { publicBookImagePath } from '../api'

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

  const scenes = (book.illustrations ?? []).filter(i => i.kind === 'Scene' && i.chapterId === currentChapter.id)
  const cover = currentIndex === 0 ? (book.illustrations ?? []).find(i => i.kind === 'Cover') : undefined

  return (
    <div>
      {currentChapter.part && <h1 className="reader-part-heading">{currentChapter.part}</h1>}
      <div className="page-header">
        <h2>Chapter {currentChapter.number}{currentChapter.title ? ': ' + currentChapter.title : ''}</h2>
      </div>
      {cover && (
        <IllustrationFigure bookId={id} illustration={cover} imagePath={imageId => publicBookImagePath(id, imageId)} />
      )}
      <div className="chapter-content">
        <ChapterContent
          content={currentChapter.content}
          illustrations={scenes}
          renderFigure={ill => (
            <IllustrationFigure bookId={id} illustration={ill} imagePath={imageId => publicBookImagePath(id, imageId)} />
          )}
        />
      </div>
      <div className="chapter-prev-next">
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
