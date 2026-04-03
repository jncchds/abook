import { useEffect, useState, useRef, useCallback } from 'react'
import { useParams, Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import type { Book, Chapter, AgentMessage } from '../api'
import {
  getBook, getMessages, postAnswer,
  startPlanning, startWriting, startEditing, startContinuityCheck
} from '../api'
import { useBookHub } from '../hooks/useBookHub'

export default function BookDetail() {
  const { id } = useParams<{ id: string }>()
  const bookId = Number(id)

  const [book, setBook] = useState<Book | null>(null)
  const [messages, setMessages] = useState<AgentMessage[]>([])
  const [activeChapter, setActiveChapter] = useState<Chapter | null>(null)
  const [streamBuffer, setStreamBuffer] = useState('')
  const [answerText, setAnswerText] = useState('')
  const [pendingQuestion, setPendingQuestion] = useState<AgentMessage | null>(null)
  const chatBottomRef = useRef<HTMLDivElement>(null)

  const { setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated } = useBookHub(bookId)

  const refreshBook = useCallback(() =>
    getBook(bookId).then(r => setBook(r.data)), [bookId])

  const refreshMessages = useCallback(() =>
    getMessages(bookId).then(r => setMessages(r.data)), [bookId])

  useEffect(() => {
    refreshBook()
    refreshMessages()
  }, [refreshBook, refreshMessages])

  useEffect(() => {
    setOnStream((_bId, _cId, token) => setStreamBuffer(prev => prev + token))
    setOnQuestion((_bId, msg) => {
      const m = msg as AgentMessage
      setPendingQuestion(m)
      setMessages(prev => [...prev, m])
    })
    setOnStatus((_bId, _role, state) => {
      if (state === 'Done') {
        setStreamBuffer('')
        refreshBook()
        refreshMessages()
      }
    })
    setOnChapterUpdated(() => refreshBook())
  }, [setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, refreshBook, refreshMessages])

  useEffect(() => {
    chatBottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages, streamBuffer])

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

  if (!book) return <div className="loading">Loading…</div>

  return (
    <div className="book-detail">
      {/* Sidebar */}
      <aside className="sidebar">
        <Link to="/" className="back-link">← Books</Link>
        <h2>{book.title}</h2>
        <p className="genre">{book.genre}</p>
        <div className="agent-actions">
          <button onClick={() => startPlanning(bookId)}>▶ Plan</button>
          <button onClick={() => startContinuityCheck(bookId)}>⚖ Continuity</button>
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
                <button onClick={() => startWriting(bookId, activeChapter.id)}>✍ Write</button>
                <button onClick={() => startEditing(bookId, activeChapter.id)}>✏ Edit</button>
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
              {streamBuffer && (
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
            <p><strong>Genre:</strong> {book.genre} · <strong>Target chapters:</strong> {book.targetChapterCount}</p>
            {streamBuffer && (
              <div className="stream-preview">
                <pre>{streamBuffer}</pre>
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
