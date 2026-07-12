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
    <div className="chat-panel" style={{ flex: 1, height: '100%' }}>
      <div className="chat-panel-header">
        <h3>Agent Messages</h3>
        {messages.length > 0 && !isRunning && (
          <button className="btn-sm btn-ghost" title="Archive all messages" onClick={clearMessagesForBook}>🗄 Archive</button>
        )}
      </div>
      <div className="chat-messages">
        {messages.map(m => {
          const preview = m.content.replace(/\*\*|__|~~|`|\n+/g, ' ').replace(/\s+/g, ' ').trim().slice(0, 120)
          const isLong = m.content.length > 200
          const isMarkdown = m.messageType === 'SystemNote' || m.messageType === 'Feedback'
          return (
            <details key={m.id} className={`chat-msg msg-${m.messageType.toLowerCase()}`} open={!isLong}>
              <summary className="chat-msg-summary">
                <span className="msg-role">{m.agentRole}</span>
                <span className="msg-type">[{m.messageType}]</span>
                {isLong && <span className="msg-preview">{preview}{m.content.length > 120 ? '…' : ''}</span>}
              </summary>
              <div className="msg-content">
                {isMarkdown
                  ? <ReactMarkdown>{m.content}</ReactMarkdown>
                  : m.content}
              </div>
            </details>
          )
        })}
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
