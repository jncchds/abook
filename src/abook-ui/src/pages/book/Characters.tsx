import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { createCharacter, updateCharacter, deleteCharacter } from '../../api'
import type { CharacterCard } from '../../api'
import { parseCharactersStream } from '../../utils/streamParsers'

export default function Characters() {
  const {
    book, characters, setCharacters, charactersStream,
    isPhaseComplete, handleCompletePhase, handleReopenPhase, handleClearPhase,
  } = useBookContext()

  const [editingCharId, setEditingCharId] = useState<number | null>(null)
  const [addingChar, setAddingChar] = useState(false)
  const [charForm, setCharForm] = useState<Partial<CharacterCard>>({})

  if (!book) return null

  const bookId = book.id

  return (
    <div className="characters-view">
      <div className="characters-header">
        <h3>Characters ({characters.length})</h3>
        <button className="btn-sm" onClick={() => { setCharForm({}); setAddingChar(true); setEditingCharId(null) }}>+ Add</button>
      </div>
      <div className="phase-actions">
        {isPhaseComplete('characters') ? (
          <>
            <span className="phase-status-badge phase-complete">✅ Complete</span>
            <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('characters')}>↺ Reopen</button>
          </>
        ) : (
          <>
            <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
            <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('characters')}>✓ Complete</button>
          </>
        )}
        <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('characters', () => setCharacters([]))}>🗑 Clear</button>
      </div>
      {addingChar && (
        <div className="char-edit-form">
          <label>Name<input autoFocus value={charForm.name ?? ''} onChange={e => setCharForm(f => ({ ...f, name: e.target.value }))} /></label>
          <label>Role<select value={charForm.role ?? 'Supporting'} onChange={e => setCharForm(f => ({ ...f, role: e.target.value }))}>{['Protagonist','Antagonist','Supporting','Minor'].map(r => <option key={r}>{r}</option>)}</select></label>
          <label>Physical Description<textarea rows={2} value={charForm.physicalDescription ?? ''} onChange={e => setCharForm(f => ({ ...f, physicalDescription: e.target.value }))} /></label>
          <label>Personality<textarea rows={2} value={charForm.personality ?? ''} onChange={e => setCharForm(f => ({ ...f, personality: e.target.value }))} /></label>
          <label>Backstory<textarea rows={2} value={charForm.backstory ?? ''} onChange={e => setCharForm(f => ({ ...f, backstory: e.target.value }))} /></label>
          <label>Goal / Motivation<textarea rows={2} value={charForm.goalMotivation ?? ''} onChange={e => setCharForm(f => ({ ...f, goalMotivation: e.target.value }))} /></label>
          <label>Arc<textarea rows={2} value={charForm.arc ?? ''} onChange={e => setCharForm(f => ({ ...f, arc: e.target.value }))} /></label>
          <label>Notes<textarea rows={2} value={charForm.notes ?? ''} onChange={e => setCharForm(f => ({ ...f, notes: e.target.value }))} /></label>
          <div className="char-edit-actions">
            <button className="btn-sm" onClick={async () => {
              if (!charForm.name?.trim()) return
              const r = await createCharacter(bookId, charForm as CharacterCard)
              setCharacters(prev => [...prev, r.data])
              setAddingChar(false)
              setCharForm({})
            }}>Save</button>
            <button className="btn-sm btn-ghost" onClick={() => { setAddingChar(false); setCharForm({}) }}>Cancel</button>
          </div>
        </div>
      )}
      {charactersStream && (() => {
        const preview = parseCharactersStream(charactersStream)
        return (
          <div className="stream-preview">
            <div className="stream-preview-label">⏳ Generating Characters… ({preview.length} so far)</div>
            {preview.map((c, i) => (
              <div key={i} className="char-card">
                <div className="char-card-header">
                  <strong>{c.name}</strong>
                  {c.role && <span className={`char-role-badge role-${(c.role as string).toLowerCase()}`}>{c.role as string}</span>}
                </div>
                {c.goalMotivation && <p className="char-field"><em>Goal:</em> {c.goalMotivation}</p>}
              </div>
            ))}
          </div>
        )
      })()}
      {characters.length === 0 && !addingChar && !charactersStream && <p className="empty">No characters yet. Run the Planner or add manually.</p>}
      {characters.map(ch => (
        <div key={ch.id} className="char-card">
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
              <div className="char-edit-actions">
                <button className="btn-sm" onClick={async () => {
                  const r = await updateCharacter(bookId, ch.id, { ...ch, ...charForm } as CharacterCard)
                  setCharacters(prev => prev.map(c => c.id === ch.id ? r.data : c))
                  setEditingCharId(null)
                  setCharForm({})
                }}>Save</button>
                <button className="btn-sm btn-ghost" onClick={() => { setEditingCharId(null); setCharForm({}) }}>Cancel</button>
              </div>
            </div>
          ) : (
            <>
              <div className="char-card-header">
                <strong>{ch.name}</strong>
                <span className={`char-role-badge role-${ch.role?.toLowerCase()}`}>{ch.role}</span>
                <div className="char-card-actions">
                  <button className="btn-icon" title="Edit" onClick={() => { setCharForm({}); setEditingCharId(ch.id); setAddingChar(false) }}>✎</button>
                  <button className="btn-icon btn-danger" title="Delete" onClick={async () => {
                    if (!confirm(`Delete character "${ch.name}"?`)) return
                    await deleteCharacter(bookId, ch.id)
                    setCharacters(prev => prev.filter(c => c.id !== ch.id))
                  }}>✕</button>
                </div>
              </div>
              {ch.physicalDescription && <p className="char-field"><em>Appearance:</em> {ch.physicalDescription}</p>}
              {ch.personality && <p className="char-field"><em>Personality:</em> {ch.personality}</p>}
              {ch.goalMotivation && <p className="char-field"><em>Goal:</em> {ch.goalMotivation}</p>}
              {ch.arc && <p className="char-field"><em>Arc:</em> {ch.arc}</p>}
            </>
          )}
        </div>
      ))}
    </div>
  )
}
