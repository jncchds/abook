import { useEffect, useRef } from 'react'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../../contexts/BookContext'

export default function ChatPage() {
  const {
    messages, pendingQuestion, answerText, setAnswerText,
    handleAnswer, isRunning, clearMessagesForBook, submittingAnswer,
  } = useBookContext()

  const chatBottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    chatBottomRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  return (
    <div>
      <div className="page-header">
        <h2>Agent Messages</h2>
        {messages.length > 0 && !isRunning && (
          <button className="btn-sm btn-archive" title="Archive all messages" onClick={clearMessagesForBook}>🗄 Archive</button>
        )}
      </div>
      <div style={{ overflowY: 'auto' }}>
        {messages.length === 0 ? (
          <p className="empty" style={{ padding: '1rem 0.75rem' }}>No messages yet.</p>
        ) : (
          <div className="book-list">
            {messages.map(m => {
              const preview = m.content.replace(/\*\*|__|~~|`|\n+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 120)
              const isLong = m.content.length > 200
              const isMarkdown = m.messageType === 'SystemNote' || m.messageType === 'Feedback'
              return (
                <div key={m.id} className={`book-list-card msg-${m.messageType.toLowerCase()}`}>
                  <div className="book-list-card-left">
                    <div className="blc-meta">
                      <span className="msg-role">{m.agentRole}</span>
                      <span className="msg-type">[{m.messageType}]</span>
                    </div>
                    {isLong ? (
                      <>
                        <p className="blc-premise">{preview}{m.content.length > 120 ? '…' : ''}</p>
                        <details style={{ marginTop: '0.5rem' }}>
                          <summary style={{ cursor: 'pointer', color: 'var(--accent)', fontSize: '0.82rem' }}>Show full message</summary>
                          {isMarkdown
                            ? <ReactMarkdown>{m.content}</ReactMarkdown>
                            : m.content}
                        </details>
                      </>
                    ) : (
                      isMarkdown
                        ? <ReactMarkdown>{m.content}</ReactMarkdown>
                        : m.content
                    )}
                  </div>
                </div>
              )
            })}
          </div>
        )}
        <div ref={chatBottomRef} />
      </div>
      {pendingQuestion && (
        <div style={{ borderTop: '1px solid var(--border)', padding: '0.75rem', display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
          <p className="question-label">Agent is waiting for your answer:</p>
          <p className="question-text">{pendingQuestion.content}</p>
          <textarea
            rows={3}
            value={answerText}
            onChange={e => setAnswerText(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); handleAnswer() } }}
            placeholder={pendingQuestion.isOptional ? 'Optional — leave blank to skip…' : 'Type your answer…'}
          />
          <div style={{ display: 'flex', gap: '0.5rem' }}>
            <button onClick={handleAnswer} disabled={submittingAnswer}>
              {submittingAnswer ? 'Sending…' : 'Send Answer'}
            </button>
            {pendingQuestion.isOptional && (
              <button className="btn-sm btn-ghost" disabled={submittingAnswer} onClick={() => { setAnswerText(''); handleAnswer() }}>Skip</button>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
