import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { updateStoryBible, getStoryBibleHistory, restoreStoryBibleSnapshot } from '../../api'
import type { StoryBible, StoryBibleSnapshotMeta } from '../../api'
import { parseStoryBibleStream } from '../../utils/streamParsers'
import PhaseActionBar from '../../components/PhaseActionBar'
import { useRestoreStream } from '../../hooks/useRestoreStream'

export default function StoryBiblePage() {
  const {
    book, storyBible, setStoryBible, storyBibleStream,
    isRunning, setStoryBibleStream,
  } = useBookContext()

  const [editingBible, setEditingBible] = useState(false)
  const [bibleForm, setBibleForm] = useState<Partial<StoryBible>>({})
  const [showHistory, setShowHistory] = useState(false)
  const [snapshots, setSnapshots] = useState<StoryBibleSnapshotMeta[]>([])
  const [previewSnapshot, setPreviewSnapshot] = useState<StoryBibleSnapshotMeta | null>(null)
  const [loadingHistory, setLoadingHistory] = useState(false)
  const [restoring, setRestoring] = useState(false)

  useRestoreStream(book?.id, isRunning, storyBibleStream, 'StoryBibleAgent', undefined, setStoryBibleStream)

  if (!book) return null

  const bookId = book.id

  const handleOpenHistory = async () => {
    setLoadingHistory(true)
    try {
      const r = await getStoryBibleHistory(bookId)
      setSnapshots(r.data)
    } finally {
      setLoadingHistory(false)
    }
    setShowHistory(true)
    setPreviewSnapshot(null)
  }

  const handleRestore = async (snapshotId: number) => {
    if (!confirm('Restore this snapshot? The current Story Bible will be saved as a new snapshot first.')) return
    setRestoring(true)
    try {
      const r = await restoreStoryBibleSnapshot(bookId, snapshotId)
      setStoryBible(r.data)
      setShowHistory(false)
    } finally {
      setRestoring(false)
    }
  }

  return (
    <div className="book-overview">
      {showHistory ? (
        <div className="history-panel">
          <div className="history-panel-header">
            <h3>📜 Story Bible History</h3>
            <button className="btn-sm btn-ghost" onClick={() => setShowHistory(false)}>✕ Close</button>
          </div>
          {loadingHistory && <p className="empty">Loading…</p>}
          {!loadingHistory && snapshots.length === 0 && <p className="empty">No snapshots yet.</p>}
          <div className="history-list">
            {snapshots.map(s => (
              <div key={s.id} className={`history-item ${previewSnapshot?.id === s.id ? 'history-item-active' : ''}`}>
                <div className="history-item-meta">
                  <span className="history-reason">{s.reason || 'snapshot'}</span>
                  <span className="history-date">{new Date(s.createdAt).toLocaleString()}</span>
                </div>
                <div className="history-item-actions">
                  <button className="btn-sm btn-ghost" onClick={() => setPreviewSnapshot(previewSnapshot?.id === s.id ? null : s)}>
                    {previewSnapshot?.id === s.id ? 'Hide' : 'Preview'}
                  </button>
                  <button className="btn-sm" disabled={restoring} onClick={() => handleRestore(s.id)}>Restore</button>
                </div>
                {previewSnapshot?.id === s.id && (
                  <div className="history-preview">
                    {s.settingDescription && <p><strong>Setting:</strong> {s.settingDescription}</p>}
                    {s.timePeriod && <p><strong>Time Period:</strong> {s.timePeriod}</p>}
                    {s.themes && <p><strong>Themes:</strong> {s.themes}</p>}
                    {s.toneAndStyle && <p><strong>Tone:</strong> {s.toneAndStyle}</p>}
                    {s.worldRules && <p><strong>World Rules:</strong> {s.worldRules}</p>}
                    {s.notes && <p><strong>Notes:</strong> {s.notes}</p>}
                  </div>
                )}
              </div>
            ))}
          </div>
        </div>
      ) : editingBible ? (
        <div className="bible-edit-form">
          <h3>Story Bible</h3>
          {(['settingDescription', 'timePeriod', 'themes', 'toneAndStyle', 'worldRules', 'notes'] as const).map(field => (
            <label key={field}>
              {field.replace(/([A-Z])/g, ' $1').replace(/^./, s => s.toUpperCase())}
              <textarea rows={3} value={(bibleForm[field] as string) ?? ''} onChange={e => setBibleForm(f => ({ ...f, [field]: e.target.value }))} />
            </label>
          ))}
          <div className="bible-edit-actions">
            <button onClick={async () => {
              const updated = { ...(storyBible ?? {}), ...bibleForm, bookId } as StoryBible
              const r = await updateStoryBible(bookId, updated)
              setStoryBible(r.data)
              setEditingBible(false)
            }}>Save</button>
            <button className="btn-ghost" onClick={() => setEditingBible(false)}>Cancel</button>
          </div>
        </div>
      ) : (
        <div className="bible-view">
          <div className="bible-view-header">
            <h3>Story Bible</h3>
            <button className="btn-edit-book" onClick={() => { setBibleForm(storyBible ?? {}); setEditingBible(true) }}>✎ Edit</button>
          </div>
          <PhaseActionBar phase="storybible" onClear={() => setStoryBible(null)} onHistory={handleOpenHistory} />
          {(() => {
            const preview = storyBibleStream ? parseStoryBibleStream(storyBibleStream) : null
            const data: Partial<StoryBible> = storyBible ?? preview ?? {}
            return (
              <>
                {data.settingDescription && <p><strong>Setting:</strong> {data.settingDescription}</p>}
                {data.timePeriod && <p><strong>Time Period:</strong> {data.timePeriod}</p>}
                {data.themes && <p><strong>Themes:</strong> {data.themes}</p>}
                {data.toneAndStyle && <p><strong>Tone &amp; Style:</strong> {data.toneAndStyle}</p>}
                {data.worldRules && <><strong>World Rules:</strong><pre className="bible-pre">{data.worldRules}</pre></>}
                {data.notes && <><strong>Notes:</strong><pre className="bible-pre">{data.notes}</pre></>}
                {!storyBibleStream && !data.settingDescription && !data.timePeriod && !data.themes && (
                  <p className="empty">No Story Bible yet. Run the Planner or edit manually.</p>
                )}
              </>
            )
          })()}
        </div>
      )}
    </div>
  )
}
