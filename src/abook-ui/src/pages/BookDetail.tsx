import { useEffect, useState, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import type { Book, Chapter, AgentMessage, AgentRunStatus } from '../api'
import {
  getBook, getMessages, postAnswer, getAgentStatus,
  startPlanning, startWriting, startEditing, startContinuityCheck
} from '../api'
import { useBookHub } from '../hooks/useBookHub'

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
  const [streamBuffer, setStreamBuffer] = useState('')
  const [streamingChapterId, setStreamingChapterId] = useState<number | null>(null)
  const [answerText, setAnswerText] = useState('')
  const [pendingQuestion, setPendingQuestion] = useState<AgentMessage | null>(null)
  const [runStatus, setRunStatus] = useState<AgentRunStatus | null>(null)
  const chatBottomRef = useRef<HTMLDivElement>(null)

  const { setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated } = useBookHub(bookId)

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
  }, [refreshBook, refreshMessages])

  useEffect(() => {
    setOnStream((_bId, cId, token) => {
      setStreamingChapterId(cId ?? null)
      setStreamBuffer(prev => prev + token)
    })
    setOnQuestion((_bId, msg) => {
      const m = msg as AgentMessage
      setPendingQuestion(m)
      setMessages(prev => [...prev, m])
    })
    setOnStatus((_bId, role, state) => {
      setRunStatus(state === 'Done' || state === 'Failed' ? null : { role, state, chapterId: undefined })
      if (state === 'Done' || state === 'Failed') {
        setStreamBuffer('')
        setStreamingChapterId(null)
        refreshBook()
        refreshMessages()
      }
    })
    setOnChapterUpdated(() => refreshBook())
  }, [setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, refreshBook, refreshMessages])

  useEffect(() => {
    chatBottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, streamBuffer])

  const isRunning = runStatus?.state === 'Running' || runStatus?.state === 'WaitingForInput'

  const handleAgentAction = async (action: () => Promise<unknown>) => {
    if (isRunning) return
    try {
      await action()
      setRunStatus({ role: 'Unknown', state: 'Running' })
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string }; status?: number } })
      if (msg?.response?.status === 409) {
        alert('An agent is already running for this book.')
      }
    }
  }

  const handleAnswer = async () => {
    if (!pendingQuestion || !answerText.trim()) return
    await postAnswer(bookId, pendingQuestion.id, answerText.trim())
    setAnswerText('')
    setPendingQuestion(null)
    refreshMessages()
  }

  const statusColor = (s: string) => ({
    Outlined: '#94a3b8', Writing: '#f59e0b', Review: '#3b82f6',
    Editing: '#a855f7', Done: '#22c55e'
  })[s] ?? '#94a3b8'

  // Beautified planning preview
  const planningChapters = parsePlanningStream(streamBuffer)

  if (!book) return <div className="loading">Loading…</div>

  return (
    <div className="book-detail">
      {/* Sidebar */}
      <aside className="sidebar">
        <Link to="/" className="back-link">← Books</Link>
        <h2>{book.title}</h2>
        <p className="genre">{book.genre} · {book.language}</p>

        {isRunning && (
          <div className="agent-running-banner">
            <span className="spinner" /> {runStatus?.role} is {runStatus?.state === 'WaitingForInput' ? 'waiting for input…' : 'running…'}
          </div>
        )}

        <div className="agent-actions">
          <button disabled={isRunning} onClick={() => handleAgentAction(() => startPlanning(bookId))}>
            ▶ Plan
          </button>
          <button disabled={isRunning} onClick={() => handleAgentAction(() => startContinuityCheck(bookId))}>
            ⚖ Continuity
          </button>
        </div>
        <ul className="chapter-list">
          {(book.chapters ?? []).map(c => (
            <li
              key={c.id}
              className={activeChapter?.id === c.id ? 'active' : ''}
              onClick={() => setActiveChapter(c)}
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
        <Link to={`/books/${bookId}/settings`} className="settings-link">⚙ Settings</Link>
      </aside>

      {/* Main content */}
      <main className="content">
        {activeChapter ? (
          <div className="chapter-view">
            <div className="chapter-header">
              <h2>Chapter {activeChapter.number}: {activeChapter.title}</h2>
              <div className="chapter-actions">
                <button disabled={isRunning} onClick={() => handleAgentAction(() => startWriting(bookId, activeChapter.id))}>✍ Write</button>
                <button disabled={isRunning} onClick={() => handleAgentAction(() => startEditing(bookId, activeChapter.id))}>✏ Edit</button>
              </div>
            </div>
            {activeChapter.outline && (
              <div className="outline">
                <strong>Outline:</strong> {activeChapter.outline}
              </div>
            )}
            <div className="chapter-content">
              {activeChapter.content ? (
                <ReactMarkdown>{activeChapter.content}</ReactMarkdown>
              ) : (
                <p className="empty">No content yet. Click Write to generate.</p>
              )}
              {streamBuffer && streamingChapterId === activeChapter.id && (
                <div className="stream-preview">
                  <ReactMarkdown>{streamBuffer}</ReactMarkdown>
                </div>
              )}
            </div>
          </div>
        ) : (
          <div className="book-overview">
            <h2>{book.title}</h2>
            <p><strong>Premise:</strong> {book.premise}</p>
            <p><strong>Genre:</strong> {book.genre} · <strong>Language:</strong> {book.language} · <strong>Target chapters:</strong> {book.targetChapterCount}</p>

            {isRunning && runStatus?.role === 'Planner' && streamBuffer && (
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
          </div>
        )}
      </main>

      {/* Chat panel */}
      <aside className="chat-panel">
        <h3>Agent Messages</h3>
        <div className="chat-messages">
          {messages.map(m => (
            <div key={m.id} className={`chat-msg msg-${m.messageType.toLowerCase()}`}>
              <span className="msg-role">{m.agentRole}</span>
              <span className="msg-type">[{m.messageType}]</span>
              <div className="msg-content">{m.content}</div>
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
      </aside>
    </div>
  )
}
