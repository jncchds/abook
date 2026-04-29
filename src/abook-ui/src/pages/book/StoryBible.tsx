import { useState, useEffect } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { updateStoryBible, getStreamBuffer } from '../../api'
import type { StoryBible } from '../../api'
import { parseStoryBibleStream } from '../../utils/streamParsers'

export default function StoryBiblePage() {
  const {
    book, storyBible, setStoryBible, storyBibleStream,
    isPhaseComplete, handleCompletePhase, handleReopenPhase, handleClearPhase,
    isRunning, setStoryBibleStream,
  } = useBookContext()

  const [editingBible, setEditingBible] = useState(false)
  const [bibleForm, setBibleForm] = useState<Partial<StoryBible>>({})

  // Restore in-progress stream on hard-refresh
  useEffect(() => {
    if (!book || !isRunning || storyBibleStream) return
    getStreamBuffer(book.id, 'StoryBibleAgent').then(r => {
      if (r.data.content) setStoryBibleStream(r.data.content)
    }).catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [book?.id])

  if (!book) return null

  const bookId = book.id

  return (
    <div className="book-overview">
      {editingBible ? (
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
          <div className="phase-actions">
            {isPhaseComplete('storybible') ? (
              <>
                <span className="phase-status-badge phase-complete">✅ Complete</span>
                <button className="btn-sm btn-ghost phase-action-btn" onClick={() => handleReopenPhase('storybible')}>↺ Reopen</button>
              </>
            ) : (
              <>
                <span className="phase-status-badge phase-not-started">⬜ Not Started</span>
                <button className="btn-sm phase-action-btn" onClick={() => handleCompletePhase('storybible')}>✓ Complete</button>
              </>
            )}
            <button className="btn-sm btn-danger phase-action-btn" onClick={() => handleClearPhase('storybible', () => setStoryBible(null))}>🗑 Clear</button>
          </div>
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
