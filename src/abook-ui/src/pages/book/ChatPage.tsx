import { useEffect, useRef } from 'react'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../../contexts/BookContext'

function splitContent(content: string): [string, string] {
  const dnIdx = content.indexOf('\n\n')
  const snIdx = content.indexOf('\n')
  const splitIdx = dnIdx >= 0 ? dnIdx : snIdx >= 0 ? snIdx : -1
  if (splitIdx < 0) return [content.trim(), '']
  return [content.slice(0, splitIdx).trim(), content.slice(splitIdx).trim()]
}

function cleanHeader(text: string): string {
  return text.replace(/^#{1,6}\s+/, '').replace(/\*\*/g, '').replace(/__/g, '').trim()
}

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
              const isMarkdown = m.messageType === 'SystemNote' || m.messageType === 'Feedback' || m.messageType === 'Question'
              const [rawHeader, body] = splitContent(m.content)
              const header = cleanHeader(rawHeader)
              const hasBody = body.length > 0
              return (
                <div key={m.id} className={`book-list-card msg-${m.messageType.toLowerCase()}`}>
                  <div className="book-list-card-left">
                    <div className="blc-meta">
                      <span className="msg-role">{m.agentRole}</span>
                      <span className="msg-type">[{m.messageType}]</span>
                    </div>
                    {hasBody ? (
                      <details>
                        <summary className="blc-premise" style={{ cursor: 'pointer', marginBottom: 0, color: 'var(--text-muted)' }}>{header}</summary>
                        <div style={{ marginTop: '0.5rem', fontSize: '0.875rem' }}>
                          {isMarkdown ? <ReactMarkdown>{body}</ReactMarkdown> : <p style={{ color: 'var(--text-muted)', lineHeight: 1.6 }}>{body}</p>}
                        </div>
                      </details>
                    ) : (
                      <p className="blc-premise">{header}</p>
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
