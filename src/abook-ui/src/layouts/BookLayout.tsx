import { useParams, useNavigate, useLocation, Outlet, Navigate } from 'react-router-dom'
import { BookContextProvider, useBookContext } from '../contexts/BookContext'
import Sidebar, { SidebarBtn, SidebarDivider, SidebarSection } from '../components/Sidebar'
import { downloadBookAsHtml, downloadBookMetadataAsHtml } from '../utils/bookHtmlExport'
import { createChapter } from '../api'
import { useState } from 'react'

function BookSidebar() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const navigate = useNavigate()
  const location = useLocation()
  const {
    book, setBook, isRunning, runStatus, pendingQuestion,
    messages, storyBible, characters, plotThreads, tokenStats,
    handleWriteBook, handlePlanBook, handleStop, handleContinue,
    handleCompletePhase, handleReopenPhase, handleClearPhase, isPhaseComplete,
  } = useBookContext()

  const [addingChapter, setAddingChapter] = useState(false)
  const [newChapterTitle, setNewChapterTitle] = useState('')
  const [newChapterOutline, setNewChapterOutline] = useState('')

  if (!book) return null

  const isAt = (suffix: string) => location.pathname === `/books/${id}/${suffix}`
  const startsAt = (suffix: string) => location.pathname.startsWith(`/books/${id}/${suffix}`)

  const statusColor = (s: string) => ({
    Outlined: '#94a3b8', Writing: '#f59e0b', Review: '#3b82f6',
    Editing: '#a855f7', Done: '#22c55e'
  })[s] ?? '#94a3b8'

  const handleAddChapter = async () => {
    if (!newChapterTitle.trim()) return
    const nextNumber = (book?.chapters?.length ?? 0) + 1
    const r = await createChapter(id, {
      number: nextNumber,
      title: newChapterTitle.trim(),
      outline: newChapterOutline.trim(),
    })
    setBook(prev => prev ? { ...prev, chapters: [...(prev.chapters ?? []), r.data] } : prev)
    setNewChapterTitle('')
    setNewChapterOutline('')
    setAddingChapter(false)
    navigate(`/books/${id}/chapters/${r.data.id}`)
  }

  return (
    <Sidebar
      hasPendingQuestion={!!pendingQuestion}
      bottomChildren={
        <>
          {(book.chapters ?? []).some(c => c.content?.trim()) && (
            <SidebarBtn icon="⬇" label="Download HTML" onClick={() => downloadBookAsHtml(book)} />
          )}
          <SidebarBtn
            icon="📄"
            label="Download Metadata"
            onClick={() => downloadBookMetadataAsHtml(book, storyBible, characters, plotThreads, messages, tokenStats)}
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
          <div className="agent-running-banner hide-when-collapsed" style={{ margin: '0 4px 4px', fontSize: '0.75rem' }}>
            <span className="spinner" /> {runStatus?.state === 'WaitingForInput' ? 'Waiting for input…' : `${runStatus?.role} running…`}
          </div>
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
      <SidebarBtn icon="🏃" label="Current State" active={isAt('state')} onClick={() => navigate(`/books/${id}/state`)} />
      <SidebarBtn icon="🪙" label="Token Stats" active={isAt('token-stats')} onClick={() => navigate(`/books/${id}/token-stats`)} />
      <SidebarDivider />
      <SidebarSection title="Chapters" hideWhenCollapsed />
      {(book.chapters ?? []).length > 0 && !addingChapter && (
        <div className="hide-when-collapsed" style={{ padding: '0 4px 2px' }}>
          <div className="phase-actions" style={{ flexWrap: 'wrap', gap: '0.3rem' }}>
            {isPhaseComplete('chapters') ? (
              <>
                <span className="phase-status-badge phase-complete">✅</span>
                <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('chapters')}>↺</button>
                <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('chapters', () => { setBook(prev => prev ? { ...prev, chapters: [] } : prev); navigate(`/books/${id}/overview`) })}>🗑</button>
              </>
            ) : (
              <>
                <span className="phase-status-badge phase-not-started">⬜</span>
                <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('chapters')}>✓</button>
                <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('chapters', () => { setBook(prev => prev ? { ...prev, chapters: [] } : prev); navigate(`/books/${id}/overview`) })}>🗑</button>
              </>
            )}
          </div>
        </div>
      )}
      <div className="sidebar-chapters">
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
        {(book.chapters ?? []).length === 0 && (
          <p style={{ fontSize: '0.77rem', color: 'var(--text-muted)', padding: '0.25rem 0.5rem' }}>No chapters yet</p>
        )}
      </div>
      {!isRunning && (
        addingChapter ? (
          <div className="add-chapter-form hide-when-collapsed" style={{ padding: '0 4px' }}>
            <input placeholder="Chapter title" value={newChapterTitle} onChange={e => setNewChapterTitle(e.target.value)} autoFocus />
            <textarea rows={2} placeholder="Brief outline (optional)" value={newChapterOutline} onChange={e => setNewChapterOutline(e.target.value)} />
            <div className="add-chapter-actions">
              <button className="btn-sm" onClick={handleAddChapter} disabled={!newChapterTitle.trim()}>Add</button>
              <button className="btn-sm btn-ghost" onClick={() => { setAddingChapter(false); setNewChapterTitle(''); setNewChapterOutline('') }}>Cancel</button>
            </div>
          </div>
        ) : (
          <SidebarBtn icon="+" label="Add Chapter" onClick={() => setAddingChapter(true)} />
        )
      )}
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
