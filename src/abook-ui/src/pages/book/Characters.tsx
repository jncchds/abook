import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { createCharacter, updateCharacter, deleteCharacter, getCharactersHistory, getCharactersSnapshot, restoreCharactersSnapshot } from '../../api'
import type { CharacterCard, CharactersSnapshotMeta } from '../../api'
import { parseCharactersStream } from '../../utils/streamParsers'
import PhaseActionBar from '../../components/PhaseActionBar'
import { useRestoreStream } from '../../hooks/useRestoreStream'

export default function Characters() {
  const {
    book, characters, setCharacters, charactersStream, setCharactersStream,
    isRunning,
  } = useBookContext()

  const [editingCharId, setEditingCharId] = useState<number | null>(null)
  const [addingChar, setAddingChar] = useState(false)
  const [charForm, setCharForm] = useState<Partial<CharacterCard>>({})
  const [showHistory, setShowHistory] = useState(false)
  const [snapshots, setSnapshots] = useState<CharactersSnapshotMeta[]>([])
  const [previewData, setPreviewData] = useState<{ id: number; cards: CharacterCard[] } | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [restoring, setRestoring] = useState(false)

  useRestoreStream(book?.id, isRunning, charactersStream, 'CharactersAgent', undefined, setCharactersStream)

  if (!book) return null

  const bookId = book.id

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try {
      const r = await getCharactersHistory(bookId)
      setSnapshots(r.data)
    } finally {
      setLoadingHistory(false)
    }
    setShowHistory(true)
    setPreviewData(null)
  }

  const handlePreview = async (snapshotId: number) => {
    if (previewData?.id === snapshotId) { setPreviewData(null); return }
    const r = await getCharactersSnapshot(bookId, snapshotId)
    try {
      const cards = JSON.parse(r.data.dataJson) as CharacterCard[]
      setPreviewData({ id: snapshotId, cards })
    } catch { setPreviewData({ id: snapshotId, cards: [] }) }
  }

  const handleRestore = async (snapshotId: number) => {
    if (!confirm('Restore this snapshot? Current characters will be replaced.')) return
    setRestoring(true)
    try {
      const r = await restoreCharactersSnapshot(bookId, snapshotId)
      setCharacters(r.data)
      setShowHistory(false)
    } finally {
      setRestoring(false)
    }
  }

  if (showHistory) {
    return (
      <div className="view-content">
        <div className="history-panel-header">
          <h3>📜 Characters History</h3>
          <button className="btn-sm btn-ghost" onClick={() => setShowHistory(false)}>✕ Close</button>
        </div>
        {loadingHistory && <p className="empty">Loading…</p>}
        {!loadingHistory && snapshots.length === 0 && <p className="empty">No snapshots yet.</p>}
        <div className="history-list">
          {snapshots.map(s => (
            <div key={s.id} className={`history-item ${previewData?.id === s.id ? 'history-item-active' : ''}`}>
              <div className="history-item-meta">
                <span className="history-source-badge">{s.source}</span>
                <span className="history-reason">{s.reason || 'snapshot'}</span>
                <span className="history-date">{new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <div className="history-item-actions">
                <button className="btn-sm btn-ghost" onClick={() => handlePreview(s.id)}>
                  {previewData?.id === s.id ? 'Hide' : 'Preview'}
                </button>
                <button className="btn-sm" disabled={restoring} onClick={() => handleRestore(s.id)}>Restore</button>
              </div>
              {previewData?.id === s.id && (
                <div className="history-preview">
                  {previewData.cards.map((c, i) => (
                    <div key={i} className="history-preview-card">
                      <strong>{c.name}</strong>
                      <span className={`char-role-badge role-${c.role?.toLowerCase()}`}>{c.role}</span>
                      {c.physicalDescription && <p><em>Appearance:</em> {c.physicalDescription}</p>}
                      {c.goalMotivation && <p><em>Goal:</em> {c.goalMotivation}</p>}
                    </div>
                  ))}
                  {previewData.cards.length === 0 && <p className="empty">Empty snapshot.</p>}
                </div>
              )}
            </div>
          ))}
        </div>
      </div>
    )
  }

  return (
    <div className="view-content">
      <div className="view-header">
        <h2>Characters ({characters.length})</h2>
        <button className="btn-sm" onClick={() => { setCharForm({}); setAddingChar(true); setEditingCharId(null) }}>+ Add</button>
      </div>
      <PhaseActionBar phase="characters" onClear={() => setCharacters([])} onHistory={handleOpenHistory} style={{ marginBottom: '1rem' }} />

      {addingChar && (
        <div className="card" style={{ maxWidth: 560, marginBottom: '1rem' }}>
          <h3>Add Character</h3>
          <label>Name<input autoFocus value={charForm.name ?? ''} onChange={e => setCharForm(f => ({ ...f, name: e.target.value }))} /></label>
          <label>Role<select value={charForm.role ?? 'Supporting'} onChange={e => setCharForm(f => ({ ...f, role: e.target.value }))}>{['Protagonist','Antagonist','Supporting','Minor'].map(r => <option key={r}>{r}</option>)}</select></label>
          <label>Physical Description<textarea rows={2} value={charForm.physicalDescription ?? ''} onChange={e => setCharForm(f => ({ ...f, physicalDescription: e.target.value }))} /></label>
          <label>Personality<textarea rows={2} value={charForm.personality ?? ''} onChange={e => setCharForm(f => ({ ...f, personality: e.target.value }))} /></label>
          <label>Backstory<textarea rows={2} value={charForm.backstory ?? ''} onChange={e => setCharForm(f => ({ ...f, backstory: e.target.value }))} /></label>
          <label>Goal / Motivation<textarea rows={2} value={charForm.goalMotivation ?? ''} onChange={e => setCharForm(f => ({ ...f, goalMotivation: e.target.value }))} /></label>
          <label>Arc<textarea rows={2} value={charForm.arc ?? ''} onChange={e => setCharForm(f => ({ ...f, arc: e.target.value }))} /></label>
          <label>Notes<textarea rows={2} value={charForm.notes ?? ''} onChange={e => setCharForm(f => ({ ...f, notes: e.target.value }))} /></label>
          <div className="actions">
            <button onClick={async () => {
              if (!charForm.name?.trim()) return
              const r = await createCharacter(bookId, charForm as CharacterCard)
              setCharacters(prev => [...prev, r.data])
              setAddingChar(false)
              setCharForm({})
            }}>Save</button>
            <button className="btn-ghost" onClick={() => { setAddingChar(false); setCharForm({}) }}>Cancel</button>
          </div>
        </div>
      )}

      {charactersStream && (() => {
        const preview = parseCharactersStream(charactersStream)
        return (
          <div className="book-list" style={{ marginBottom: '1rem' }}>
            {preview.map((c, i) => (
              <div key={i} className="book-list-card">
                <div className="book-list-card-left">
                  <h3>{c.name}</h3>
                  <div className="blc-meta">
                    {c.role && <span className={`char-role-badge role-${(c.role as string).toLowerCase()}`}>{c.role as string}</span>}
                  </div>
                  {c.physicalDescription && <p className="blc-premise"><em>Appearance:</em> {c.physicalDescription}</p>}
                  {c.personality && <p className="blc-premise"><em>Personality:</em> {c.personality}</p>}
                  {c.goalMotivation && <p className="blc-premise"><em>Goal:</em> {c.goalMotivation}</p>}
                  {c.arc && <p className="blc-premise"><em>Arc:</em> {c.arc}</p>}
                </div>
              </div>
            ))}
          </div>
        )
      })()}

      {characters.length === 0 && !addingChar && !charactersStream && <p className="empty">No characters yet. Run the Planner or add manually.</p>}

      <div className="book-list">
        {characters.map(ch => (
          <div key={ch.id} className="book-list-card">
            <div className="book-list-card-left">
              {editingCharId === ch.id ? (
                <div className="char-edit-form">
                  <label>Name<input autoFocus value={charForm.name ?? ch.name} onChange={e => setCharForm(f => ({ ...f, name: e.target.value }))} /></label>
                  <label>Role<select value={charForm.role ?? ch.role} onChange={e => setCharForm(f => ({ ...f, role: e.target.value }))}>{['Protagonist','Antagonist','Supporting','Minor'].map(r => <option key={r}>{r}</option>)}</select></label>
                  <label>Physical Description<textarea rows={2} value={charForm.physicalDescription ?? ch.physicalDescription ?? ''} onChange={e => setCharForm(f => ({ ...f, physicalDescription: e.target.value }))} /></label>
                  <label>Personality<textarea rows={2} value={charForm.personality ?? ch.personality ?? ''} onChange={e => setCharForm(f => ({ ...f, personality: e.target.value }))} /></label>
                  <label>Backstory<textarea rows={2} value={charForm.backstory ?? ch.backstory ?? ''} onChange={e => setCharForm(f => ({ ...f, backstory: e.target.value }))} /></label>
                  <label>Goal / Motivation<textarea rows={2} value={charForm.goalMotivation ?? ch.goalMotivation ?? ''} onChange={e => setCharForm(f => ({ ...f, goalMotivation: e.target.value }))} /></label>
                  <label>Arc<textarea rows={2} value={charForm.arc ?? ch.arc ?? ''} onChange={e => setCharForm(f => ({ ...f, arc: e.target.value }))} /></label>
                  <label>Notes<textarea rows={2} value={charForm.notes ?? ch.notes ?? ''} onChange={e => setCharForm(f => ({ ...f, notes: e.target.value }))} /></label>
                  <div className="actions">
                    <button onClick={async () => {
                      const r = await updateCharacter(bookId, ch.id, { ...ch, ...charForm } as CharacterCard)
                      setCharacters(prev => prev.map(c => c.id === ch.id ? r.data : c))
                      setEditingCharId(null)
                      setCharForm({})
                    }}>Save</button>
                    <button className="btn-ghost" onClick={() => { setEditingCharId(null); setCharForm({}) }}>Cancel</button>
                  </div>
                </div>
              ) : (
                <>
                  <h3>{ch.name}</h3>
                  <div className="blc-meta">
                    <span className={`char-role-badge role-${ch.role?.toLowerCase()}`}>{ch.role}</span>
                  </div>
                  {ch.physicalDescription && <p className="blc-premise"><em>Appearance:</em> {ch.physicalDescription}</p>}
                  {ch.personality && <p className="blc-premise"><em>Personality:</em> {ch.personality}</p>}
                  {ch.goalMotivation && <p className="blc-premise"><em>Goal:</em> {ch.goalMotivation}</p>}
                  {ch.arc && <p className="blc-premise"><em>Arc:</em> {ch.arc}</p>}
                </>
              )}
            </div>
            {editingCharId !== ch.id && (
              <div className="book-list-card-right">
                <button className="btn-icon" title="Edit" onClick={() => { setCharForm({}); setEditingCharId(ch.id); setAddingChar(false) }}>✎</button>
                <button className="btn-icon btn-danger" title="Delete" onClick={async () => {
                  if (!confirm(`Delete character "${ch.name}"?`)) return
                  await deleteCharacter(bookId, ch.id)
                  setCharacters(prev => prev.filter(c => c.id !== ch.id))
                }}>✕</button>
              </div>
            )}
          </div>
        ))}
      </div>
    </div>
  )
}
