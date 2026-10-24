import { useCallback, useEffect, useLayoutEffect, useMemo, useRef, useState } from 'react'
import { Link } from 'react-router-dom'
import ReactMarkdown from 'react-markdown'
import axios from 'axios'
import { useBookContext } from '../../contexts/BookContext'
import ChangeDiff from '../../components/ChangeDiff'
import { partialJsonString } from '../../utils/streamParsers'
import { readChatCache, removeChatCache, writeChatCache } from '../../utils/chatCache'
import { useAuth } from '../../hooks/useAuth'
import {
  getBookChat, sendBookChat, retryBookChat, kickoffBookChat, applyProposals, rejectProposals, undoProposal,
  lockStoryBible,
  type BookChatMessage, type BookChatState, type ChatProposal, type ChatConflict, type BookChatConflict,
} from '../../api'

const TYPE_LABELS: Record<string, string> = {
  update_premise: 'Premise',
  update_story_bible: 'Story Bible',
  upsert_character: 'Character',
  archive_character: 'Character',
  upsert_plot_thread: 'Plot thread',
  archive_plot_thread: 'Plot thread',
  add_chapter: 'New chapter',
  update_chapter_outline: 'Outline',
  archive_chapter: 'Remove chapter',
  write_chapter: 'Write',
  revise_chapter: 'Revise',
  patch_chapter: 'Edit passage',
  add_note: 'Note',
  check_outline: 'Outline check',
  check_chapter: 'Continuity check',
  summarize_chapter: 'Summary',
}

/** Long actions run the Writer or the Editor: there is no preview to diff, only what they were asked. */
const LONG_ACTIONS = new Set(['write_chapter', 'revise_chapter'])

/** Steps re-run a check or summary: they change nothing themselves, can be run again, and have nothing to undo. */
const STEP_ACTIONS = new Set(['check_outline', 'check_chapter', 'summarize_chapter'])

function errorText(err: unknown) {
  return axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err)
}

/** The 409 of an apply/undo whose target changed meanwhile; null for any other failure. */
function staleConflict(err: unknown): BookChatConflict | null {
  if (!axios.isAxiosError(err) || err.response?.status !== 409) return null
  const data = err.response.data as BookChatConflict
  return data?.result === 'Stale' ? data : null
}

function targetLink(bookId: number, target?: string | null): string | null {
  if (!target) return null
  const [kind, key] = target.includes(':') ? [target.slice(0, target.indexOf(':')), target.slice(target.indexOf(':') + 1)] : [target, '']
  switch (kind) {
    case 'premise': return `/books/${bookId}/overview`
    case 'bible': return `/books/${bookId}/story-bible`
    case 'character': return `/books/${bookId}/characters`
    case 'thread': return `/books/${bookId}/plot-threads`
    case 'chapter': case 'prose': return `/books/${bookId}/chapters/${key}`
    case 'note': return `/books/${bookId}/clarifications`
    default: return null
  }
}

function payloadInstructions(p: ChatProposal): string | null {
  try { return (JSON.parse(p.payloadJson) as { instructions?: string | null }).instructions ?? null } catch { return null }
}

function ProposalCard({ p, bookId, busy, locked, onApply, onReject, onUndo, selected, onToggle }: {
  p: ChatProposal; bookId: number; busy: boolean; locked: boolean
  onApply: (ids: number[]) => void; onReject: (ids: number[]) => void; onUndo: (id: number) => void
  /** Set when the card can be ticked for "Apply selected". */
  selected?: boolean; onToggle?: () => void
}) {
  const [open, setOpen] = useState(false)
  const instructions = LONG_ACTIONS.has(p.type) ? payloadInstructions(p) : null
  const hasDiff = !LONG_ACTIONS.has(p.type) && (p.previewBefore !== '' || p.previewAfter !== '')
  const link = p.status === 'Applied' ? targetLink(bookId, p.target) : null
  const actionable = p.status === 'Pending' || p.status === 'Failed'
  const step = STEP_ACTIONS.has(p.type)
  const status = step ? ({ Applying: 'Running', Applied: 'Done' } as Record<string, string>)[p.status] ?? p.status : p.status
  return (
    <div className={`proposal-card proposal-${p.status.toLowerCase()}${selected ? ' proposal-selected' : ''}`}>
      <div className="proposal-head">
        {onToggle && actionable ? (
          <label className="proposal-pick">
            <input type="checkbox" checked={!!selected} disabled={busy} onChange={onToggle} />
            <span className="proposal-type">{TYPE_LABELS[p.type] ?? p.type}</span>
          </label>
        ) : <span className="proposal-type">{TYPE_LABELS[p.type] ?? p.type}</span>}
        <span className="proposal-status">{p.status === 'Applying' ? <><span className="spinner" /> {status}</> : status}</span>
      </div>
      <div className="proposal-label">{p.label}</div>
      {instructions && <div className="proposal-instructions">{instructions}</div>}
      {p.type === 'write_chapter' && !locked && actionable && (
        <div className="proposal-hint">Applying this locks the premise and Story Bible.</div>
      )}
      {hasDiff && (
        <button type="button" className="btn-link proposal-toggle" onClick={() => setOpen(v => !v)}>
          {open ? 'Hide changes' : 'Show changes'}
        </button>
      )}
      {open && hasDiff && <ChangeDiff before={p.previewBefore} after={p.previewAfter} />}
      {p.resultNote && <div className="proposal-note">{p.resultNote}</div>}
      <div className="proposal-actions">
        {actionable && <button className="btn-sm" disabled={busy} onClick={() => onApply([p.id])}>{step ? '▶ Run' : 'Apply'}</button>}
        {actionable && <button className="btn-sm btn-ghost" disabled={busy} onClick={() => onReject([p.id])}>{step ? 'Dismiss' : 'Reject'}</button>}
        {p.status === 'Applied' && (step
          ? <button className="btn-sm btn-ghost" disabled={busy} onClick={() => onApply([p.id])}>↻ Run again</button>
          : <button className="btn-sm btn-ghost" disabled={busy} onClick={() => onUndo(p.id)}>↩ Undo</button>)}
        {link && <Link className="proposal-open" to={link}>Open ↗</Link>}
      </div>
    </div>
  )
}

function Proposals({ message, ...rest }: {
  message: BookChatMessage; bookId: number; busy: boolean; locked: boolean
  onApply: (ids: number[]) => void; onReject: (ids: number[]) => void; onUndo: (id: number) => void
}) {
  const proposals = message.proposals
  const pending = proposals.filter(p => p.status === 'Pending' || p.status === 'Failed')
  const plain = pending.filter(p => !p.optionGroup)
  const groups = [...new Set(proposals.map(p => p.optionGroup).filter((g): g is string => !!g))].sort()
  // Ticks are kept only while their card is still open; alternatives (options) are chosen as a whole instead.
  const [ticked, setTicked] = useState<Set<number>>(new Set())
  const selected = plain.filter(p => ticked.has(p.id)).map(p => p.id)
  const pickable = plain.length > 1
  const toggle = (id: number) => setTicked(prev => {
    const next = new Set(prev)
    if (!next.delete(id)) next.add(id)
    return next
  })
  const render = (list: ChatProposal[]) => list.map(p => (
    <ProposalCard key={p.id} p={p} {...rest}
      {...(pickable && !p.optionGroup ? { selected: ticked.has(p.id), onToggle: () => toggle(p.id) } : {})} />
  ))
  const applyIds = (ids: number[]) => { setTicked(new Set()); rest.onApply(ids) }
  const rejectIds = (ids: number[]) => { setTicked(new Set()); rest.onReject(ids) }
  return (
    <div className="proposals">
      {render(proposals.filter(p => !p.optionGroup))}
      {groups.map(g => {
        const inGroup = proposals.filter(p => p.optionGroup === g)
        const open = inGroup.filter(p => p.status === 'Pending' || p.status === 'Failed')
        return (
          <div key={g} className="proposal-option">
            <div className="proposal-option-head">
              <span>Option {g}</span>
              {open.length > 0 && (
                <button className="btn-sm" disabled={rest.busy} onClick={() => rest.onApply(open.map(p => p.id))}>Choose option {g}</button>
              )}
            </div>
            {render(inGroup)}
          </div>
        )
      })}
      {pickable && (
        <div className="proposal-bulk">
          {selected.length > 0 ? (
            <>
              <button className="btn-sm" disabled={rest.busy} onClick={() => applyIds(selected)}>Apply selected ({selected.length})</button>
              <button className="btn-sm btn-ghost" disabled={rest.busy} onClick={() => rejectIds(selected)}>Reject selected</button>
              <button className="btn-sm btn-ghost" disabled={rest.busy} onClick={() => setTicked(new Set())}>Clear</button>
            </>
          ) : (
            <>
              <button className="btn-sm" disabled={rest.busy} onClick={() => applyIds(plain.map(p => p.id))}>Apply all ({plain.length})</button>
              <button className="btn-sm btn-ghost" disabled={rest.busy} onClick={() => rejectIds(pending.map(p => p.id))}>Reject all</button>
              <span className="proposal-bulk-hint">or tick the ones you want</span>
            </>
          )}
        </div>
      )}
    </div>
  )
}

function Conflicts({ json }: { json: string }) {
  const conflicts = useMemo(() => { try { return JSON.parse(json) as ChatConflict[] } catch { return [] } }, [json])
  if (conflicts.length === 0) return null
  return (
    <div className="chat-conflicts" role="note">
      <div className="chat-conflicts-title">⚠ Conflicts with the book</div>
      <ul>
        {conflicts.map((c, i) => (
          <li key={i}>
            {c.description}
            {(c.canon || c.chapter) && <span className="chat-conflict-canon"> — {c.chapter ? `Ch. ${c.chapter}: ` : ''}{c.canon}</span>}
          </li>
        ))}
      </ul>
    </div>
  )
}

/**
 * The chat an Interactive book is written in. The author says what they want; the LLM replies, flags conflicts
 * with the book and proposes changes as cards, which do nothing until applied. Laid out as a single column so it
 * works on a phone: messages scroll, the input stays at the bottom.
 */
export default function BookChat() {
  const { book, bookChatStream, bookChatVersion, isRunning, runStatus, handleStop, refreshBook } = useBookContext()
  const bookId = book?.id ?? 0
  const userId = useAuth().user?.id
  const [chat, setChat] = useState<BookChatState | null>(() => readChatCache(userId, bookId))
  const [older, setOlder] = useState<BookChatMessage[]>([])
  const [text, setText] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [sending, setSending] = useState(false)
  const scrollRef = useRef<HTMLDivElement>(null)
  const stickToBottom = useRef(true)

  // Show the chat as it was last seen right away; the server's copy replaces it when it loads.
  useEffect(() => {
    setChat(readChatCache(userId, bookId))
    setOlder([])
  }, [userId, bookId])

  const requestSeq = useRef(0)
  const load = useCallback(async () => {
    if (!bookId) return
    const seq = ++requestSeq.current
    try {
      const r = await getBookChat(bookId)
      if (seq !== requestSeq.current) return
      setChat(r.data)
      writeChatCache(userId, bookId, r.data)
    } catch (err) {
      if (seq !== requestSeq.current) return
      // Gone or not ours (any more): the cached copy must not stay on screen.
      if (axios.isAxiosError(err) && (err.response?.status === 403 || err.response?.status === 404)) {
        removeChatCache(userId, bookId)
        setChat(null)
      }
      setError(`Could not load the chat: ${errorText(err)}`)
    }
  }, [userId, bookId])

  useEffect(() => { void load() }, [load, bookChatVersion])
  // A run that ends (Stop, failure) without a chat event still changes "busy".
  useEffect(() => { void load() }, [load, isRunning])

  const loadOlder = async () => {
    const first = (older[0] ?? chat?.messages[0])?.id
    if (!first) return
    try {
      const r = await getBookChat(bookId, first)
      stickToBottom.current = false
      setOlder(prev => [...r.data.messages, ...prev])
      setChat(prev => prev ? { ...prev, hasMore: r.data.hasMore } : prev)
    } catch (err) { setError(errorText(err)) }
  }

  const messages = useMemo(() => {
    const latest = chat?.messages ?? []
    const ids = new Set(latest.map(m => m.id))
    return [...older.filter(m => !ids.has(m.id)), ...latest]
  }, [older, chat])

  const busy = isRunning || !!chat?.busy || sending
  const chatRunning = isRunning && (runStatus?.role === 'BookChat')
  const streamed = partialJsonString(bookChatStream, 'message')
  const locked = !!(chat?.storyBibleLockedAt ?? book?.storyBibleLockedAt)
  const lastId = messages.reduce((max, m) => Math.max(max, m.id), 0)
  const lastIdRef = useRef(lastId)
  useEffect(() => { lastIdRef.current = lastId }, [lastId])

  // The author's message is shown as soon as it is sent, until the server's copy is loaded.
  const [pendingAuthor, setPendingAuthor] = useState<{ text: string; afterId: number } | null>(null)
  // The streamed reply stays on screen after the stream ends (while the server reads the proposals and saves
  // the reply) and is replaced in place once the saved reply is loaded.
  const [heldReply, setHeldReply] = useState<{ text: string; afterId: number } | null>(null)

  useEffect(() => {
    if (chatRunning && streamed) setHeldReply(prev => ({ text: streamed, afterId: prev?.afterId ?? lastIdRef.current }))
  }, [chatRunning, streamed])

  const authorArrived = !!pendingAuthor && messages.some(m => m.role === 'Author' && m.id > pendingAuthor.afterId)
  // The reply (or a failure) arrived, or the run is over and the reloaded chat has nothing more to come.
  const replyArrived = !!heldReply && (
    messages.some(m => m.id > heldReply.afterId && (m.role !== 'Author' || m.status === 'Failed')) ||
    (!isRunning && !!chat && !chat.busy))
  useEffect(() => { if (authorArrived) setPendingAuthor(null) }, [authorArrived])
  useEffect(() => { if (replyArrived) setHeldReply(null) }, [replyArrived])
  const showPendingAuthor = !!pendingAuthor && !authorArrived
  const showHeldReply = !!heldReply && !replyArrived

  const replyText = chatRunning && streamed ? streamed : showHeldReply ? heldReply.text : ''
  // The message part is complete and the LLM is still writing the proposed changes.
  const draftingChanges = !!replyText && /"actions"\s*:/.test(bookChatStream)

  useLayoutEffect(() => {
    const el = scrollRef.current
    if (el && stickToBottom.current) el.scrollTop = el.scrollHeight
  }, [messages.length, replyText, chat?.busy, pendingAuthor])

  const onScroll = () => {
    const el = scrollRef.current
    if (el) stickToBottom.current = el.scrollHeight - el.scrollTop - el.clientHeight < 80
  }

  const run = async (action: () => Promise<unknown>) => {
    setError(null)
    try { await action(); stickToBottom.current = true; await load() }
    catch (err) { setError(errorText(err)) }
  }

  const send = async () => {
    const message = text.trim()
    if (!message || busy) return
    setSending(true)
    setError(null)
    setPendingAuthor({ text: message, afterId: lastId })
    setText('')
    stickToBottom.current = true
    try {
      await sendBookChat(bookId, message)
      await load()
    } catch (err) {
      setPendingAuthor(null)
      setText(prev => prev || message)
      setError(`Could not send: ${errorText(err)}`)
    } finally {
      setSending(false)
    }
  }

  const apply = async (ids: number[]) => {
    setError(null)
    try {
      await applyProposals(bookId, ids)
    } catch (err) {
      const stale = staleConflict(err)
      if (!stale) { setError(`Could not apply: ${errorText(err)}`); return }
      const names = (stale.stale ?? []).map(s => `• ${s.label}`).join('\n')
      if (!confirm(`${stale.message}\n\n${names}\n\nApply anyway?`)) return
      try { await applyProposals(bookId, ids, true) } catch (e) { setError(`Could not apply: ${errorText(e)}`); return }
    }
    stickToBottom.current = true
    await load()
  }

  const undo = async (id: number) => {
    setError(null)
    try {
      await undoProposal(bookId, id)
    } catch (err) {
      const stale = staleConflict(err)
      if (!stale) { setError(`Could not undo: ${errorText(err)}`); return }
      if (!confirm(`${stale.message}\n\nUndo anyway?`)) return
      try { await undoProposal(bookId, id, true) } catch (e) { setError(`Could not undo: ${errorText(e)}`); return }
    }
    await load()
  }

  const lock = async () => {
    if (!confirm('Lock the premise and Story Bible and start writing? From then on the chat cannot change them — only you can unlock them again, in Book Settings.')) return
    await run(() => lockStoryBible(bookId))
    await refreshBook()
  }

  if (!book) return null
  if (book.mode !== 'Interactive') {
    return (
      <div className="studio-empty">
        <p>The book chat is for Interactive books. This book is written by the automatic pipeline.</p>
        <p>Switch its mode in <Link to={`/books/${bookId}/settings`}>Book Settings</Link> to write it in the chat.</p>
      </div>
    )
  }

  const cardProps = { bookId, busy, locked, onApply: apply, onReject: (ids: number[]) => void run(() => rejectProposals(bookId, ids)), onUndo: undo }

  return (
    <div className="studio">
      <header className="studio-header">
        <div className="studio-title">
          <span className="studio-book">{book.title}</span>
          <span className={`studio-stage${locked ? ' locked' : ''}`}>{locked ? '🔒 Writing' : '🌱 Foundation'}</span>
        </div>
        {!locked && (
          <button className="btn-sm" disabled={busy} onClick={() => void lock()} title="Lock the premise and Story Bible">
            Lock bible &amp; start writing
          </button>
        )}
      </header>

      <div className="studio-messages" ref={scrollRef} onScroll={onScroll}>
        {chat?.hasMore && <button className="btn-sm btn-ghost studio-older" onClick={() => void loadOlder()}>Load earlier messages</button>}
        {chat && !chat.hasMore && messages.every(m => m.role === 'Event') && !busy && !showPendingAuthor && (
          <div className="studio-empty">
            <p>Before you start, check the <Link to={`/books/${bookId}/overview`}>premise</Link> (Overview) and
              the LLM preset (<Link to={`/books/${bookId}/settings`}>Book Settings</Link>).</p>
            <p>When you press Start, the LLM reads your premise, proposes a Story Bible and asks what it needs to know.</p>
            <button onClick={() => void run(() => kickoffBookChat(bookId))}>Start the book chat</button>
          </div>
        )}
        {messages.map(m => m.role === 'Event' ? (
          <div key={m.id} className="chat-event"><ReactMarkdown>{m.content}</ReactMarkdown></div>
        ) : m.role === 'Author' ? (
          <div key={m.id} className="chat-msg from-author">
            {m.origin && <div className="message-origin">✈️ {m.origin}</div>}
            <p>{m.content}</p>
            {m.status === 'Failed' && (
              <div className="chat-msg-failed">
                No reply. <button className="btn-link" disabled={busy} onClick={() => void run(() => retryBookChat(bookId, m.id))}>Retry</button>
              </div>
            )}
          </div>
        ) : (
          <div key={m.id} className="chat-msg from-llm">
            <ReactMarkdown>{m.content}</ReactMarkdown>
            <Conflicts json={m.conflictsJson} />
            {m.proposals.length > 0 && <Proposals message={m} {...cardProps} />}
          </div>
        ))}
        {showPendingAuthor && (
          <div className="chat-msg from-author">
            <p>{pendingAuthor.text}</p>
          </div>
        )}
        {(chatRunning || showHeldReply || showPendingAuthor) && (
          <div className="chat-msg from-llm">
            {replyText
              ? <>
                  <ReactMarkdown>{replyText}</ReactMarkdown>
                  {draftingChanges && <p className="chat-typing"><span className="spinner" /> Preparing changes…</p>}
                </>
              : <p className="chat-typing"><span className="spinner" /> Working…</p>}
          </div>
        )}
        {isRunning && !chatRunning && (
          <p className="chat-typing studio-running"><span className="spinner" /> {runStatus?.role === 'Writer' ? 'Writing the chapter…' : runStatus?.role === 'Editor' ? 'Revising the chapter…' : runStatus?.role === 'ContinuityChecker' ? 'Checking the chapter…' : runStatus?.role === 'ChapterSummarizer' ? 'Summarizing the chapter…' : 'Working…'}</p>
        )}
      </div>

      <form className="studio-input" onSubmit={e => { e.preventDefault(); void send() }}>
        {error && <p className="error-msg" role="alert">{error}</p>}
        <div className="studio-input-row">
          <textarea
            rows={2}
            value={text}
            onChange={e => setText(e.target.value)}
            onKeyDown={e => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); void send() } }}
            placeholder={locked ? 'Add a character, a chapter, a change…' : 'Discuss the premise and Story Bible, or add characters…'}
            aria-label="Message to your co-author"
          />
          {isRunning
            ? <button type="button" className="btn-stop" onClick={() => void handleStop()}>Stop</button>
            : <button type="submit" disabled={busy || !text.trim()}>Send</button>}
        </div>
      </form>
    </div>
  )
}
