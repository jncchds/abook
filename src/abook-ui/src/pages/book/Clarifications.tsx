import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import {
  createClarification, updateClarification, archiveClarification, unarchiveClarification,
} from '../../api'
import type { Clarification } from '../../api'
import PhaseActionBar from '../../components/PhaseActionBar'

type Draft = { question: string; answer: string }

export default function Clarifications() {
  const { book, clarifications, setClarifications, refreshClarifications, refreshBook, runStatus } = useBookContext()

  const [editingId, setEditingId] = useState<number | 'new' | null>(null)
  const [draft, setDraft] = useState<Draft>({ question: '', answer: '' })
  const [answers, setAnswers] = useState<Record<number, string>>({})
  const [busyId, setBusyId] = useState<number | 'new' | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [showArchived, setShowArchived] = useState(false)

  if (!book) return null
  const bookId = book.id

  const open = clarifications.filter(c => c.isOpen)
  const answered = clarifications.filter(c => !c.isOpen && !c.isArchived)
  const archived = clarifications.filter(c => c.isArchived)
  const plannerWaiting = runStatus?.state === 'WaitingForInput' && runStatus.role === 'Planner'

  const replace = (c: Clarification) => setClarifications(prev => prev.map(x => x.id === c.id ? c : x))

  // Closing the last open question completes the round on the server (and resumes a waiting run).
  const run = async (id: number | 'new', action: () => Promise<void>, closesOpen: boolean) => {
    setBusyId(id); setError(null)
    try {
      await action()
      if (closesOpen && open.length === 1) refreshBook().catch(() => {})
    } catch {
      setError('Could not save — try again.')
      refreshClarifications()
    } finally { setBusyId(null) }
  }

  const submitAnswer = (c: Clarification) => {
    const answer = (answers[c.id] ?? '').trim()
    if (!answer) return
    return run(c.id, async () => {
      replace((await updateClarification(bookId, c.id, { question: c.question, answer })).data)
      setAnswers(prev => {
        const next = { ...prev }
        delete next[c.id]
        return next
      })
    }, true)
  }

  const setArchived = (c: Clarification, value: boolean) =>
    run(c.id, async () => {
      replace((value ? await archiveClarification(bookId, c.id) : await unarchiveClarification(bookId, c.id)).data)
    }, value && c.isOpen)

  const startEdit = (c: Clarification | null) => {
    setEditingId(c ? c.id : 'new')
    setDraft(c ? { question: c.question, answer: c.answer } : { question: '', answer: '' })
  }

  const saveEdit = () => {
    if (!draft.question.trim() || !draft.answer.trim() || editingId === null) return
    const id = editingId
    return run(id, async () => {
      if (id === 'new') {
        const r = await createClarification(bookId, draft)
        setClarifications(prev => [...prev, r.data])
      } else {
        replace((await updateClarification(bookId, id, draft)).data)
      }
      setEditingId(null)
    }, false)
  }

  const editor = (
    <div className="char-edit-form">
      <label>Question<textarea autoFocus rows={2} value={draft.question} onChange={e => setDraft(d => ({ ...d, question: e.target.value }))} /></label>
      <label>Answer<textarea rows={3} value={draft.answer} onChange={e => setDraft(d => ({ ...d, answer: e.target.value }))} /></label>
      <div className="actions">
        <button disabled={busyId !== null || !draft.question.trim() || !draft.answer.trim()} onClick={saveEdit}>
          {busyId === editingId ? 'Saving…' : 'Save'}
        </button>
        <button className="btn-ghost" onClick={() => setEditingId(null)}>Cancel</button>
      </div>
    </div>
  )

  return (
    <div>
      <div className="page-header">
        <h2>Clarifications ({answered.length}{open.length > 0 ? ` + ${open.length} open` : ''}{archived.length > 0 ? ` + ${archived.length} archived` : ''})</h2>
        <button className="btn-sm" onClick={() => startEdit(null)}>+ Add</button>
      </div>
      <PhaseActionBar
        phase="clarifications"
        clearLabel="🗄 Archive all"
        onClear={() => { refreshClarifications() }}
        style={{ marginBottom: '0.75rem' }}
      />
      <p className="empty" style={{ marginBottom: '1rem' }}>
        The planner's questions and your answers, plus your notes between planning phases. Every answered
        clarification is added to the premise whenever an agent uses it; archived ones are ignored. Once the
        round is complete, planning doesn't ask again — reopen it to get a fresh round of questions.
      </p>
      {error && <p className="error-msg" role="alert">{error}</p>}

      {open.length > 0 && (
        <section style={{ marginBottom: '1.5rem' }}>
          <p className="question-label">
            {plannerWaiting
              ? `The planner is waiting for your answers (${open.length} left) — planning continues after the last one.`
              : `${open.length} question${open.length === 1 ? ' is' : 's are'} still open — answer them, then continue planning.`}
          </p>
          <div className="book-list">
            {open.map(c => (
              <div key={c.id} className="book-list-card">
                <div className="book-list-card-left" style={{ display: 'flex', flexDirection: 'column', gap: '0.5rem' }}>
                  <p className="question-text">{c.question}</p>
                  <textarea
                    rows={3}
                    value={answers[c.id] ?? ''}
                    onChange={e => setAnswers(a => ({ ...a, [c.id]: e.target.value }))}
                    onKeyDown={e => { if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); submitAnswer(c) } }}
                    placeholder="Type your answer…"
                  />
                  <div style={{ display: 'flex', gap: '0.5rem' }}>
                    <button disabled={busyId !== null || !(answers[c.id] ?? '').trim()} onClick={() => submitAnswer(c)}>
                      {busyId === c.id ? 'Sending…' : open.length === 1 ? 'Submit last answer' : 'Submit answer'}
                    </button>
                    <button className="btn-sm btn-ghost" disabled={busyId !== null} title="Archive this question unanswered"
                      onClick={() => setArchived(c, true)}>Skip</button>
                  </div>
                </div>
              </div>
            ))}
          </div>
        </section>
      )}

      {editingId === 'new' && <div className="card" style={{ maxWidth: 640, marginBottom: '1rem' }}>{editor}</div>}

      {answered.length === 0 && open.length === 0 && editingId !== 'new' && (
        <p className="empty">No clarifications yet. The planner's questions appear here when planning starts, or add your own.</p>
      )}

      <div className="book-list">
        {answered.map(c => (
          <div key={c.id} className="book-list-card">
            <div className="book-list-card-left">
              {editingId === c.id ? editor : (
                <>
                  <h3>{c.question}</h3>
                  <p className="blc-premise" style={{ whiteSpace: 'pre-wrap' }}>{c.answer}</p>
                </>
              )}
            </div>
            {editingId !== c.id && (
              <div className="book-list-card-right">
                <button className="btn-icon" title="Edit" onClick={() => startEdit(c)}>✎</button>
                <button className="btn-archive" title="Archive" disabled={busyId !== null} onClick={() => setArchived(c, true)}>🗄</button>
              </div>
            )}
          </div>
        ))}
      </div>

      {archived.length > 0 && (
        <div style={{ marginTop: '1.5rem' }}>
          <button className="btn-sm btn-ghost" onClick={() => setShowArchived(v => !v)}>
            {showArchived ? '▲ Hide archived' : `▼ Show archived (${archived.length})`}
          </button>
          {showArchived && (
            <div className="book-list" style={{ marginTop: '0.5rem', opacity: 0.6 }}>
              {archived.map(c => (
                <div key={c.id} className="book-list-card">
                  <div className="book-list-card-left">
                    <h3>{c.question} <span className="history-source-badge">archived</span></h3>
                    {c.answer
                      ? <p className="blc-premise" style={{ whiteSpace: 'pre-wrap' }}>{c.answer}</p>
                      : <p className="empty">Skipped without an answer.</p>}
                  </div>
                  <div className="book-list-card-right">
                    <button className="btn-sm" title="Restore" disabled={busyId !== null} onClick={() => setArchived(c, false)}>♻ Restore</button>
                  </div>
                </div>
              ))}
            </div>
          )}
        </div>
      )}
    </div>
  )
}
