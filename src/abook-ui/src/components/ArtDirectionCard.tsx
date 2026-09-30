import { useState } from 'react'
import type { Book } from '../api'
import { updateArtDirection } from '../api'

interface Props {
  book: Book
  editable: boolean
  canPlan: boolean
  onRewrite: () => void
  onSaved: (book: Partial<Book>) => void
}

/**
 * The book's art direction: written once by the illustrator, then added word for word to every portrait,
 * scene and the cover so they all share one style.
 */
export default function ArtDirectionCard({ book, editable, canPlan, onRewrite, onSaved }: Props) {
  const [editing, setEditing] = useState(false)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState('')
  const [form, setForm] = useState({ style: '', portraitFraming: '', negativePrompt: '' })

  const hasStyle = !!book.illustrationStyle?.trim()

  const startEdit = () => {
    setForm({
      style: book.illustrationStyle ?? '',
      portraitFraming: book.illustrationPortraitFraming ?? '',
      negativePrompt: book.illustrationNegativePrompt ?? '',
    })
    setNotice('')
    setEditing(true)
  }

  const save = async () => {
    setSaving(true)
    try {
      const r = await updateArtDirection(book.id, form)
      onSaved({
        illustrationStyle: r.data.style,
        illustrationPortraitFraming: r.data.portraitFraming,
        illustrationNegativePrompt: r.data.negativePrompt,
      })
      setNotice(r.data.markedForRerender > 0
        ? `${r.data.markedForRerender} rendered image${r.data.markedForRerender === 1 ? '' : 's'} marked for re-rendering.`
        : '')
      setEditing(false)
    } finally {
      setSaving(false)
    }
  }

  return (
    <section className="illustrations-section card">
      <h3>Art direction</h3>
      <p className="hint" style={{ marginBottom: '0.5rem' }}>
        Added word for word to every portrait, scene and the cover, so the whole book shares one style.
        Written by the illustrator from the visual style in the image settings; edit it freely.
      </p>
      {editing ? (
        <div className="illustration-edit" style={{ marginTop: 0 }}>
          <label>Style<textarea rows={3} value={form.style} onChange={e => setForm(f => ({ ...f, style: e.target.value }))} /></label>
          <label>Portrait composition (shared by every character)
            <textarea rows={2} value={form.portraitFraming} onChange={e => setForm(f => ({ ...f, portraitFraming: e.target.value }))} />
          </label>
          <label>Book-wide negative prompt
            <textarea rows={2} value={form.negativePrompt} onChange={e => setForm(f => ({ ...f, negativePrompt: e.target.value }))} />
          </label>
          <div className="illustration-edit-actions">
            <button className="btn-sm" disabled={saving} onClick={save}>{saving ? 'Saving…' : 'Save'}</button>
            <button className="btn-sm btn-ghost" disabled={saving} onClick={() => setEditing(false)}>Cancel</button>
          </div>
        </div>
      ) : hasStyle ? (
        <div className="illustration-prompt" style={{ marginTop: 0 }}>
          <p><strong>Style:</strong> {book.illustrationStyle}</p>
          {book.illustrationPortraitFraming && <p><strong>Portraits:</strong> {book.illustrationPortraitFraming}</p>}
          {book.illustrationNegativePrompt && <p><strong>Never:</strong> {book.illustrationNegativePrompt}</p>}
        </div>
      ) : (
        <p className="empty">Not written yet — it is created with the first illustration plan.</p>
      )}
      {notice && <p className="hint" style={{ marginTop: '0.4rem' }}>{notice}</p>}
      {editable && !editing && (
        <div className="illustrations-actions" style={{ marginTop: '0.5rem', marginBottom: 0 }}>
          {hasStyle && <button className="btn-sm btn-ghost" onClick={startEdit}>✎ Edit</button>}
          <button className="btn-sm btn-ghost" disabled={!canPlan} onClick={() => {
            if (hasStyle && !confirm('Have the illustrator rewrite the art direction? Rendered images will need re-rendering.')) return
            onRewrite()
          }} title="LLM: write the art direction from the visual style in the image settings">
            🎨 {hasStyle ? 'Rewrite' : 'Write'} with the illustrator
          </button>
        </div>
      )}
    </section>
  )
}
