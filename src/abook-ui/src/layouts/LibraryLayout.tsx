import { useState, useEffect, useRef } from 'react'
import { Outlet, useParams, useNavigate, useLocation } from 'react-router-dom'
import Sidebar, { SidebarBtn, SidebarDivider } from '../components/Sidebar'
import { useAuth } from '../hooks/useAuth'
import { getPublicBook, authLogout } from '../api'
import type { PublicBookDetail } from '../api'

export interface LibraryOutletContext {
  book: PublicBookDetail | null
  bookLoading: boolean
}

export default function LibraryLayout() {
  const { bookId, chapterId } = useParams<{ bookId?: string; chapterId?: string }>()
  const navigate = useNavigate()
  const location = useLocation()
  const { user, setUser } = useAuth()

  const id = bookId ? Number(bookId) : null
  const currentChapterId = chapterId ? Number(chapterId) : null

  const [book, setBook] = useState<PublicBookDetail | null>(null)
  const [bookLoading, setBookLoading] = useState(false)
  const [showExportMenu, setShowExportMenu] = useState(false)
  const exportMenuRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!id) { setBook(null); return }
    setBookLoading(true)
    getPublicBook(id)
      .then(r => setBook(r.data))
      .catch(() => setBook(null))
      .finally(() => setBookLoading(false))
  }, [id])

  useEffect(() => {
    if (!showExportMenu) return
    const handleClickOutside = (e: MouseEvent) => {
      if (exportMenuRef.current && !exportMenuRef.current.contains(e.target as Node)) {
        setShowExportMenu(false)
      }
    }
    document.addEventListener('mousedown', handleClickOutside)
    return () => document.removeEventListener('mousedown', handleClickOutside)
  }, [showExportMenu])

  const handleLogout = async () => {
    await authLogout()
    setUser(null)
    navigate('/library')
  }

  const isOnLibraryRoot = !id && location.pathname === '/library'

  const bottomChildren = (
    <>
      {id && book && book.chapters.some(c => c.content?.trim()) && (
        <div className="sidebar-split-wrap" ref={exportMenuRef}>
          <button
            className="sidebar-btn sidebar-split-main"
            title="Download as HTML"
            onClick={() => { window.location.href = `/api/public/books/${id}/export?format=html` }}
          >
            <span className="s-icon">⬇</span>
            <span className="s-label">Download Book</span>
          </button>
          <button
            className="sidebar-btn sidebar-split-arrow"
            title="More export formats"
            onClick={() => setShowExportMenu(v => !v)}
          >▾</button>
          {showExportMenu && (
            <div className="sidebar-split-menu">
              <button onClick={() => { window.location.href = `/api/public/books/${id}/export?format=html`; setShowExportMenu(false) }}>HTML</button>
              <button onClick={() => { window.location.href = `/api/public/books/${id}/export?format=fb2`; setShowExportMenu(false) }}>FB2</button>
              <button onClick={() => { window.location.href = `/api/public/books/${id}/export?format=epub`; setShowExportMenu(false) }}>EPUB</button>
            </div>
          )}
        </div>
      )}
      {user ? (
        <SidebarBtn icon="🚪" label={`Sign Out (${user.username})`} onClick={handleLogout} />
      ) : (
        <SidebarBtn icon="🔑" label="Log In" onClick={() => navigate('/login')} />
      )}
    </>
  )

  return (
    <div className="app-layout">
      <Sidebar bottomChildren={bottomChildren}>
        {user && (
          <>
            <SidebarBtn icon="←" label="My Books" onClick={() => navigate('/')} />
            <SidebarDivider />
          </>
        )}
        <SidebarBtn
          icon="📚"
          label="Library"
          active={isOnLibraryRoot}
          onClick={() => navigate('/library')}
        />

        {book && (
          <>
            <SidebarDivider />
            <div className="sidebar-book-info">
              <span className="s-book-title">{book.title}</span>
              {book.genre && <span className="s-book-meta">{book.genre}</span>}
              <SidebarDivider />
            </div>
            <div>
              {book.chapters.map(c => (
                <button
                  key={c.id}
                  className={`sidebar-btn${currentChapterId === c.id ? ' active' : ''}`}
                  onClick={() => navigate(`/library/${id}/chapters/${c.id}`)}
                  title={c.title || 'Chapter ' + c.number}
                >
                  <span className="s-icon">
                    <span className="lib-ch-circle">{c.number}</span>
                  </span>
                  <span className="s-label">{c.title || 'Chapter ' + c.number}</span>
                </button>
              ))}
            </div>
          </>
        )}
      </Sidebar>
      <main className="app-main">
        <Outlet context={{ book, bookLoading } satisfies LibraryOutletContext} />
      </main>
    </div>
  )
}
