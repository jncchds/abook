import { useEffect, useRef } from 'react'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../../contexts/BookContext'

export default function ChatPage() {
  const {
    messages, pendingQuestion, answerText, setAnswerText,
    handleAnswer, isRunning, clearMessagesForBook,
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
            onKeyDown={e => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); handleAnswer() } }}
            placeholder={pendingQuestion.isOptional ? 'Optional — leave blank to skip…' : 'Type your answer…'}
          />
          <div style={{ display: 'flex', gap: '0.5rem' }}>
            <button onClick={handleAnswer}>Send Answer</button>
            {pendingQuestion.isOptional && (
              <button className="btn-sm btn-ghost" onClick={() => { setAnswerText(''); handleAnswer() }}>Skip</button>
            )}
          </div>
        </div>
      )}
    </div>
  )
}
