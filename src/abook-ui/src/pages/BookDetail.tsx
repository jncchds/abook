import { useEffect, useState, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import type { Book, Chapter, AgentMessage, AgentRunStatus } from '../api'
import {
  getBook, getMessages, postAnswer, getAgentStatus,
  startWorkflow, continueWorkflow, stopWorkflow, clearChapterContent,
  createChapter, updateChapter, updateBook, getTokenUsage
} from '../api'
import { useBookHub } from '../hooks/useBookHub'
import { downloadBookAsHtml } from '../utils/bookHtmlExport'

// Attempt to extract chapter plan entries from partial JSON
function parsePlanningStream(raw: string): { number: number; title: string; outline: string }[] {
  const results: { number: number; title: string; outline: string }[] = []
  // Match complete objects: { "number": N, "title": "...", "outline": "..." }
  const re = /\{\s*"number"\s*:\s*(\d+)\s*,\s*"title"\s*:\s*"([^"\\]*)"\s*,\s*"outline"\s*:\s*"([^"\\]*)"\s*\}/g
  let m: RegExpExecArray | null
  while ((m = re.exec(raw)) !== null) {
    results.push({ number: +m[1], title: m[2], outline: m[3] })
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

  const chatBottomRef = useRef<HTMLDivElement>(null)
  const workflowLogEndRef = useRef<HTMLDivElement>(null)

  const { setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError } = useBookHub(bookId)

  const refreshBook = useCallback(() =>
    getBook(bookId).then(r => setBook(r.data)), [bookId])

  const refreshMessages = useCallback(() =>
    getMessages(bookId).then(r => setMessages(r.data)), [bookId])

  // Poll agent status on mount to restore indicator if agent was running
  useEffect(() => {
    getAgentStatus(bookId)
      .then(r => setRunStatus(r.data))
      .catch(() => {})
  }, [bookId])

  useEffect(() => {
    refreshBook()
    refreshMessages()
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
  }, [refreshBook, refreshMessages, bookId])

  useEffect(() => {
    setOnStream((_bId, cId, token) => {
      if (cId === null) {
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
    })
    setOnWorkflowProgress((_bId, step, isComplete) => {
      setWorkflowLog(prev => [...prev, step])
      if (isComplete) {
        setRunStatus(null)
        setPlannerBuffer('')
        setStreamBuffer('')
        setStreamingChapterId(null)
        refreshBook()
        refreshMessages()
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
              {(book.chapters ?? []).length > 0 && (
                <button className="btn-continue" onClick={handleContinue}>↻ Continue</button>
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
            {editingBook ? (
              <div className="book-edit-form">
                <label>
                  Title
                  <input value={bookEditTitle} onChange={e => setBookEditTitle(e.target.value)} />
                </label>
                <label>
                  Genre
                  <input value={bookEditGenre} onChange={e => setBookEditGenre(e.target.value)} />
                </label>
                <label>
                  Target chapters
                  <input type="number" min={1} value={bookEditTargetChapters} onChange={e => setBookEditTargetChapters(+e.target.value)} />
                </label>
                <label>
                  Premise / Plot
                  <textarea rows={6} value={bookEditPremise} onChange={e => setBookEditPremise(e.target.value)} />
                </label>
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
                    <button
                      className="btn-edit-book"
                      onClick={() => {
                        setBookEditTitle(book.title)
                        setBookEditPremise(book.premise)
                        setBookEditGenre(book.genre)
                        setBookEditTargetChapters(book.targetChapterCount)
                        setEditingBook(true)
                      }}
                      title="Edit book details"
                    >✎ Edit</button>
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
                            <div>
                              <strong>{c.title}</strong>
                              <p>{c.outline}</p>
                            </div>
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
            )}
          </div>
        )}
      </main>

      {/* Chat panel */}
      <aside className={`chat-panel${mobilePanel === 'chat' ? ' mobile-panel-active' : ''}`}>
        <h3>Agent Messages</h3>
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
            <summary>Token stats ({tokenStats.length} calls)</summary>
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
