import { useEffect, useRef, useState } from 'react'
import ReactMarkdown from 'react-markdown'
import { useBookContext } from '../contexts/BookContext'
import type { SectionChatChange } from '../api'
import { sectionLabel, visibleReply } from '../utils/sectionChat'
import { foldContext, lineDiff } from '../utils/lineDiff'

/** What changed in one chat change, unchanged stretches folded. */
function ChangeDiff({ change }: { change: SectionChatChange }) {
  const lines = foldContext(lineDiff(change.before, change.after))
  return (
    <div className="section-chat-diff">
      {lines.map((l, i) => 'skipped' in l
        ? <div key={i} className="diff-skip">⋯ {l.skipped} unchanged line{l.skipped === 1 ? '' : 's'}</div>
        : <div key={i} className={`diff-${l.kind}`}>{l.kind === 'added' ? '+ ' : l.kind === 'removed' ? '− ' : '  '}{l.text || ' '}</div>)}
    </div>
  )
}

type Step = 'chat' | 'summarizing' | 'note'

/**
 * Chat with the LLM about the section a human-assisted run is paused on. Every message is answered
 * with the whole conversation as context; when the author asks for a change the LLM rewrites the
 * section, which is saved at once and shows on the page. Each change can be inspected as a diff and
 * the latest one undone. Complete drafts a note of the decisions, which the author can edit and save
 * as a clarification, then resumes the run.
 */
export default function SectionChatPanel() {
  const {
    book, pendingQuestion, sectionTarget, sectionChat, sectionChatStream, sectionChatError,
    sendSectionChatMessage, undoSectionChatChange, draftSectionChatSummary, completeSectionChat,
    submittingAnswer,
  } = useBookContext()

  const [text, setText] = useState('')
  const [sending, setSending] = useState(false)
  const [collapsed, setCollapsed] = useState(false)
  const [openDiffs, setOpenDiffs] = useState<Set<number>>(new Set())
  const [step, setStep] = useState<Step>('chat')
  const [note, setNote] = useState('')
  const bottomRef = useRef<HTMLDivElement>(null)

  const messages = sectionChat?.messages ?? []
  const changes = sectionChat?.changes ?? []
  const busy = sending || !!sectionChat?.busy
  const streamed = visibleReply(sectionChatStream)
  const latestChange = changes.length > 0 ? changes[changes.length - 1] : null

  useEffect(() => {
    if (step === 'chat') bottomRef.current?.scrollIntoView({ behavior: 'smooth', block: 'end' })
  }, [messages.length, streamed, busy, step])

  if (!book || !pendingQuestion || !sectionTarget) return null

  const chapterNumber = sectionTarget.chapterId
    ? book.chapters?.find(c => c.id === sectionTarget.chapterId)?.number
    : null
  const label = sectionLabel(sectionTarget, chapterNumber)
  const authorWrote = messages.some(m => m.messageType === 'ChatUser')

  const send = async () => {
    if (!text.trim() || busy) return
    setSending(true)
    try {
      if (await sendSectionChatMessage(text)) setText('')
    } finally {
      setSending(false)
    }
  }

  const toggleDiff = (id: number) => setOpenDiffs(prev => {
    const next = new Set(prev)
    if (next.has(id)) next.delete(id); else next.add(id)
    return next
  })

  const undo = () => {
    if (!sectionChat?.undoKeepsEdits
        && !confirm(`The ${label} was edited by hand after the chat's last change. Undo replaces those edits too. Continue?`)) return
    void undoSectionChatChange()
  }

  // Nothing to summarise when the author never wrote — complete straight away.
  const startComplete = async () => {
    if (!authorWrote) { await completeSectionChat(null); return }
    setStep('summarizing')
    const draft = await draftSectionChatSummary()
    if (draft === null) { setStep('chat'); return }
    setNote(draft)
    setStep('note')
  }

  return (
    <aside className={`section-chat${collapsed ? ' collapsed' : ''}`} aria-label={`Chat about the ${label}`}>
      <div className="section-chat-header">
        <button className="section-chat-title btn-ghost" onClick={() => setCollapsed(v => !v)} title={collapsed ? 'Expand' : 'Collapse'}>
          💬 Discuss the {label} {collapsed ? '▴' : '▾'}
        </button>
        {step === 'chat' && (
          <button
            className="btn-sm"
            disabled={busy || submittingAnswer}
            title={busy ? 'Wait for the reply first' : 'Continue the run with the section as it is now'}
            onClick={() => void startComplete()}
          >
            {submittingAnswer ? 'Completing…' : '✓ Complete'}
          </button>
        )}
      </div>

      {!collapsed && step === 'chat' && (
        <>
          <div className="section-chat-messages">
            <p className="section-chat-intro">{pendingQuestion.content}</p>
            {messages.length === 0 && !busy && (
              <p className="section-chat-hint">
                Ask about the {label}, or say what to change — the LLM updates it on this page.
                You can also edit it yourself. Press Complete to continue.
              </p>
            )}
            {messages.map(m => {
              const change = changes.find(c => c.replyId === m.id)
              return (
                <div key={m.id} className={`section-chat-msg ${m.messageType === 'ChatUser' ? 'from-author' : 'from-llm'}`}>
                  {m.messageType === 'ChatUser' ? <p>{m.content}</p> : <ReactMarkdown>{m.content}</ReactMarkdown>}
                  {change && (
                    <div className="section-chat-change-actions">
                      <button className="btn-sm btn-ghost" onClick={() => toggleDiff(change.id)}>
                        {openDiffs.has(change.id) ? 'Hide changes' : 'Show changes'}
                      </button>
                      {change.id === latestChange?.id && (
                        <button className="btn-sm btn-ghost" disabled={busy} onClick={undo}
                          title="Restore the section as it was before this change">↩ Undo</button>
                      )}
                    </div>
                  )}
                  {change && openDiffs.has(change.id) && <ChangeDiff change={change} />}
                </div>
              )
            })}
            {busy && (
              <div className="section-chat-msg from-llm">
                {streamed ? <ReactMarkdown>{streamed}</ReactMarkdown> : <p className="section-chat-typing"><span className="spinner" /> Thinking…</p>}
              </div>
            )}
            <div ref={bottomRef} />
          </div>

          <div className="section-chat-input">
            {sectionChatError && <p className="error-msg" role="alert">{sectionChatError}</p>}
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

      {!collapsed && step !== 'chat' && (
        <div className="section-chat-note">
          {step === 'summarizing' ? (
            <p className="section-chat-typing"><span className="spinner" /> Summarizing the decisions from this chat…</p>
          ) : (
            <>
              <p className="section-chat-hint">
                {note.trim()
                  ? 'Decisions to keep for the rest of the book. Edit them as you like — they are saved as a clarification (editable later on the Clarifications page) and every later step reads them.'
                  : 'Nothing in this chat seems worth keeping for later steps. Write a note yourself, or complete without one.'}
              </p>
              <textarea rows={8} value={note} onChange={e => setNote(e.target.value)} placeholder="- …" />
              {sectionChatError && <p className="error-msg" role="alert">{sectionChatError}</p>}
              <div className="section-chat-note-actions">
                <button disabled={submittingAnswer} onClick={() => void completeSectionChat(note.trim() ? note : null)}>
                  {submittingAnswer ? 'Completing…' : note.trim() ? 'Save note & complete' : 'Complete'}
                </button>
                {note.trim() && (
                  <button className="btn-sm btn-ghost" disabled={submittingAnswer} onClick={() => void completeSectionChat(null)}>
                    Complete without a note
                  </button>
                )}
                <button className="btn-sm btn-ghost" disabled={submittingAnswer} onClick={() => setStep('chat')}>Back to chat</button>
              </div>
            </>
          )}
        </div>
      )}
    </aside>
  )
}
