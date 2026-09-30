import { useState } from 'react'
import type { CharacterCard, Illustration, IllustrationOrientation } from '../api'
import { bookImageUrl, deleteIllustration, updateIllustration } from '../api'

interface Props {
  bookId: number
  illustration: Illustration
  /** Read-only when false (public reader, or while a run holds the book). */
  editable?: boolean
  /** Rendering needs the image model; disabled while any run is active. */
  canRender?: boolean
  characters?: CharacterCard[]
  /** Paragraph count of the chapter, to bound Move up/down. Omit for covers and portraits. */
  paragraphCount?: number
  imageUrl?: (imageId: number) => string
  onRender?: (illustration: Illustration, reroll: boolean) => void
  onChanged?: () => void
  compact?: boolean
}

const STATUS_LABEL: Record<string, string> = {
  Planned: 'Not rendered',
  Rendered: 'Rendered',
  Failed: 'Failed',
}

/**
 * One illustration: the rendered image, or — before rendering — a card showing the planned prompt.
 * An edited illustration keeps its previous image, flagged, until it is re-rendered.
 */
export default function IllustrationFigure({
  bookId, illustration: ill, editable = false, canRender = false, characters = [], paragraphCount,
  imageUrl, onRender, onChanged, compact = false,
}: Props) {
  const [editing, setEditing] = useState(false)
  const [showPrompt, setShowPrompt] = useState(false)
  const [saving, setSaving] = useState(false)
  const [form, setForm] = useState({
    caption: ill.caption, prompt: ill.prompt, negativePrompt: ill.negativePrompt,
    orientation: ill.orientation, focusCharacterId: ill.focusCharacterId ?? 0,
  })

  const src = ill.imageId ? (imageUrl ?? (id => bookImageUrl(bookId, id)))(ill.imageId) : null
  const outdated = !!src && ill.status !== 'Rendered'

  const startEdit = () => {
    setForm({
      caption: ill.caption, prompt: ill.prompt, negativePrompt: ill.negativePrompt,
      orientation: ill.orientation, focusCharacterId: ill.focusCharacterId ?? 0,
    })
    setEditing(true)
  }

  const save = async () => {
    setSaving(true)
    try {
      await updateIllustration(bookId, ill.id, form)
      setEditing(false)
      onChanged?.()
    } finally {
      setSaving(false)
    }
  }

  const move = async (delta: number) => {
    await updateIllustration(bookId, ill.id, { paragraphNumber: ill.paragraphIndex + 1 + delta })
    onChanged?.()
  }

  const remove = async () => {
    if (!confirm('Delete this illustration?')) return
    await deleteIllustration(bookId, ill.id)
    onChanged?.()
  }

  return (
    <figure className={`illustration-figure illustration-${ill.orientation.toLowerCase()}${compact ? ' compact' : ''}`}>
      {src ? (
        <a href={src} target="_blank" rel="noreferrer">
          <img src={src} alt={ill.caption || 'Illustration'} loading="lazy" className={outdated ? 'outdated' : undefined} />
        </a>
      ) : (
        <div className="illustration-placeholder" onClick={() => setShowPrompt(v => !v)}>
          <span className="illustration-placeholder-icon">🎨</span>
          <span className="illustration-placeholder-text">{ill.prompt || 'No prompt yet'}</span>
        </div>
      )}
      {ill.caption && ill.kind !== 'Portrait' && <figcaption>{ill.caption}</figcaption>}

      {editable && (
        <div className="illustration-toolbar">
          <span className={`illustration-status status-${ill.status.toLowerCase()}`}
            title={ill.error ?? undefined}>
            {outdated ? 'Changed — re-render' : STATUS_LABEL[ill.status] ?? ill.status}
          </span>
          {!editing && <button className="btn-sm btn-ghost" onClick={startEdit} title="Edit prompt and placement">✎</button>}
          {src && !editing && (
            <button className="btn-sm btn-ghost" onClick={() => setShowPrompt(v => !v)} title="Show prompt">ⓘ</button>
          )}
          {paragraphCount !== undefined && ill.kind === 'Scene' && (
            <>
              <button className="btn-sm btn-ghost" disabled={ill.paragraphIndex <= 0} onClick={() => move(-1)} title="Move up a paragraph">↑</button>
              <button className="btn-sm btn-ghost" disabled={ill.paragraphIndex >= paragraphCount - 1} onClick={() => move(1)} title="Move down a paragraph">↓</button>
            </>
          )}
          {onRender && (
            <>
              <button className="btn-sm btn-ghost" disabled={!canRender} onClick={() => onRender(ill, false)}
                title="Render with the current seed">{src ? '↻ Re-render' : '▶ Render'}</button>
              <button className="btn-sm btn-ghost" disabled={!canRender} onClick={() => onRender(ill, true)}
                title={ill.kind === 'Portrait'
                  ? 'Render with a new seed — it becomes the character’s seed for scenes centred on them'
                  : 'Render with a new seed'}>🎲</button>
            </>
          )}
          {ill.kind !== 'Portrait' && (
            <button className="btn-sm btn-ghost" onClick={remove} title="Delete illustration">🗑</button>
          )}
        </div>
      )}
      {ill.status === 'Failed' && ill.error && editable && <p className="illustration-error">{ill.error}</p>}

      {showPrompt && !editing && (
        <div className="illustration-prompt">
          <p><strong>Prompt:</strong> {ill.prompt}</p>
          {ill.negativePrompt && <p><strong>Negative:</strong> {ill.negativePrompt}</p>}
          {ill.characters.length > 0 && <p><strong>Shows:</strong> {ill.characters.join(', ')}</p>}
        </div>
      )}

      {editing && (
        <div className="illustration-edit">
          {ill.kind !== 'Portrait' && (
            <label>Caption<input value={form.caption} onChange={e => setForm(f => ({ ...f, caption: e.target.value }))} /></label>
          )}
          <label>Prompt<textarea rows={5} value={form.prompt} onChange={e => setForm(f => ({ ...f, prompt: e.target.value }))} /></label>
          <label>
            Negative prompt (optional)
            <textarea rows={2} value={form.negativePrompt} onChange={e => setForm(f => ({ ...f, negativePrompt: e.target.value }))} />
            <span className="hint">Ignored at CFG 1.0 — put what the image should show in the prompt instead.</span>
          </label>
          <div className="illustration-edit-row">
            <label>
              Orientation
              <select value={form.orientation} onChange={e => setForm(f => ({ ...f, orientation: e.target.value as IllustrationOrientation }))}>
                <option value="Portrait">Portrait</option>
                <option value="Landscape">Landscape</option>
                <option value="Square">Square</option>
              </select>
            </label>
            {ill.kind !== 'Portrait' && (
              <label>
                Focus character (seed)
                <select value={form.focusCharacterId} onChange={e => setForm(f => ({ ...f, focusCharacterId: Number(e.target.value) }))}>
                  <option value={0}>— none —</option>
                  {characters.filter(c => !c.isArchived).map(c => <option key={c.id} value={c.id}>{c.name}</option>)}
                </select>
              </label>
            )}
          </div>
          <div className="illustration-edit-actions">
            <button className="btn-sm" disabled={saving} onClick={save}>{saving ? 'Saving…' : 'Save'}</button>
            <button className="btn-sm btn-ghost" disabled={saving} onClick={() => setEditing(false)}>Cancel</button>
          </div>
        </div>
      )}
    </figure>
  )
}
