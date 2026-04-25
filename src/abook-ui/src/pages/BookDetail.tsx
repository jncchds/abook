import { useEffect, useState, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import type { Book, Chapter, AgentMessage, AgentRunStatus, StoryBible, CharacterCard, PlotThread } from '../api'
import {
  getBook, getMessages, postAnswer, getAgentStatus,
  startPlanning, continuePlanning, completePlanningPhase, reopenPlanningPhase, clearPlanningPhase,
  startWorkflow, continueWorkflow, stopWorkflow, clearChapterContent,
  createChapter, updateChapter, updateBook, getTokenUsage, clearMessages, clearTokenUsage,
  getStoryBible, updateStoryBible,
  getCharacters, createCharacter, updateCharacter, deleteCharacter,
  getPlotThreads, createPlotThread, updatePlotThread, deletePlotThread,
} from '../api'
import { useBookHub } from '../hooks/useBookHub'
import { downloadBookAsHtml, downloadBookMetadataAsHtml } from '../utils/bookHtmlExport'

// Attempt to extract chapter plan entries from partial JSON
function parsePlanningStream(raw: string): { number: number; title: string; outline: string }[] {
  const results: { number: number; title: string; outline: string }[] = []
  // Match complete objects: { "number": N, "title": "...", "outline": "..." }
  const re = /\{\s*"number"\s*:\s*(\d+)\s*,\s*"title"\s*:\s*"([^"\\]*)"\s*,\s*"outline"\s*:\s*"([^"\\]*)"\s*\}/g
  let m: RegExpExecArray | null
  while ((m = re.exec(raw)) !== null) {
    try { results.push({ number: +m[1], title: m[2], outline: m[3] }) } catch { /* skip malformed */ }
  }
  return results
}

// Progressively extract Story Bible fields from partial JSON
function parseStoryBibleStream(raw: string): Partial<StoryBible> {
  const result: Partial<StoryBible> = {}
  const fields = ['settingDescription', 'timePeriod', 'themes', 'toneAndStyle', 'worldRules', 'notes'] as const
  for (const field of fields) {
    try {
      const re = new RegExp(`"${field}"\\s*:\\s*"((?:[^"\\\\]|\\\\.)*)"`, 's')
      const m = re.exec(raw)
      if (m) (result as Record<string, string>)[field] = m[1].replace(/\\n/g, '\n').replace(/\\"/g, '"')
    } catch { /* skip */ }
  }
  return result
}

// Progressively extract character cards from partial JSON stream
function parseCharactersStream(raw: string): Partial<CharacterCard>[] {
  const results: Partial<CharacterCard>[] = []
  let depth = 0, start = -1
  for (let i = 0; i < raw.length; i++) {
    if (raw[i] === '{') { if (depth === 0) start = i; depth++ }
    else if (raw[i] === '}') {
      depth--
      if (depth === 0 && start >= 0) {
        try {
          const obj = JSON.parse(raw.slice(start, i + 1))
          if (obj.name) results.push(obj)
        } catch { /* skip malformed */ }
        start = -1
      }
    }
  }
  return results
}

// Progressively extract plot threads from partial JSON stream
function parsePlotThreadsStream(raw: string): Partial<PlotThread>[] {
  const results: Partial<PlotThread>[] = []
  let depth = 0, start = -1
  for (let i = 0; i < raw.length; i++) {
    if (raw[i] === '{') { if (depth === 0) start = i; depth++ }
    else if (raw[i] === '}') {
      depth--
      if (depth === 0 && start >= 0) {
        try {
          const obj = JSON.parse(raw.slice(start, i + 1))
          if (obj.name) results.push(obj)
        } catch { /* skip malformed */ }
        start = -1
      }
    }
  }
  return results
}

export default function BookDetail() {
  const { id } = useParams<{ id: string }>()
  const bookId = Number(id)

  const [book, setBook] = useState<Book | null>(null)
  const [messages, setMessages] = useState<AgentMessage[]>([])
  const [activeChapter, setActiveChapter] = useState<Chapter | null>(null)
  const [plannerBuffer, setPlannerBuffer] = useState('')
  const [streamBuffer, setStreamBuffer] = useState('')
  const [streamingChapterId, setStreamingChapterId] = useState<number | null>(null)
  const [storyBibleStream, setStoryBibleStream] = useState('')
  const [charactersStream, setCharactersStream] = useState('')
  const [plotThreadsStream, setPlotThreadsStream] = useState('')
  const [answerText, setAnswerText] = useState('')
  const [pendingQuestion, setPendingQuestion] = useState<AgentMessage | null>(null)
  const [runStatus, setRunStatus] = useState<AgentRunStatus | null>(null)
  const [workflowLog, setWorkflowLog] = useState<string[]>([])

  // Token stats — persisted (from DB) + live (from SignalR during current session)
  interface TokenStat { id: number; chapterId: number | null; role: string; prompt: number; completion: number; time: string; persisted?: boolean }
  const [tokenStats, setTokenStats] = useState<TokenStat[]>([])

  // Chapter inline editing
  const [editingChapter, setEditingChapter] = useState(false)
  const [chapterEditTitle, setChapterEditTitle] = useState('')
  const [chapterEditOutline, setChapterEditOutline] = useState('')

  // Book inline editing
  const [editingBook, setEditingBook] = useState(false)
  const [bookEditTitle, setBookEditTitle] = useState('')
  const [bookEditPremise, setBookEditPremise] = useState('')
  const [bookEditGenre, setBookEditGenre] = useState('')
  const [bookEditTargetChapters, setBookEditTargetChapters] = useState(0)

  // Mobile panel navigation
  const [mobilePanel, setMobilePanel] = useState<'sidebar' | 'content' | 'chat'>('content')

  // Add chapter manually
  const [addingChapter, setAddingChapter] = useState(false)
  const [newChapterTitle, setNewChapterTitle] = useState('')
  const [newChapterOutline, setNewChapterOutline] = useState('')

  // Overview tabs
  const [activeTab, setActiveTab] = useState<'overview' | 'storybible' | 'characters' | 'plotthreads'>('overview')

  // Story Bible
  const [storyBible, setStoryBible] = useState<StoryBible | null>(null)
  const [editingBible, setEditingBible] = useState(false)
  const [bibleForm, setBibleForm] = useState<Partial<StoryBible>>({})

  // Characters
  const [characters, setCharacters] = useState<CharacterCard[]>([])
  const [editingCharId, setEditingCharId] = useState<number | null>(null)
  const [addingChar, setAddingChar] = useState(false)
  const [charForm, setCharForm] = useState<Partial<CharacterCard>>({})

  // Plot Threads
  const [plotThreads, setPlotThreads] = useState<PlotThread[]>([])
  const [editingThreadId, setEditingThreadId] = useState<number | null>(null)
  const [addingThread, setAddingThread] = useState(false)
  const [threadForm, setThreadForm] = useState<Partial<PlotThread>>({})

  const chatBottomRef = useRef<HTMLDivElement>(null)
  const workflowLogEndRef = useRef<HTMLDivElement>(null)

  const { setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError } = useBookHub(bookId)

  const refreshBook = useCallback(() =>
    getBook(bookId).then(r => setBook(r.data)), [bookId])

  const refreshMessages = useCallback(() =>
    getMessages(bookId).then(r => setMessages(r.data)), [bookId])

  useEffect(() => {
    refreshBook()
    // Load messages and agent status together so we can restore any pending question after a page refresh
    Promise.all([
      getMessages(bookId),
      getAgentStatus(bookId).catch(() => ({ data: null as AgentRunStatus | null }))
    ]).then(([msgRes, statusRes]) => {
      setMessages(msgRes.data)
      if (statusRes.data) setRunStatus(statusRes.data)
      if (statusRes.data?.state === 'WaitingForInput') {
        const q = [...msgRes.data].reverse().find(m => m.messageType === 'Question' && !m.isResolved)
        if (q) setPendingQuestion(q)
      }
    }).catch(() => {})
    getTokenUsage(bookId).then(r => {
      setTokenStats(r.data.map(rec => ({
        id: rec.id,
        chapterId: rec.chapterId,
        role: rec.agentRole,
        prompt: rec.promptTokens,
        completion: rec.completionTokens,
        time: new Date(rec.createdAt).toLocaleString(),
        persisted: true
      })))
    }).catch(() => {})
    getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
    getCharacters(bookId).then(r => setCharacters(r.data)).catch(() => {})
    getPlotThreads(bookId).then(r => setPlotThreads(r.data)).catch(() => {})
  }, [refreshBook, bookId])

  useEffect(() => {
    setOnStream((_bId, cId, agentRole, token) => {
      if (agentRole === 'StoryBibleAgent') {
        setStoryBibleStream(prev => prev + token)
      } else if (agentRole === 'CharactersAgent') {
        setCharactersStream(prev => prev + token)
      } else if (agentRole === 'PlotThreadsAgent') {
        setPlotThreadsStream(prev => prev + token)
      } else if (cId === null) {
        // Planner stream — buffer for planning preview
        setPlannerBuffer(prev => prev + token)
      } else {
        // Chapter stream — accumulate in streamBuffer (replaces chapter content view while streaming)
        setStreamingChapterId(cId)
        setStreamBuffer(prev => prev + token)
      }
    })
    setOnQuestion((_bId, msg) => {
      const m = msg as AgentMessage
      setPendingQuestion(m)
      setMessages(prev => [...prev, m])
      setMobilePanel('chat')
    })
    setOnStatus((_bId, role, state) => {
      setRunStatus(state === 'Done' || state === 'Failed' || state === 'Cancelled' ? null : { role, state, chapterId: undefined })
      if (state === 'Done' || state === 'Failed' || state === 'Cancelled') {
        setPlannerBuffer('')
        setStreamBuffer('')
        setStreamingChapterId(null)
        setStoryBibleStream('')
        setCharactersStream('')
        setPlotThreadsStream('')
        refreshBook()
        refreshMessages()
      }
    })
    setOnChapterUpdated((_bId, cId) => {
      setStreamBuffer('')
      setStreamingChapterId(null)
      getBook(bookId).then(r => {
        setBook(r.data)
        setActiveChapter(prev => {
          if (!prev || prev.id !== cId) return prev
          return r.data.chapters?.find(c => c.id === cId) ?? prev
        })
      })
      getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
      getCharacters(bookId).then(r => setCharacters(r.data)).catch(() => {})
      getPlotThreads(bookId).then(r => setPlotThreads(r.data)).catch(() => {})
    })
    setOnWorkflowProgress((_bId, step, isComplete) => {
      setWorkflowLog(prev => [...prev, step])
      if (isComplete) {
        setRunStatus(null)
        setPlannerBuffer('')
        setStreamBuffer('')
        setStreamingChapterId(null)
        setStoryBibleStream('')
        setCharactersStream('')
        setPlotThreadsStream('')
        refreshBook()
        refreshMessages()
      }
      // When a new planning phase starts, the previous phase has already been saved to DB.
      // Refresh the relevant data and auto-switch to its tab (only when no chapter is open).
      if (step.includes('Phase 2/4')) {
        // Phase 1 (Story Bible) just finished
        setStoryBibleStream('')
        getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
        setActiveChapter(prev => { if (!prev) setActiveTab('storybible'); return prev })
      } else if (step.includes('Phase 3/4')) {
        // Phase 2 (Characters) just finished
        setCharactersStream('')
        getCharacters(bookId).then(r => setCharacters(r.data)).catch(() => {})
        setActiveChapter(prev => { if (!prev) setActiveTab('characters'); return prev })
      } else if (step.includes('Phase 4/4')) {
        // Phase 3 (Plot Threads) just finished
        setPlotThreadsStream('')
        getPlotThreads(bookId).then(r => setPlotThreads(r.data)).catch(() => {})
        setActiveChapter(prev => { if (!prev) setActiveTab('plotthreads'); return prev })
      }
    })
    setOnTokenStats((_bId, cId, role, prompt, completion) => {
      // Reload from DB so the new persisted record is shown (avoids duplicates from live + stored)
      getTokenUsage(bookId).then(r => {
        setTokenStats(r.data.map(rec => ({
          id: rec.id,
          chapterId: rec.chapterId,
          role: rec.agentRole,
          prompt: rec.promptTokens,
          completion: rec.completionTokens,
          time: new Date(rec.createdAt).toLocaleString(),
          persisted: true
        })))
      }).catch(() => {
        // Fallback: add the live stat if DB reload fails
        setTokenStats(prev => [...prev, {
          id: Date.now(),
          chapterId: cId,
          role,
          prompt,
          completion,
          time: new Date().toLocaleTimeString()
        }])
      })
    })
    setOnAgentError((_bId, _role, _message) => {
      // Error is persisted as a chat message on the backend — just reload messages
      refreshMessages()
      setMobilePanel('chat')
    })
  }, [setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError, refreshBook, refreshMessages])

  useEffect(() => {
    chatBottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  useEffect(() => {
    workflowLogEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [workflowLog])

  const isRunning = runStatus?.state === 'Running' || runStatus?.state === 'WaitingForInput'

  const handleWriteBook = async () => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await startWorkflow(bookId)
      setRunStatus({ role: 'Planner', state: 'Running' })
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string }; status?: number } })
      if (msg?.response?.status === 409) {
        alert('An agent is already running for this book.')
      }
    }
  }

  const handlePlanBook = async () => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await startPlanning(bookId)
      setRunStatus({ role: 'Planner', state: 'Running' })
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string }; status?: number } })
      if (msg?.response?.status === 409) {
        alert('An agent is already running for this book.')
      }
    }
  }

  const handleStop = async () => {
    try {
      await stopWorkflow(bookId)
    } catch {
      // ignore
    }
  }

  const handleContinue = async () => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await continueWorkflow(bookId)
      setRunStatus({ role: 'Writer', state: 'Running' })
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string }; status?: number } })
      if (msg?.response?.status === 409) {
        alert('An agent is already running for this book.')
      }
    }
  }

  const handleContinuePlanning = async () => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await continuePlanning(bookId)
      setRunStatus({ role: 'Planner', state: 'Running' })
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string }; status?: number } })
      if (msg?.response?.status === 409) {
        alert('An agent is already running for this book.')
      }
    }
  }

  const handleCompletePhase = async (phase: string) => {
    await completePlanningPhase(bookId, phase)
    refreshBook()
  }

  const handleReopenPhase = async (phase: string) => {
    await reopenPlanningPhase(bookId, phase)
    refreshBook()
  }

  const handleClearPhase = async (phase: string, clearLocal: () => void) => {
    const labels: Record<string, string> = { storybible: 'Story Bible', characters: 'Characters', plotthreads: 'Plot Threads', chapters: 'Chapter Outlines' }
    if (!confirm(`Clear all ${labels[phase] ?? phase} data? This cannot be undone.`)) return
    await clearPlanningPhase(bookId, phase)
    clearLocal()
    refreshBook()
  }

  const handleClearChapter = async (chapter: Chapter) => {
    if (!confirm(`Clear all content for Chapter ${chapter.number}: "${chapter.title}"? This cannot be undone.`)) return
    const r = await clearChapterContent(bookId, chapter)
    setActiveChapter(r.data)
    setBook(prev => prev ? {
      ...prev,
      chapters: (prev.chapters ?? []).map(c => c.id === r.data.id ? r.data : c)
    } : prev)
  }

  const handleAnswer = async () => {
    if (!pendingQuestion || !answerText.trim()) return
    await postAnswer(bookId, pendingQuestion.id, answerText.trim())
    setAnswerText('')
    setPendingQuestion(null)
    refreshMessages()
  }

  const handleSaveChapterEdit = async () => {
    if (!activeChapter) return
    const r = await updateChapter(bookId, activeChapter.id, {
      title: chapterEditTitle,
      outline: chapterEditOutline,
      content: activeChapter.content,
      status: activeChapter.status as never,
    })
    setActiveChapter(r.data)
    setBook(prev => prev ? { ...prev, chapters: (prev.chapters ?? []).map(c => c.id === r.data.id ? r.data : c) } : prev)
    setEditingChapter(false)
  }

  const handleSaveBookEdit = async () => {
    if (!book) return
    const r = await updateBook(bookId, {
      title: bookEditTitle,
      premise: bookEditPremise,
      genre: bookEditGenre,
      targetChapterCount: bookEditTargetChapters,
      status: book.status as never,
      language: book.language,
    })
    setBook(r.data)
    setEditingBook(false)
  }

  const handleAddChapter = async () => {
    if (!newChapterTitle.trim()) return
    const nextNumber = (book?.chapters?.length ?? 0) + 1
    const r = await createChapter(bookId, {
      number: nextNumber,
      title: newChapterTitle.trim(),
      outline: newChapterOutline.trim(),
    })
    setBook(prev => prev ? { ...prev, chapters: [...(prev.chapters ?? []), r.data] } : prev)
    setNewChapterTitle('')
    setNewChapterOutline('')
    setAddingChapter(false)
    setActiveChapter(r.data)
  }

  const isPhaseComplete = (phase: string) => {
    switch (phase) {
      case 'storybible':  return book?.storyBibleStatus  === 'Complete'
      case 'characters':  return book?.charactersStatus  === 'Complete'
      case 'plotthreads': return book?.plotThreadsStatus === 'Complete'
      case 'chapters':    return book?.chaptersStatus    === 'Complete'
      default: return false
    }
  }

  const statusColor = (s: string) => ({
    Outlined: '#94a3b8', Writing: '#f59e0b', Review: '#3b82f6',
    Editing: '#a855f7', Done: '#22c55e'
  })[s] ?? '#94a3b8'

  // Beautified planning preview
  const planningChapters = parsePlanningStream(plannerBuffer)

  if (!book) return <div className="loading">Loading…</div>

  return (
    <div className="book-detail">
      {/* Mobile tab bar (hidden on desktop via CSS) */}
      <nav className="mobile-nav-tabs">
        <button
          className={`mobile-nav-tab${mobilePanel === 'sidebar' ? ' active' : ''}`}
          onClick={() => setMobilePanel('sidebar')}
        >≡ Nav</button>
        <button
          className={`mobile-nav-tab${mobilePanel === 'content' ? ' active' : ''}`}
          onClick={() => setMobilePanel('content')}
        >📖 Book</button>
        <button
          className={`mobile-nav-tab${mobilePanel === 'chat' ? ' active' : ''}`}
          onClick={() => setMobilePanel('chat')}
        >{pendingQuestion ? '💬 Chat ●' : '💬 Chat'}</button>
      </nav>

      {/* Sidebar */}
      <aside className={`sidebar${mobilePanel === 'sidebar' ? ' mobile-panel-active' : ''}`}>
        <Link to="/" className="back-link">← Books</Link>
        <h2>{book.title}</h2>
        <p className="genre">{book.genre} · {book.language}</p>

        {isRunning && (
          <div className="agent-running-banner">
            <span className="spinner" /> {runStatus?.role} is {runStatus?.state === 'WaitingForInput' ? 'waiting for input…' : 'running…'}
          </div>
        )}

        <div className="agent-actions">
          {isRunning ? (
            <button className="btn-stop" onClick={handleStop}>⊙ Stop</button>
          ) : (
            <>
              <button className="btn-primary" onClick={handleWriteBook}>▶ Write Book</button>
              <button className="btn-plan" onClick={handlePlanBook}>🗂 Plan Only</button>
              {(book.chapters ?? []).length > 0 && (
                <button className="btn-continue" onClick={handleContinue}>↻ Continue</button>
              )}
              {(book.storyBibleStatus === 'Complete' || book.charactersStatus === 'Complete' || book.plotThreadsStatus === 'Complete' || book.chaptersStatus === 'Complete') && (
                <button className="btn-continue-plan" onClick={handleContinuePlanning}>⏩ Continue Planning</button>
              )}
            </>
          )}

        </div>

        {workflowLog.length > 0 && (
          <div className="workflow-log">
            <strong>Workflow</strong>
            <ul>
              {workflowLog.map((step, i) => (
                <li key={i} className="workflow-log-step">{step}</li>
              ))}
            </ul>
            <div ref={workflowLogEndRef} />
          </div>
        )}
        {(book.chapters ?? []).length > 0 && (
          <div className="phase-actions phase-actions-chapters">
            {isPhaseComplete('chapters') ? (
              <>
                <span className="phase-status-badge phase-complete">✅ Chapters</span>
                <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('chapters')}>↺ Reopen</button>
                <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('chapters', () => { setBook(prev => prev ? { ...prev, chapters: [] } : prev); setActiveChapter(null) })}>🗑 Clear</button>
              </>
            ) : (
              <>
                <span className="phase-status-badge phase-not-started">⬜ Chapters</span>
                <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('chapters')}>✓ Complete</button>
                <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('chapters', () => { setBook(prev => prev ? { ...prev, chapters: [] } : prev); setActiveChapter(null) })}>🗑 Clear</button>
              </>
            )}
          </div>
        )}
        <ul className="chapter-list">
          {(book.chapters ?? []).map(c => (
            <li
              key={c.id}
              className={activeChapter?.id === c.id ? 'active' : ''}
              onClick={() => { setActiveChapter(c); setEditingChapter(false) }}
            >
              <span className="ch-num">{c.number}.</span>
              <span className="ch-title">{c.title || 'Untitled'}</span>
              <span className="ch-dot" style={{ background: statusColor(c.status) }} />
            </li>
          ))}
          {(book.chapters ?? []).length === 0 && (
            <li className="empty-chapters">Run Planner to generate chapters</li>
          )}
        </ul>
        {!isRunning && (
          addingChapter ? (
            <div className="add-chapter-form">
              <input
                placeholder="Chapter title"
                value={newChapterTitle}
                onChange={e => setNewChapterTitle(e.target.value)}
                autoFocus
              />
              <textarea
                rows={2}
                placeholder="Brief outline (optional)"
                value={newChapterOutline}
                onChange={e => setNewChapterOutline(e.target.value)}
              />
              <div className="add-chapter-actions">
                <button className="btn-sm" onClick={handleAddChapter} disabled={!newChapterTitle.trim()}>Add</button>
                <button className="btn-sm btn-ghost" onClick={() => { setAddingChapter(false); setNewChapterTitle(''); setNewChapterOutline('') }}>Cancel</button>
              </div>
            </div>
          ) : (
            <button className="btn-add-chapter" onClick={() => setAddingChapter(true)}>+ Chapter</button>
          )
        )}
        {(book.chapters ?? []).some(c => c.content?.trim()) && (
          <button
            className="download-html-btn"
            onClick={() => downloadBookAsHtml(book)}
            title="Download full book as a self-contained HTML file"
          >
            ⬇ Download HTML
          </button>
        )}
        <button
          className="download-html-btn"
          onClick={() => downloadBookMetadataAsHtml(book, storyBible, characters, plotThreads, messages, tokenStats)}
          title="Download book metadata (outlines, characters, plot threads, agent messages) as HTML"
        >
          ⬇ Download Metadata
        </button>
        <Link to={`/books/${bookId}/settings`} className="settings-link">⚙ Settings</Link>
      </aside>

      {/* Main content */}
      <main className={`content${mobilePanel === 'content' ? ' mobile-panel-active' : ''}`}>
        {activeChapter ? (
          <div className="chapter-view">
            {editingChapter ? (
              <div className="chapter-edit-form">
                <label>
                  Title
                  <input value={chapterEditTitle} onChange={e => setChapterEditTitle(e.target.value)} />
                </label>
                <label>
                  Outline
                  <textarea rows={4} value={chapterEditOutline} onChange={e => setChapterEditOutline(e.target.value)} />
                </label>
                <div className="chapter-edit-actions">
                  <button onClick={handleSaveChapterEdit}>Save</button>
                  <button className="btn-ghost" onClick={() => setEditingChapter(false)}>Cancel</button>
                </div>
              </div>
            ) : (
              <>
                <div className="chapter-header">
                  <button className="btn-back-overview" onClick={() => { setActiveChapter(null); setEditingChapter(false) }} title="Back to book overview">← Overview</button>
                  <h2>Chapter {activeChapter.number}: {activeChapter.title}</h2>
                  <span className="ch-status-badge" style={{ background: statusColor(activeChapter.status) }}>{activeChapter.status}</span>
                  {!isRunning && (
                    <button
                      className="btn-edit-chapter"
                      onClick={() => {
                        setChapterEditTitle(activeChapter.title)
                        setChapterEditOutline(activeChapter.outline ?? '')
                        setEditingChapter(true)
                      }}
                      title="Edit chapter title and outline"
                    >✎ Edit</button>
                  )}
                  {activeChapter.content?.trim() && (
                    <button
                      className="btn-clear-chapter"
                      disabled={isRunning}
                      onClick={() => handleClearChapter(activeChapter)}
                      title="Clear chapter content and reset status to Outlined"
                    >↺ Clear</button>
                  )}
                </div>
                {activeChapter.outline && (
                  <div className="outline">
                    <strong>Outline:</strong> {activeChapter.outline}
                  </div>
                )}
                {(activeChapter.povCharacter || activeChapter.foreshadowingNotes || activeChapter.payoffNotes) && (
                  <div className="chapter-meta-fields">
                    {activeChapter.povCharacter && <span className="ch-meta-item"><strong>POV:</strong> {activeChapter.povCharacter}</span>}
                    {activeChapter.foreshadowingNotes && <span className="ch-meta-item"><strong>Foreshadowing:</strong> {activeChapter.foreshadowingNotes}</span>}
                    {activeChapter.payoffNotes && <span className="ch-meta-item"><strong>Payoff:</strong> {activeChapter.payoffNotes}</span>}
                  </div>
                )}
                <div className="chapter-content">
                  {streamBuffer && streamingChapterId === activeChapter.id ? (
                    <ReactMarkdown>{streamBuffer}</ReactMarkdown>
                  ) : activeChapter.content ? (
                    <ReactMarkdown>{activeChapter.content}</ReactMarkdown>
                  ) : (
                    <p className="empty">Waiting to be written by the agents…</p>
                  )}
                </div>
              </>
            )}
          </div>
        ) : (
          <div className="book-overview">
            {/* Tab navigation */}
            <div className="overview-tabs">
              {(['overview', 'storybible', 'characters', 'plotthreads'] as const).map(tab => (
                <button
                  key={tab}
                  className={`overview-tab${activeTab === tab ? ' active' : ''}`}
                  onClick={() => setActiveTab(tab)}
                >
                  {{ overview: '📖 Overview', storybible: '🌍 Story Bible', characters: '👤 Characters', plotthreads: '🧵 Plot Threads' }[tab]}
                  {tab !== 'overview' && isPhaseComplete(tab) && <span className="phase-dot-complete" title="Complete"> ●</span>}
                </button>
              ))}
            </div>

            {/* Overview tab */}
            {activeTab === 'overview' && (
              editingBook ? (
                <div className="book-edit-form">
                  <label>Title<input value={bookEditTitle} onChange={e => setBookEditTitle(e.target.value)} /></label>
                  <label>Genre<input value={bookEditGenre} onChange={e => setBookEditGenre(e.target.value)} /></label>
                  <label>Target chapters<input type="number" min={1} value={bookEditTargetChapters} onChange={e => setBookEditTargetChapters(+e.target.value)} /></label>
                  <label>Premise / Plot<textarea rows={6} value={bookEditPremise} onChange={e => setBookEditPremise(e.target.value)} /></label>
                  <div className="book-edit-actions">
                    <button onClick={handleSaveBookEdit}>Save</button>
                    <button className="btn-ghost" onClick={() => setEditingBook(false)}>Cancel</button>
                  </div>
                </div>
              ) : (
                <>
                  <div className="book-overview-header">
                    <h2>{book.title}</h2>
                    {!isRunning && (
                      <button className="btn-edit-book" onClick={() => { setBookEditTitle(book.title); setBookEditPremise(book.premise); setBookEditGenre(book.genre); setBookEditTargetChapters(book.targetChapterCount); setEditingBook(true) }} title="Edit book details">✎ Edit</button>
                    )}
                  </div>
                  <p><strong>Premise:</strong> {book.premise}</p>
                  <p><strong>Genre:</strong> {book.genre} · <strong>Language:</strong> {book.language} · <strong>Target chapters:</strong> {book.targetChapterCount}</p>

                  {isRunning && runStatus?.role === 'Planner' && plannerBuffer && (
                    <div className="planning-preview">
                      <h3>Planning in progress…</h3>
                      {planningChapters.length > 0 ? (
                        <div className="plan-chapters">
                          {planningChapters.map(c => (
                            <div key={c.number} className="plan-chapter-card">
                              <span className="plan-ch-num">Ch. {c.number}</span>
                              <div><strong>{c.title}</strong><p>{c.outline}</p></div>
                            </div>
                          ))}
                          <p className="plan-partial-hint">Building chapter plan…</p>
                        </div>
                      ) : (
                        <div className="stream-raw"><span className="spinner" /> Generating outlines…</div>
                      )}
                    </div>
                  )}
                </>
              )
            )}

            {/* Story Bible tab */}
            {activeTab === 'storybible' && (
              editingBible ? (
                <div className="bible-edit-form">
                  <h3>Story Bible</h3>
                  {(['settingDescription', 'timePeriod', 'themes', 'toneAndStyle', 'worldRules', 'notes'] as const).map(field => (
                    <label key={field}>
                      {field.replace(/([A-Z])/g, ' $1').replace(/^./, s => s.toUpperCase())}
                      <textarea rows={3} value={(bibleForm[field] as string) ?? ''} onChange={e => setBibleForm(f => ({ ...f, [field]: e.target.value }))} />
                    </label>
                  ))}
                  <div className="bible-edit-actions">
                    <button onClick={async () => {
                      const updated = { ...(storyBible ?? {}), ...bibleForm, bookId } as StoryBible
                      const r = await updateStoryBible(bookId, updated)
                      setStoryBible(r.data)
                      setEditingBible(false)
                    }}>Save</button>
                    <button className="btn-ghost" onClick={() => setEditingBible(false)}>Cancel</button>
                  </div>
                </div>
              ) : (
                <div className="bible-view">
                  <div className="bible-view-header">
                    <h3>Story Bible</h3>
                    <button className="btn-edit-book" onClick={() => { setBibleForm(storyBible ?? {}); setEditingBible(true) }}>✎ Edit</button>
                  </div>
                  <div className="phase-actions">
                    {isPhaseComplete('storybible') ? (
                      <>
                        <span className="phase-status-badge phase-complete">✅ Complete</span>
                        <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('storybible')}>↺ Reopen</button>
                      </>
                    ) : (
                      <>
                        <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
                        <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('storybible')}>✓ Complete</button>
                      </>
                    )}
                    <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('storybible', () => setStoryBible(null))}>🗑 Clear</button>
                  </div>
                  {storyBible?.settingDescription && <p><strong>Setting:</strong> {storyBible.settingDescription}</p>}
                  {storyBible?.timePeriod && <p><strong>Time Period:</strong> {storyBible.timePeriod}</p>}
                  {storyBible?.themes && <p><strong>Themes:</strong> {storyBible.themes}</p>}
                  {storyBible?.toneAndStyle && <p><strong>Tone & Style:</strong> {storyBible.toneAndStyle}</p>}
                  {storyBible?.worldRules && <><strong>World Rules:</strong><pre className="bible-pre">{storyBible.worldRules}</pre></>}
                  {storyBible?.notes && <><strong>Notes:</strong><pre className="bible-pre">{storyBible.notes}</pre></>}
                  {storyBibleStream && (() => {
                    const preview = parseStoryBibleStream(storyBibleStream)
                    return Object.keys(preview).length > 0 ? (
                      <div className="stream-preview">
                        <div className="stream-preview-label">⏳ Generating Story Bible…</div>
                        {preview.settingDescription && <p><strong>Setting:</strong> {preview.settingDescription}</p>}
                        {preview.timePeriod && <p><strong>Time Period:</strong> {preview.timePeriod}</p>}
                        {preview.themes && <p><strong>Themes:</strong> {preview.themes}</p>}
                        {preview.toneAndStyle && <p><strong>Tone & Style:</strong> {preview.toneAndStyle}</p>}
                        {preview.worldRules && <><strong>World Rules:</strong><pre className="bible-pre">{preview.worldRules}</pre></>}
                        {preview.notes && <><strong>Notes:</strong><pre className="bible-pre">{preview.notes}</pre></>}
                      </div>
                    ) : <div className="stream-preview"><div className="stream-preview-label">⏳ Generating Story Bible…</div></div>
                  })()}
                  {!storyBible?.settingDescription && !storyBible?.timePeriod && !storyBible?.themes && !storyBibleStream && (
                    <p className="empty">No Story Bible yet. Run the Planner or edit manually.</p>
                  )}
                </div>
              )
            )}

            {/* Characters tab */}
            {activeTab === 'characters' && (
              <div className="characters-view">
                <div className="characters-header">
                  <h3>Characters ({characters.length})</h3>
                  <button className="btn-sm" onClick={() => { setCharForm({}); setAddingChar(true); setEditingCharId(null) }}>+ Add</button>
                </div>
                <div className="phase-actions">
                  {isPhaseComplete('characters') ? (
                    <>
                      <span className="phase-status-badge phase-complete">✅ Complete</span>
                      <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('characters')}>↺ Reopen</button>
                    </>
                  ) : (
                    <>
                      <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
                      <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('characters')}>✓ Complete</button>
                    </>
                  )}
                  <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('characters', () => setCharacters([]))}>🗑 Clear</button>
                </div>
                {addingChar && (
                  <div className="char-edit-form">
                    <label>Name<input autoFocus value={charForm.name ?? ''} onChange={e => setCharForm(f => ({ ...f, name: e.target.value }))} /></label>
                    <label>Role
                      <select value={charForm.role ?? 'Supporting'} onChange={e => setCharForm(f => ({ ...f, role: e.target.value }))}>
                        {['Protagonist','Antagonist','Supporting','Minor'].map(r => <option key={r}>{r}</option>)}
                      </select>
                    </label>
                    <label>Physical Description<textarea rows={2} value={charForm.physicalDescription ?? ''} onChange={e => setCharForm(f => ({ ...f, physicalDescription: e.target.value }))} /></label>
                    <label>Personality<textarea rows={2} value={charForm.personality ?? ''} onChange={e => setCharForm(f => ({ ...f, personality: e.target.value }))} /></label>
                    <label>Backstory<textarea rows={2} value={charForm.backstory ?? ''} onChange={e => setCharForm(f => ({ ...f, backstory: e.target.value }))} /></label>
                    <label>Goal / Motivation<textarea rows={2} value={charForm.goalMotivation ?? ''} onChange={e => setCharForm(f => ({ ...f, goalMotivation: e.target.value }))} /></label>
                    <label>Arc<textarea rows={2} value={charForm.arc ?? ''} onChange={e => setCharForm(f => ({ ...f, arc: e.target.value }))} /></label>
                    <label>Notes<textarea rows={2} value={charForm.notes ?? ''} onChange={e => setCharForm(f => ({ ...f, notes: e.target.value }))} /></label>
                    <div className="char-edit-actions">
                      <button className="btn-sm" onClick={async () => {
                        if (!charForm.name?.trim()) return
                        const r = await createCharacter(bookId, charForm as CharacterCard)
                        setCharacters(prev => [...prev, r.data])
                        setAddingChar(false)
                        setCharForm({})
                      }}>Save</button>
                      <button className="btn-sm btn-ghost" onClick={() => { setAddingChar(false); setCharForm({}) }}>Cancel</button>
                    </div>
                  </div>
                )}
                {charactersStream && (() => {
                  const preview = parseCharactersStream(charactersStream)
                  return (
                    <div className="stream-preview">
                      <div className="stream-preview-label">⏳ Generating Characters… ({preview.length} so far)</div>
                      {preview.map((c, i) => (
                        <div key={i} className="char-card">
                          <div className="char-card-header">
                            <strong>{c.name}</strong>
                            {c.role && <span className={`char-role-badge role-${(c.role as string).toLowerCase()}`}>{c.role as string}</span>}
                          </div>
                          {c.goalMotivation && <p className="char-field"><em>Goal:</em> {c.goalMotivation}</p>}
                        </div>
                      ))}
                    </div>
                  )
                })()}
                {characters.length === 0 && !addingChar && !charactersStream && <p className="empty">No characters yet. Run the Planner or add manually.</p>}
                {characters.map(ch => (
                  <div key={ch.id} className="char-card">
                    {editingCharId === ch.id ? (
                      <div className="char-edit-form">
                        <label>Name<input autoFocus value={charForm.name ?? ch.name} onChange={e => setCharForm(f => ({ ...f, name: e.target.value }))} /></label>
                        <label>Role
                          <select value={charForm.role ?? ch.role} onChange={e => setCharForm(f => ({ ...f, role: e.target.value }))}>
                            {['Protagonist','Antagonist','Supporting','Minor'].map(r => <option key={r}>{r}</option>)}
                          </select>
                        </label>
                        <label>Physical Description<textarea rows={2} value={charForm.physicalDescription ?? ch.physicalDescription ?? ''} onChange={e => setCharForm(f => ({ ...f, physicalDescription: e.target.value }))} /></label>
                        <label>Personality<textarea rows={2} value={charForm.personality ?? ch.personality ?? ''} onChange={e => setCharForm(f => ({ ...f, personality: e.target.value }))} /></label>
                        <label>Backstory<textarea rows={2} value={charForm.backstory ?? ch.backstory ?? ''} onChange={e => setCharForm(f => ({ ...f, backstory: e.target.value }))} /></label>
                        <label>Goal / Motivation<textarea rows={2} value={charForm.goalMotivation ?? ch.goalMotivation ?? ''} onChange={e => setCharForm(f => ({ ...f, goalMotivation: e.target.value }))} /></label>
                        <label>Arc<textarea rows={2} value={charForm.arc ?? ch.arc ?? ''} onChange={e => setCharForm(f => ({ ...f, arc: e.target.value }))} /></label>
                        <label>Notes<textarea rows={2} value={charForm.notes ?? ch.notes ?? ''} onChange={e => setCharForm(f => ({ ...f, notes: e.target.value }))} /></label>
                        <div className="char-edit-actions">
                          <button className="btn-sm" onClick={async () => {
                            const r = await updateCharacter(bookId, ch.id, { ...ch, ...charForm } as CharacterCard)
                            setCharacters(prev => prev.map(c => c.id === ch.id ? r.data : c))
                            setEditingCharId(null)
                            setCharForm({})
                          }}>Save</button>
                          <button className="btn-sm btn-ghost" onClick={() => { setEditingCharId(null); setCharForm({}) }}>Cancel</button>
                        </div>
                      </div>
                    ) : (
                      <>
                        <div className="char-card-header">
                          <strong>{ch.name}</strong>
                          <span className={`char-role-badge role-${ch.role?.toLowerCase()}`}>{ch.role}</span>
                          <div className="char-card-actions">
                            <button className="btn-icon" title="Edit" onClick={() => { setCharForm({}); setEditingCharId(ch.id); setAddingChar(false) }}>✎</button>
                            <button className="btn-icon btn-danger" title="Delete" onClick={async () => {
                              if (!confirm(`Delete character "${ch.name}"?`)) return
                              await deleteCharacter(bookId, ch.id)
                              setCharacters(prev => prev.filter(c => c.id !== ch.id))
                            }}>✕</button>
                          </div>
                        </div>
                        {ch.physicalDescription && <p className="char-field"><em>Appearance:</em> {ch.physicalDescription}</p>}
                        {ch.personality && <p className="char-field"><em>Personality:</em> {ch.personality}</p>}
                        {ch.goalMotivation && <p className="char-field"><em>Goal:</em> {ch.goalMotivation}</p>}
                        {ch.arc && <p className="char-field"><em>Arc:</em> {ch.arc}</p>}
                      </>
                    )}
                  </div>
                ))}
              </div>
            )}

            {/* Plot Threads tab */}
            {activeTab === 'plotthreads' && (
              <div className="plotthreads-view">
                <div className="plotthreads-header">
                  <h3>Plot Threads ({plotThreads.length})</h3>
                  <button className="btn-sm" onClick={() => { setThreadForm({}); setAddingThread(true); setEditingThreadId(null) }}>+ Add</button>
                </div>
                <div className="phase-actions">
                  {isPhaseComplete('plotthreads') ? (
                    <>
                      <span className="phase-status-badge phase-complete">✅ Complete</span>
                      <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('plotthreads')}>↺ Reopen</button>
                    </>
                  ) : (
                    <>
                      <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
                      <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('plotthreads')}>✓ Complete</button>
                    </>
                  )}
                  <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('plotthreads', () => setPlotThreads([]))}>🗑 Clear</button>
                </div>
                {addingThread && (
                  <div className="thread-edit-form">
                    <label>Name<input autoFocus value={threadForm.name ?? ''} onChange={e => setThreadForm(f => ({ ...f, name: e.target.value }))} /></label>
                    <label>Description<textarea rows={3} value={threadForm.description ?? ''} onChange={e => setThreadForm(f => ({ ...f, description: e.target.value }))} /></label>
                    <label>Type
                      <select value={threadForm.type ?? 'MainPlot'} onChange={e => setThreadForm(f => ({ ...f, type: e.target.value }))}>
                        {['MainPlot','Subplot','CharacterArc','Mystery','Foreshadowing','WorldBuilding','ThematicThread'].map(t => <option key={t}>{t}</option>)}
                      </select>
                    </label>
                    <label>Status
                      <select value={threadForm.status ?? 'Active'} onChange={e => setThreadForm(f => ({ ...f, status: e.target.value }))}>
                        {['Active','Resolved','Dormant'].map(s => <option key={s}>{s}</option>)}
                      </select>
                    </label>
                    <label>Introduced Ch.<input type="number" value={threadForm.introducedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, introducedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
                    <label>Resolved Ch.<input type="number" value={threadForm.resolvedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
                    <div className="thread-edit-actions">
                      <button className="btn-sm" onClick={async () => {
                        if (!threadForm.name?.trim()) return
                        const r = await createPlotThread(bookId, threadForm as PlotThread)
                        setPlotThreads(prev => [...prev, r.data])
                        setAddingThread(false)
                        setThreadForm({})
                      }}>Save</button>
                      <button className="btn-sm btn-ghost" onClick={() => { setAddingThread(false); setThreadForm({}) }}>Cancel</button>
                    </div>
                  </div>
                )}
                {plotThreadsStream && (() => {
                  const preview = parsePlotThreadsStream(plotThreadsStream)
                  return (
                    <div className="stream-preview">
                      <div className="stream-preview-label">⏳ Generating Plot Threads… ({preview.length} so far)</div>
                      {preview.map((t, i) => (
                        <div key={i} className={`thread-card status-${(t.status as string | undefined)?.toLowerCase() ?? 'active'}`}>
                          <div className="thread-card-header">
                            <strong>{t.name}</strong>
                            {t.type && <span className="thread-type-badge">{t.type as string}</span>}
                          </div>
                          {t.description && <p className="thread-desc">{t.description}</p>}
                        </div>
                      ))}
                    </div>
                  )
                })()}
                {plotThreads.length === 0 && !addingThread && !plotThreadsStream && <p className="empty">No plot threads yet. Run the Planner or add manually.</p>}
                {plotThreads.map(t => (
                  <div key={t.id} className={`thread-card status-${t.status?.toLowerCase()}`}>
                    {editingThreadId === t.id ? (
                      <div className="thread-edit-form">
                        <label>Name<input autoFocus value={threadForm.name ?? t.name} onChange={e => setThreadForm(f => ({ ...f, name: e.target.value }))} /></label>
                        <label>Description<textarea rows={3} value={threadForm.description ?? t.description ?? ''} onChange={e => setThreadForm(f => ({ ...f, description: e.target.value }))} /></label>
                        <label>Type
                          <select value={threadForm.type ?? t.type} onChange={e => setThreadForm(f => ({ ...f, type: e.target.value }))}>
                            {['MainPlot','Subplot','CharacterArc','Mystery','Foreshadowing','WorldBuilding','ThematicThread'].map(tt => <option key={tt}>{tt}</option>)}
                          </select>
                        </label>
                        <label>Status
                          <select value={threadForm.status ?? t.status} onChange={e => setThreadForm(f => ({ ...f, status: e.target.value }))}>
                            {['Active','Resolved','Dormant'].map(s => <option key={s}>{s}</option>)}
                          </select>
                        </label>
                        <label>Introduced Ch.<input type="number" value={threadForm.introducedChapterNumber ?? t.introducedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, introducedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
                        <label>Resolved Ch.<input type="number" value={threadForm.resolvedChapterNumber ?? t.resolvedChapterNumber ?? ''} onChange={e => setThreadForm(f => ({ ...f, resolvedChapterNumber: e.target.value ? +e.target.value : undefined }))} /></label>
                        <div className="thread-edit-actions">
                          <button className="btn-sm" onClick={async () => {
                            const r = await updatePlotThread(bookId, t.id, { ...t, ...threadForm } as PlotThread)
                            setPlotThreads(prev => prev.map(x => x.id === t.id ? r.data : x))
                            setEditingThreadId(null)
                            setThreadForm({})
                          }}>Save</button>
                          <button className="btn-sm btn-ghost" onClick={() => { setEditingThreadId(null); setThreadForm({}) }}>Cancel</button>
                        </div>
                      </div>
                    ) : (
                      <>
                        <div className="thread-card-header">
                          <strong>{t.name}</strong>
                          <span className={`thread-type-badge`}>{t.type}</span>
                          <span className={`thread-status-badge status-${t.status?.toLowerCase()}`}>{t.status}</span>
                          <div className="thread-card-actions">
                            <button className="btn-icon" onClick={() => { setThreadForm({}); setEditingThreadId(t.id); setAddingThread(false) }}>✎</button>
                            <button className="btn-icon btn-danger" onClick={async () => {
                              if (!confirm(`Delete plot thread "${t.name}"?`)) return
                              await deletePlotThread(bookId, t.id)
                              setPlotThreads(prev => prev.filter(x => x.id !== t.id))
                            }}>✕</button>
                          </div>
                        </div>
                        {t.description && <p className="thread-desc">{t.description}</p>}
                        <div className="thread-meta">
                          {t.introducedChapterNumber != null && <span>Intro: Ch.{t.introducedChapterNumber}</span>}
                          {t.resolvedChapterNumber != null && <span>Resolved: Ch.{t.resolvedChapterNumber}</span>}
                        </div>
                      </>
                    )}
                  </div>
                ))}
              </div>
            )}
          </div>
        )}
      </main>

      {/* Chat panel */}
      <aside className={`chat-panel${mobilePanel === 'chat' ? ' mobile-panel-active' : ''}`}>
        <div className="chat-panel-header">
          <h3>Agent Messages</h3>
          {messages.length > 0 && !isRunning && (
            <button className="btn-sm btn-ghost" title="Clear all messages" onClick={async () => {
              if (!confirm('Clear all agent messages for this book?')) return
              await clearMessages(bookId)
              setMessages([])
            }}>🗑 Clear</button>
          )}
        </div>
        <div className="chat-messages">
          {messages.map(m => (
            <div key={m.id} className={`chat-msg msg-${m.messageType.toLowerCase()}`}>
              <span className="msg-role">{m.agentRole}</span>
              <span className="msg-type">[{m.messageType}]</span>
              <div className="msg-content">
                {m.messageType === 'SystemNote' || m.messageType === 'Feedback'
                  ? <ReactMarkdown>{m.content}</ReactMarkdown>
                  : m.content}
              </div>
            </div>
          ))}
          <div ref={chatBottomRef} />
        </div>
        {pendingQuestion && (
          <div className="answer-form">
            <p className="question-label">Agent is waiting for your answer:</p>
            <p className="question-text">{pendingQuestion.content}</p>
            <textarea
              rows={3}
              value={answerText}
              onChange={e => setAnswerText(e.target.value)}
              placeholder="Type your answer…"
            />
            <button onClick={handleAnswer}>Send Answer</button>
          </div>
        )}
        {tokenStats.length > 0 && (
          <details className="token-stats-panel">
            <summary>
              Token stats ({tokenStats.length} calls)
              {!isRunning && (
                <button className="btn-sm btn-ghost" style={{ marginLeft: '0.5rem' }} title="Clear token stats" onClick={async e => {
                  e.preventDefault()
                  if (!confirm('Clear all token usage stats for this book?')) return
                  await clearTokenUsage(bookId)
                  setTokenStats([])
                }}>🗑 Clear</button>
              )}
            </summary>
            <div className="token-stats-list">
              <table>
                <thead>
                  <tr><th>Time</th><th>Agent</th><th>Prompt</th><th>Completion</th></tr>
                </thead>
                <tbody>
                  {tokenStats.map(s => (
                    <tr key={s.id}>
                      <td>{s.time}</td>
                      <td>{s.role}</td>
                      <td>{s.prompt.toLocaleString()}</td>
                      <td>{s.completion.toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
              <div className="token-stats-total">
                Total: {tokenStats.reduce((a, s) => a + s.prompt + s.completion, 0).toLocaleString()} tokens
              </div>
            </div>
          </details>
        )}
      </aside>
    </div>
  )
}
