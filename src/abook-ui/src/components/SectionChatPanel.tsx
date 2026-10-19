import { useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../contexts/BookContext'
import { sectionLabel, visibleReply } from '../utils/sectionChat'

/**
 * Chat with the LLM about the section a human-assisted run is paused on. Every message is answered
 * with the whole conversation as context; when the author asks for a change the LLM rewrites the
 * section, which is saved at once and shows on the page. "Complete" resumes the run.
 */
export default function SectionChatPanel() {
  const {
    book, pendingQuestion, sectionTarget, sectionChat, sectionChatStream, sectionChatError,
    sendSectionChatMessage, handleAnswer, submittingAnswer, answerError,
  } = useBookContext()

  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const [collapsed, setCollapsed] = useState(false)
  const bottomRef = useRef<HTMLDivElement>(null)

  const messages = sectionChat?.messages ?? []
  const busy = sending || !!sectionChat?.busy
  const streamed = visibleReply(sectionChatStream)

  useEffect(() => {
    bottomRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' })
  }, [messages.length, streamed, busy])

  if (!book || !pendingQuestion || !sectionTarget) return null

  const chapterNumber = sectionTarget.chapterId
    ? book.chapters?.find(c => c.id === sectionTarget.chapterId)?.number
    : null
  const label = sectionLabel(sectionTarget, chapterNumber)

  const send = async () => {
    if (!text.trim() || busy) return
    setSending(true)
    try {
      if (await sendSectionChatMessage(text)) setText('')
    } finally {
      setSending(false)
    }
  }

  return (
    <aside className={`section-chat${collapsed ? ' collapsed' : ''}`} aria-label={`Chat about the ${label}`}>
      <div className="section-chat-header">
        <button className="section-chat-title btn-ghost" onClick={() => setCollapsed(v => !v)} title={collapsed ? 'Expand' : 'Collapse'}>
          💬 Discuss the {label} {collapsed ? '▴' : '▾'}
        </button>
        <button
          className="btn-sm"
          disabled={busy || submittingAnswer}
          title={busy ? 'Wait for the reply first' : 'Resume the run with the section as it is now'}
          onClick={() => handleAnswer('')}
        >
          {submittingAnswer ? 'Completing…' : '✓ Complete'}
        </button>
      </div>

      {!collapsed && (
        <>
          <div className="section-chat-messages">
            <p className="section-chat-intro">{pendingQuestion.content}</p>
            {messages.length === 0 && !busy && (
              <p className="section-chat-hint">
                Ask about the {label}, or say what to change — the LLM updates it on this page.
                You can also edit it yourself. Press Complete to continue.
              </p>
            )}
            {messages.map(m => (
              <div key={m.id} className={`section-chat-msg ${m.messageType === 'ChatUser' ? 'from-author' : 'from-llm'}`}>
                {m.messageType === 'ChatUser'
                  ? <p>{m.content}</p>
                  : <ReactMarkdown>{m.content}</ReactMarkdown>}
              </div>
            ))}
            {busy && (
              <div className="section-chat-msg from-llm">
                {streamed ? <ReactMarkdown>{streamed}</ReactMarkdown> : <p className="section-chat-typing"><span className="spinner" /> Thinking…</p>}
              </div>
            )}
            <div ref={bottomRef} />
          </div>

          <div className="section-chat-input">
            {(sectionChatError || answerError) && <p className="error-msg" role="alert">{sectionChatError ?? answerError}</p>}
            <textarea
              rows={3}
              value={text}
              onChange={e => setText(e.target.value)}
              onKeyDown={e => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); void send() } }}
              placeholder={`Message about the ${label}… (Ctrl+Enter to send)`}
            />
            <button onClick={() => void send()} disabled={busy || !text.trim()}>
              {busy ? 'Waiting for reply…' : 'Send'}
            </button>
          </div>
        </>
      )}
    </aside>
  )
}
