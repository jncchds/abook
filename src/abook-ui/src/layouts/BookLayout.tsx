import { useState, useRef, useEffect } from 'react'
import { useParams, useNavigate, useLocation, Outlet, Navigate } from 'react-router-dom'
import { BookContextProvider, useBookContext } from '../contexts/BookContext'
import Sidebar, { SidebarBtn, SidebarDivider } from '../components/Sidebar'

function agentCaption(role: string | undefined, state: string | undefined, chapterNum: number | null): string {
  if (state === 'WaitingForInput') return 'Waiting for your answer…'
  const ch = chapterNum ? ` Ch. ${chapterNum}` : ''
  switch (role) {
    case 'StoryBibleAgent':     return 'Crafting Story Bible…'
    case 'CharactersAgent':     return 'Developing Characters…'
    case 'PlotThreadsAgent':    return 'Weaving Plot Threads…'
    case 'Planner':             return 'Gathering clarifications…'
    case 'ChaptersAgent':       return 'Outlining Chapters…'
    case 'Writer':              return `Writing${ch}…`
    case 'Editor':              return `Editing${ch}…`
    case 'ContinuityChecker':   return `Checking Continuity${ch}…`
    case 'Embedder':            return `Indexing${ch}…`
    default:                    return `${role ?? 'Agent'} running…`
  }
}

function BookSidebar() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const navigate = useNavigate()
  const location = useLocation()
  const {
    book, isRunning, runStatus, pendingQuestion,
    streamingChapterId,
    handleWriteBook, handlePlanBook, handleStop, isPhaseComplete,
  } = useBookContext()

  const [showExportMenu, setShowExportMenu] = useState(false)
  const exportMenuRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!showExportMenu) return
    function handleClickOutside(e: MouseEvent) {
      if (exportMenuRef.current && !exportMenuRef.current.contains(e.target as Node)) {
        setShowExportMenu(false)
      }
    }
    document.addEventListener('mousedown', handleClickOutside)
    return () => document.removeEventListener('mousedown', handleClickOutside)
  }, [showExportMenu])

  if (!book) return null

  const isAt = (suffix: string) => location.pathname === `/books/${id}/${suffix}`
  const startsAt = (suffix: string) => location.pathname.startsWith(`/books/${id}/${suffix}`)

  const statusColor = (s: string) => ({
    Outlined: '#94a3b8', Writing: '#f59e0b', Review: '#3b82f6',
    Editing: '#a855f7', Done: '#22c55e'
  })[s] ?? '#94a3b8'

  return (
    <Sidebar
      hasPendingQuestion={!!pendingQuestion}
      bottomChildren={
        <>
          {(book.chapters ?? []).some(c => c.content?.trim()) && (
            <div className="sidebar-split-wrap" ref={exportMenuRef}>
              <button className="sidebar-btn sidebar-split-main" title="Download Book as HTML" onClick={() => { window.location.href = `/api/books/${id}/export?format=html` }}>
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
                  <button onClick={() => { window.location.href = `/api/books/${id}/export?format=html`; setShowExportMenu(false) }}>HTML</button>
                  <button onClick={() => { window.location.href = `/api/books/${id}/export?format=fb2`; setShowExportMenu(false) }}>FB2</button>
                  <button onClick={() => { window.location.href = `/api/books/${id}/export?format=epub`; setShowExportMenu(false) }}>EPUB</button>
                </div>
              )}
            </div>
          )}
          <SidebarBtn
            icon="📄"
            label="Download Metadata"
            onClick={() => { window.location.href = `/api/books/${id}/export?format=metadata` }}
          />
          <SidebarBtn icon="⚙" label="Settings" onClick={() => navigate(`/books/${id}/settings`)} />
        </>
      }
    >
      <SidebarBtn icon="←" label="All Books" onClick={() => navigate('/')} />
      <div className="sidebar-book-info">
        <span className="s-book-title">{book.title}</span>
        <span className="s-book-meta">{book.genre}{book.genre && book.language ? ' · ' : ''}{book.language}</span>
      </div>
      <SidebarDivider />
      {isRunning ? (
        <>
          {(() => {
            const chNum = streamingChapterId
              ? (book.chapters ?? []).find(c => c.id === streamingChapterId)?.number ?? null
              : null
            const label = agentCaption(runStatus?.role, runStatus?.state, chNum)
            return (
              <div className="agent-running-banner" style={{ margin: '0 4px 4px', fontSize: '0.75rem' }}>
                <span className="spinner" />
                <span className="hide-when-collapsed">{label}</span>
              </div>
            )
          })()}
          <SidebarBtn icon="⊙" label="Stop" onClick={handleStop} />
        </>
      ) : (
        <>
          <SidebarBtn icon="▶" label="Write Book" onClick={handleWriteBook} />
          <SidebarBtn icon="🗗" label="Plan Book" onClick={handlePlanBook} />
        </>
      )}
      <SidebarDivider />
      <SidebarBtn icon="📖" label="Overview" active={isAt('overview')} onClick={() => navigate(`/books/${id}/overview`)} />
      <SidebarBtn icon="🌍" label="Story Bible" active={isAt('story-bible')} onClick={() => navigate(`/books/${id}/story-bible`)} dot={isPhaseComplete('storybible')} />
      <SidebarBtn icon="👤" label="Characters" active={isAt('characters')} onClick={() => navigate(`/books/${id}/characters`)} dot={isPhaseComplete('characters')} />
      <SidebarBtn icon="🧵" label="Plot Threads" active={isAt('plot-threads')} onClick={() => navigate(`/books/${id}/plot-threads`)} dot={isPhaseComplete('plotthreads')} />
      <SidebarDivider />
      <SidebarBtn icon="💬" label="Chat" active={isAt('chat')} onClick={() => navigate(`/books/${id}/chat`)} dot={!!pendingQuestion} dotColor={pendingQuestion ? 'var(--warning)' : undefined} />
      <SidebarBtn icon="🪙" label="Token Stats" active={isAt('token-stats')} onClick={() => navigate(`/books/${id}/token-stats`)} />
      <SidebarDivider />
      <SidebarBtn
        icon="📚"
        label="Chapters"
        active={location.pathname === `/books/${id}/chapters`}
        onClick={() => navigate(`/books/${id}/chapters`)}
        dot={isPhaseComplete('chapters')}
      />
      <div className="sidebar-chapters hide-when-collapsed">
        {(book.chapters ?? []).map(c => (
          <div
            key={c.id}
            className={`sidebar-ch-item${startsAt(`chapters/${c.id}`) ? ' active' : ''}`}
            onClick={() => navigate(`/books/${id}/chapters/${c.id}`)}
          >
            <span className="sidebar-ch-num">{c.number}.</span>
            <span className="sidebar-ch-title">{c.title || 'Untitled'}</span>
            <span className="sidebar-ch-dot" style={{ background: statusColor(c.status) }} />
          </div>
        ))}
      </div>
    </Sidebar>
  )
}

function BookLayoutInner() {
  const { bookId } = useParams<{ bookId: string }>()
  const location = useLocation()
  const { book } = useBookContext()

  if (!book) return <div className="loading">Loading…</div>

  // Redirect /books/:id to /books/:id/overview
  if (location.pathname === `/books/${bookId}`) {
    return <Navigate to={`/books/${bookId}/overview`} replace />
  }

  const isChatView = location.pathname.endsWith('/chat')

  return (
    <div className="app-layout">
      <BookSidebar />
      <main className={`app-main${isChatView ? ' view-chat' : ''}`}>
        <Outlet />
      </main>
    </div>
  )
}

export default function BookLayout() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  return (
    <BookContextProvider bookId={id}>
      <BookLayoutInner />
    </BookContextProvider>
  )
}
