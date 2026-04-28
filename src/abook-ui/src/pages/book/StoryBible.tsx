import { useState } from 'react'
import { useBookContext } from '../../contexts/BookContext'
import { updateStoryBible } from '../../api'
import type { StoryBible } from '../../api'
import { parseStoryBibleStream } from '../../utils/streamParsers'

export default function StoryBiblePage() {
  const {
    book, storyBible, setStoryBible, storyBibleStream,
    isPhaseComplete, handleCompletePhase, handleReopenPhase, handleClearPhase,
  } = useBookContext()

  const [editingBible, setEditingBible] = useState(false)
  const [bibleForm, setBibleForm] = useState<Partial<StoryBible>>({})

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
          {storyBible?.settingDescription && <p><strong>Setting:</strong> {storyBible.settingDescription}</p>}
          {storyBible?.timePeriod && <p><strong>Time Period:</strong> {storyBible.timePeriod}</p>}
          {storyBible?.themes && <p><strong>Themes:</strong> {storyBible.themes}</p>}
          {storyBible?.toneAndStyle && <p><strong>Tone &amp; Style:</strong> {storyBible.toneAndStyle}</p>}
          {storyBible?.worldRules && <><strong>World Rules:</strong><pre className="bible-pre">{storyBible.worldRules}</pre></>}
          {storyBible?.notes && <><strong>Notes:</strong><pre className="bible-pre">{storyBible.notes}</pre></>}
          {storyBibleStream && (() => {
            const preview = parseStoryBibleStream(storyBibleStream)
            return Object.keys(preview).length > 0 ? (
              <div className="stream-preview">
                <div className="stream-preview-label">⏳ Generating Story Bible…</div>
                {preview.settingDescription && <p><strong>Setting:</strong> {preview.settingDescription}</p>}
                {preview.timePeriod && <p><strong>Time Period:</strong> {preview.timePeriod}</p>}
                {preview.themes && <p><strong>Themes:</strong> {preview.themes}</p>}
                {preview.toneAndStyle && <p><strong>Tone &amp; Style:</strong> {preview.toneAndStyle}</p>}
                {preview.worldRules && <><strong>World Rules:</strong><pre className="bible-pre">{preview.worldRules}</pre></>}
                {preview.notes && <><strong>Notes:</strong><pre className="bible-pre">{preview.notes}</pre></>}
              </div>
            ) : <div className="stream-preview"><div className="stream-preview-label">⏳ Generating Story Bible…</div></div>
          })()}
          {!storyBible?.settingDescription && !storyBible?.timePeriod && !storyBible?.themes && !storyBibleStream && (
            <p className="empty">No Story Bible yet. Run the Planner or edit manually.</p>
          )}
        </div>
      )}
    </div>
  )
}
