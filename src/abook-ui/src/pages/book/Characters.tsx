import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import {
  createCharacter, updateCharacter, archiveCharacter, unarchiveCharacter,
  getCharacterHistory, restoreCharacterVersion,
  getCharactersHistory, getCharactersSnapshot, restoreCharactersSnapshot,
} from '../../api'
import type { CharacterCard, CharacterCardVersion, CharactersSnapshotMeta } from '../../api'
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
  const [saving, setSaving] = useState(false)
  const [showArchived, setShowArchived] = useState(false)

  const [showHistory, setShowHistory] = useState(false)
  const [snapshots, setSnapshots] = useState<CharactersSnapshotMeta[]>([])
  const [previewData, setPreviewData] = useState<{ id: number; cards: CharacterCard[] } | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [restoring, setRestoring] = useState(false)

  const [itemHistoryCardId, setItemHistoryCardId] = useState<number | null>(null)
  const [itemVersions, setItemVersions] = useState<CharacterCardVersion[]>([])
  const [loadingItemHistory, setLoadingItemHistory] = useState(false)
  const [restoringVersion, setRestoringVersion] = useState(false)

  useRestoreStream(book?.id, isRunning, charactersStream, 'CharactersAgent', undefined,
    (content) => setCharactersStream(prev => prev.length >= content.length ? prev : content))

  if (!book) return null

  const bookId = book.id
  const activeChars = characters.filter(c => !c.isArchived)
  const archivedChars = characters.filter(c => c.isArchived)

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try { const r = await getCharactersHistory(bookId); setSnapshots(r.data) }
    finally { setLoadingHistory(false) }
    setShowHistory(true); setPreviewData(null)
  }

  const handlePreview = async (snapshotId: number) => {
    if (previewData?.id === snapshotId) { setPreviewData(null); return }
    const r = await getCharactersSnapshot(bookId, snapshotId)
    try { setPreviewData({ id: snapshotId, cards: JSON.parse(r.data.dataJson) as CharacterCard[] }) }
    catch { setPreviewData({ id: snapshotId, cards: [] }) }
  }

  const handleRestore = async (snapshotId: number) => {
    if (!confirm('Restore this snapshot? Current characters will be replaced.')) return
    setRestoring(true)
    try { const r = await restoreCharactersSnapshot(bookId, snapshotId); setCharacters(r.data); setShowHistory(false) }
    finally { setRestoring(false) }
  }

  const handleOpenItemHistory = async (cardId: number) => {
    if (itemHistoryCardId === cardId) { setItemHistoryCardId(null); return }
    setLoadingItemHistory(true)
    try { const r = await getCharacterHistory(bookId, cardId); setItemVersions(r.data) }
    finally { setLoadingItemHistory(false) }
    setItemHistoryCardId(cardId)
  }

  const handleRestoreVersion = async (cardId: number, versionId: number) => {
    if (!confirm('Restore this version?')) return
    setRestoringVersion(true)
    try {
      const r = await restoreCharacterVersion(bookId, cardId, versionId)
      setCharacters(prev => prev.map(c => c.id === cardId ? r.data : c))
      setItemHistoryCardId(null)
    } finally { setRestoringVersion(false) }
  }

  if (showHistory) {
    return (
      <div>
        <div className="history-panel-header">
          <h3>📜 Characters History</h3>
          <button className="btn-sm btn-ghost" onClick={() => setShowHistory(false)}>✕ Close</button>
        </div>
        {loadingHistory && <p className="empty">Loading…</p>}
        {!loadingHistory && snapshots.length === 0 && <p className="empty">No snapshots yet.</p>}
        <div className="history-list">
          {snapshots.map(s => (
            <div key={s.id} className={"history-item" + (previewData?.id === s.id ? ' history-item-active' : '')}>
              <div className="history-item-meta">
                <span className="history-source-badge">{s.source}</span>
                <span className="history-reason">{s.reason || 'snapshot'}</span>
                <span className="history-date">{new Date(s.createdAt).toLocaleString()}</span>
              </div>
              <div className="history-item-actions">
                <button className="btn-sm btn-ghost" onClick={() => handlePreview(s.id)}>{previewData?.id === s.id ? 'Hide' : 'Preview'}</button>
                <button className="btn-sm" disabled={restoring} onClick={() => handleRestore(s.id)}>Restore</button>
              </div>
              {previewData?.id === s.id && (
                <div className="history-preview">
                  {previewData.cards.map((c, i) => (
                    <div key={i} className="history-preview-card">
                      <strong>{c.name}</strong>
                      <span className={"char-role-badge role-" + c.role?.toLowerCase()}>{c.role}</span>
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
    <div>
      <div className="page-header">
        <h2>Characters ({activeChars.length}{archivedChars.length > 0 ? ' + ' + archivedChars.length + ' archived' : ''})</h2>
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
            <button disabled={saving} onClick={async () => {
              if (!charForm.name?.trim()) return
              setSaving(true)
              try {
                const r = await createCharacter(bookId, charForm as CharacterCard)
                setCharacters(prev => [...prev, r.data]); setAddingChar(false); setCharForm({})
              } finally { setSaving(false) }
            }}>{saving ? 'Saving…' : 'Save'}</button>
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
                    {c.role && <span className={"char-role-badge role-" + (c.role as string).toLowerCase()}>{c.role as string}</span>}
                  </div>
                  {c.physicalDescription && <p className="blc-premise"><em>Appearance:</em> {c.physicalDescription}</p>}
                  {c.goalMotivation && <p className="blc-premise"><em>Goal:</em> {c.goalMotivation}</p>}
                </div>
              </div>
            ))}
          </div>
        )
      })()}

      {activeChars.length === 0 && !addingChar && !charactersStream && <p className="empty">No characters yet. Run the Planner or add manually.</p>}

      {!charactersStream && <div className="book-list">
        {activeChars.map(ch => (
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
                    <button disabled={saving} onClick={async () => {
                      setSaving(true)
                      try {
                        const r = await updateCharacter(bookId, ch.id, { ...ch, ...charForm } as CharacterCard)
                        setCharacters(prev => prev.map(c => c.id === ch.id ? r.data : c))
                        setEditingCharId(null); setCharForm({})
                      } finally { setSaving(false) }
                    }}>{saving ? 'Saving…' : 'Save'}</button>
                    <button className="btn-ghost" onClick={() => { setEditingCharId(null); setCharForm({}) }}>Cancel</button>
                  </div>
                </div>
              ) : (
                <>
                  <h3>{ch.name}</h3>
                  <div className="blc-meta">
                    <span className={"char-role-badge role-" + ch.role?.toLowerCase()}>{ch.role}</span>
                  </div>
                  {ch.physicalDescription && <p className="blc-premise"><em>Appearance:</em> {ch.physicalDescription}</p>}
                  {ch.personality && <p className="blc-premise"><em>Personality:</em> {ch.personality}</p>}
                  {ch.goalMotivation && <p className="blc-premise"><em>Goal:</em> {ch.goalMotivation}</p>}
                  {ch.arc && <p className="blc-premise"><em>Arc:</em> {ch.arc}</p>}
                  {itemHistoryCardId === ch.id && (
                    <div className="item-history-panel">
                      <div className="history-panel-header" style={{ marginTop: '0.75rem' }}>
                        <strong>📜 Version History</strong>
                        <button className="btn-sm btn-ghost" onClick={() => setItemHistoryCardId(null)}>✕</button>
                      </div>
                      {loadingItemHistory && <p className="empty">Loading…</p>}
                      {!loadingItemHistory && itemVersions.length === 0 && <p className="empty">No versions yet.</p>}
                      <div className="history-list">
                        {itemVersions.map(v => (
                          <div key={v.id} className="history-item">
                            <div className="history-item-meta">
                              <span className="history-source-badge">v{v.versionNumber}</span>
                              <span className="history-reason">{v.createdBy}</span>
                              <span className="history-date">{new Date(v.createdAt).toLocaleString()}</span>
                            </div>
                            <div className="history-preview-card" style={{ marginTop: '0.25rem' }}>
                              <strong>{v.name}</strong>
                              <span className={"char-role-badge role-" + v.role?.toLowerCase()}>{v.role}</span>
                              {v.goalMotivation && <p><em>Goal:</em> {v.goalMotivation}</p>}
                            </div>
                            <div className="history-item-actions">
                              <button className="btn-sm" disabled={restoringVersion}
                                onClick={() => handleRestoreVersion(ch.id, v.id)}>↩ Restore</button>
                            </div>
                          </div>
                        ))}
                      </div>
                    </div>
                  )}
                </>
              )}
            </div>
            {editingCharId !== ch.id && (
              <div className="book-list-card-right">
                <button className="btn-icon" title="Edit" onClick={() => { setCharForm({}); setEditingCharId(ch.id); setAddingChar(false) }}>✎</button>
                <button className="btn-icon" title="Version History" onClick={() => handleOpenItemHistory(ch.id)}>📜</button>
                <button className="btn-archive" title="Archive" onClick={async () => {
                  await archiveCharacter(bookId, ch.id)
                  setCharacters(prev => prev.map(c => c.id === ch.id ? { ...c, isArchived: true } : c))
                }}>🗄</button>
              </div>
            )}
          </div>
        ))}
      </div>}

      {archivedChars.length > 0 && (
        <div style={{ marginTop: '1.5rem' }}>
          <button className="btn-sm btn-ghost" onClick={() => setShowArchived(v => !v)}>
            {showArchived ? '▲ Hide archived' : '▼ Show archived (' + archivedChars.length + ')'}
          </button>
          {showArchived && (
            <div className="book-list" style={{ marginTop: '0.5rem', opacity: 0.6 }}>
              {archivedChars.map(ch => (
                <div key={ch.id} className="book-list-card">
                  <div className="book-list-card-left">
                    <h3>{ch.name} <span className="history-source-badge">archived</span></h3>
                    <div className="blc-meta">
                      <span className={"char-role-badge role-" + ch.role?.toLowerCase()}>{ch.role}</span>
                    </div>
                    {ch.goalMotivation && <p className="blc-premise"><em>Goal:</em> {ch.goalMotivation}</p>}
                    {itemHistoryCardId === ch.id && (
                      <div className="item-history-panel">
                        <div className="history-panel-header" style={{ marginTop: '0.75rem' }}>
                          <strong>📜 Version History</strong>
                          <button className="btn-sm btn-ghost" onClick={() => setItemHistoryCardId(null)}>✕</button>
                        </div>
                        {loadingItemHistory && <p className="empty">Loading…</p>}
                        {!loadingItemHistory && itemVersions.length === 0 && <p className="empty">No versions yet.</p>}
                        <div className="history-list">
                          {itemVersions.map(v => (
                            <div key={v.id} className="history-item">
                              <div className="history-item-meta">
                                <span className="history-source-badge">v{v.versionNumber}</span>
                                <span className="history-reason">{v.createdBy}</span>
                                <span className="history-date">{new Date(v.createdAt).toLocaleString()}</span>
                              </div>
                              <div className="history-preview-card" style={{ marginTop: '0.25rem' }}>
                                <strong>{v.name}</strong>
                                <span className={"char-role-badge role-" + v.role?.toLowerCase()}>{v.role}</span>
                                {v.goalMotivation && <p><em>Goal:</em> {v.goalMotivation}</p>}
                              </div>
                              <div className="history-item-actions">
                                <button className="btn-sm" disabled={restoringVersion}
                                  onClick={() => handleRestoreVersion(ch.id, v.id)}>↩ Restore</button>
                              </div>
                            </div>
                          ))}
                        </div>
                      </div>
                    )}
                  </div>
                  <div className="book-list-card-right">
                    <button className="btn-icon" title="Version History" onClick={() => handleOpenItemHistory(ch.id)}>📜</button>
                    <button className="btn-sm" title="Unarchive" onClick={async () => {
                      const r = await unarchiveCharacter(bookId, ch.id)
                      setCharacters(prev => prev.map(c => c.id === ch.id ? r.data : c))
                    }}>♻ Restore</button>
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
