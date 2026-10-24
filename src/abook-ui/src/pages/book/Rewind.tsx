import { useCallback, useEffect, useState } from 'react'
import { useParams } from 'react-router-dom'
import type { RewindPlan, RewindPoint } from '../../api'
import { getRewindPoints, previewRewind, rewindBook } from '../../api'
import { useBookContext } from '../../contexts/BookContext'

const KIND_ICONS: Record<string, string> = {
  'chat-before': '✍️', 'chat-after': '🤖', 'chapter-version': '📄', 'run-before': '▶️', 'run-after': '⏹',
}

const CHANGE_WORDS: Record<string, string> = { Restore: 'Restored', Archive: 'Archived', Recreate: 'Brought back' }

function errorText(err: unknown, fallback: string): string {
  const data = (err as { response?: { data?: { message?: string } } })?.response?.data
  return data?.message ?? fallback
}

const when = (iso: string) => new Date(iso).toLocaleString(undefined, {
  day: 'numeric', month: 'short', hour: '2-digit', minute: '2-digit', second: '2-digit',
})

/**
 * Rewind the whole book (and its chat) to an earlier moment: pick a moment, check the preview, confirm. Each part gets
 * the state it had then as a new version — the versions in between stay in its history — and later chat messages are
 * hidden for good. There is no fast-forward.
 */
export default function Rewind() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)
  const { isRunning } = useBookContext()

  const [points, setPoints] = useState<RewindPoint[]>([])
  const [earliest, setEarliest] = useState<string | null>(null)
  const [hasMore, setHasMore] = useState(false)
  const [plan, setPlan] = useState<RewindPlan | null>(null)
  const [error, setError] = useState('')
  const [busy, setBusy] = useState(false)
  const [done, setDone] = useState('')

  const load = useCallback((before?: string) => {
    getRewindPoints(id, before)
      .then(r => {
        setEarliest(r.data.earliest)
        setPoints(prev => before ? [...prev, ...r.data.points] : r.data.points)
        setHasMore(r.data.points.length >= 30)
      })
      .catch(err => setError(errorText(err, 'Could not load the moments to rewind to.')))
  }, [id])

  useEffect(() => { load() }, [load])

  const preview = async (at: string) => {
    setError('')
    setDone('')
    try {
      setPlan((await previewRewind(id, at)).data)
    } catch (err) {
      setError(errorText(err, 'Could not preview the rewind.'))
    }
  }

  const confirmRewind = async () => {
    if (!plan) return
    if (!confirm(`Rewind the whole book to ${when(plan.at)}? Later chat messages are hidden for good — there is no fast-forward.`)) return
    setBusy(true)
    setError('')
    try {
      await rewindBook(id, plan.at, plan.fingerprint)
      setDone(`Rewinding to ${when(plan.at)}… The book updates in a moment.`)
      setPlan(null)
      setTimeout(() => load(), 3000)
    } catch (err) {
      setError(errorText(err, 'Could not rewind.'))
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="rewind-page">
      <div className="page-header">
        <h2>⏪ Rewind</h2>
      </div>
      <p className="hint">
        Bring the whole book back to how it was at a moment: chapters, Story Bible, characters, plot threads, clarifications
        and the book chat. Each part gets the state it had then <b>as a new version</b> — everything in between stays in its
        history, where you can still open or restore it. Book-chat messages after the moment are hidden and never sent to the
        LLM again. Settings (prompts, LLM, mode) are not touched.
        {earliest && <> History starts {when(earliest)}.</>}
      </p>
      {error && <p className="error-msg" role="alert">{error}</p>}
      {done && <p className="saved-msg">{done}</p>}

      {plan && (
        <div className="card rewind-preview">
          <h3>Rewind to {when(plan.at)}?</h3>
          {plan.isEmpty ? (
            <p className="hint">The book is already as it was then — nothing to rewind.</p>
          ) : (
            <>
              <ul className="rewind-changes">
                {plan.changes.map(c => (
                  <li key={`${c.entityType}-${c.entityId}`}>
                    <span className={`rewind-kind rewind-${c.kind.toLowerCase()}`}>{CHANGE_WORDS[c.kind]}</span> {c.label}
                    {c.fields.length > 0 && <span className="hint"> — {c.fields.join(', ')}</span>}
                  </li>
                ))}
                {plan.hiddenChatMessages > 0 && (
                  <li><span className="rewind-kind rewind-archive">Hidden</span> {plan.hiddenChatMessages} book-chat message(s)</li>
                )}
              </ul>
              {isRunning && <p className="hint">An agent is running — stop it or wait before rewinding.</p>}
            </>
          )}
          <div className="actions">
            {!plan.isEmpty && (
              <button type="button" className="delete-btn" disabled={busy || isRunning} onClick={() => void confirmRewind()}>
                ⏪ Rewind
              </button>
            )}
            <button type="button" className="btn-secondary" onClick={() => setPlan(null)}>Cancel</button>
          </div>
        </div>
      )}

      {points.length === 0
        ? <p className="empty">No moments to rewind to yet.</p>
        : (
          <div className="book-list">
            {points.map(p => (
              <div key={`${p.at}-${p.kind}`} className="book-list-card rewind-point">
                <div className="book-list-card-left">
                  <span className="hint">{when(p.at)}</span>
                  <p className="blc-premise">{KIND_ICONS[p.kind] ?? '•'} {p.label}</p>
                </div>
                <div className="book-list-card-right">
                  <button type="button" className="btn-sm" onClick={() => void preview(p.at)}>Preview</button>
                </div>
              </div>
            ))}
          </div>
        )}
      {hasMore && points.length > 0 && (
        <button type="button" className="btn-sm btn-ghost" onClick={() => load(points[points.length - 1].at)}>Older moments</button>
      )}
    </div>
  )
}
