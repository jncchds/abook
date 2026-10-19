import { useState, useRef, useEffect } from 'react'
import { useParams, useNavigate, useLocation, Outlet, Navigate } from 'react-router-dom'
import { BookContextProvider, useBookContext } from '../contexts/BookContext'
import Sidebar, { SidebarBtn, SidebarDivider } from '../components/Sidebar'
import { chapterStatusColor } from '../utils/chapterStatus'
import { downloadFile } from '../api'
import SectionChatPanel from '../components/SectionChatPanel'
import { sectionPath } from '../utils/sectionChat'

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
    case 'ContinuityChecker':   return `Checking${ch}…`
    case 'Embedder':            return `Indexing${ch}…`
    case 'IllustrationPlanner': return `Planning illustrations${ch}…`
    case 'Illustrator':         return 'Rendering illustrations…'
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
    illustrations, handleRenderIllustrations, openClarificationCount, sectionTarget,
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

  const statusColor = chapterStatusColor

  return (
    <Sidebar
      hasPendingQuestion={!!pendingQuestion || openClarificationCount > 0}
      bottomChildren={
        <>
          {(book.chapters ?? []).some(c => !c.isArchived && c.content?.trim()) && (
            <div className="sidebar-split-wrap" ref={exportMenuRef}>
              <button className="sidebar-btn sidebar-split-main" title="Download Book as HTML" onClick={() => { void downloadFile(`/books/${id}/export?format=html`) }}>
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
                  <button onClick={() => { void downloadFile(`/books/${id}/export?format=html`); setShowExportMenu(false) }}>HTML</button>
                  <button onClick={() => { void downloadFile(`/books/${id}/export?format=fb2`); setShowExportMenu(false) }}>FB2</button>
                  <button onClick={() => { void downloadFile(`/books/${id}/export?format=epub`); setShowExportMenu(false) }}>EPUB</button>
                </div>
              )}
            </div>
          )}
          <SidebarBtn
            icon="📄"
            label="Download Metadata"
            onClick={() => { void downloadFile(`/books/${id}/export?format=metadata`) }}
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
            const chId = runStatus?.chapterId ?? streamingChapterId
            const chNum = chId
              ? (book.chapters ?? []).find(c => c.id === chId)?.number ?? null
              : null
            const label = runStatus?.state === 'WaitingForInput' && openClarificationCount > 0
              ? 'Waiting for your clarifications…'
              : runStatus?.state === 'WaitingForInput' && sectionTarget
                ? 'Waiting for your review…'
                : agentCaption(runStatus?.role, runStatus?.state, chNum)
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
          {(book.illustrationsEnabled || illustrations.length > 0) && (() => {
            // Rendering is its own run (image model only), started once the book is written.
            const liveChapters = new Set((book.chapters ?? []).filter(c => !c.isArchived).map(c => c.id))
            const pending = illustrations.filter(i =>
              i.status !== 'Rendered' && (i.kind !== 'Scene' || (i.chapterId !== null && liveChapters.has(i.chapterId)))).length
            return (
              <SidebarBtn
                icon="🎨"
                label={pending > 0 ? `Illustrate Book (${pending})` : 'Illustrate Book'}
                title={pending > 0
                  ? 'Render every planned illustration with the image model (no LLM calls)'
                  : 'Nothing to render — open the Illustrations page'}
                onClick={() => pending > 0 ? handleRenderIllustrations('All') : navigate(`/books/${id}/illustrations`)}
              />
            )
          })()}
        </>
      )}
      <SidebarDivider />
      <SidebarBtn icon="📖" label="Overview" active={isAt('overview')} onClick={() => navigate(`/books/${id}/overview`)} />
      <SidebarBtn
        icon="❓"
        label={openClarificationCount > 0 ? `Clarifications (${openClarificationCount})` : 'Clarifications'}
        active={isAt('clarifications')}
        onClick={() => navigate(`/books/${id}/clarifications`)}
        dot={openClarificationCount > 0 || isPhaseComplete('clarifications')}
        dotColor={openClarificationCount > 0 ? 'var(--warning)' : undefined}
      />
      <SidebarBtn icon="🌍" label="Story Bible" active={isAt('story-bible')} onClick={() => navigate(`/books/${id}/story-bible`)} dot={isPhaseComplete('storybible')} />
      <SidebarBtn icon="👤" label="Characters" active={isAt('characters')} onClick={() => navigate(`/books/${id}/characters`)} dot={isPhaseComplete('characters')} />
      <SidebarBtn icon="🧵" label="Plot Threads" active={isAt('plot-threads')} onClick={() => navigate(`/books/${id}/plot-threads`)} dot={isPhaseComplete('plotthreads')} />
      <SidebarBtn icon="🎨" label="Illustrations" active={isAt('illustrations')} onClick={() => navigate(`/books/${id}/illustrations`)} />
      <SidebarDivider />
      <SidebarBtn icon="💬" label="Agent Messages" active={isAt('chat')} onClick={() => navigate(`/books/${id}/chat`)} dot={!!pendingQuestion} dotColor={pendingQuestion ? 'var(--warning)' : undefined} />
      <SidebarBtn icon="🪙" label="Token Stats" active={isAt('token-stats')} onClick={() => navigate(`/books/${id}/token-stats`)} />
      <SidebarDivider />
      <SidebarBtn
        icon="📚"
        label="Chapters"
        active={location.pathname === `/books/${id}/chapters`}
        onClick={() => navigate(`/books/${id}/chapters`)}
        dot={isPhaseComplete('chapters')}
      />
      <div className="sidebar-chapters">
        {(book.chapters ?? []).filter(c => !c.isArchived).map(c => (
          <button
            key={c.id}
            className={`sidebar-btn${startsAt(`chapters/${c.id}`) ? ' active' : ''}`}
            onClick={() => navigate(`/books/${id}/chapters/${c.id}`)}
          >
            <span className="s-icon">
              <span className="lib-ch-circle">{c.number}</span>
            </span>
            <span className="s-label">{c.title || 'Untitled'}</span>
            <span className="sidebar-ch-dot" style={{ background: statusColor(c.status) }} />
          </button>
        ))}
      </div>
    </Sidebar>
  )
}

function BookLayoutInner() {
  const { bookId } = useParams<{ bookId: string }>()
  const location = useLocation()
  const { book, sectionTarget } = useBookContext()

  if (!book) return <div className="loading">Loading…</div>

  // Redirect /books/:id to /books/:id/overview
  if (location.pathname === `/books/${bookId}`) {
    return <Navigate to={`/books/${bookId}/overview`} replace />
  }

  const isChatView = location.pathname.endsWith('/chat')
  // The section chat sits next to the page of the section the run is paused on.
  const showSectionChat = !!sectionTarget && location.pathname === sectionPath(book.id, sectionTarget)

  return (
    <div className="app-layout">
      <BookSidebar />
      <main className={`app-main${isChatView ? ' view-chat' : ''}${showSectionChat ? ' with-section-chat' : ''}`}>
        <Outlet />
      </main>
      {showSectionChat && <SectionChatPanel />}
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
