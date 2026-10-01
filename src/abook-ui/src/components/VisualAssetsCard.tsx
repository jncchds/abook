import { useCallback, useEffect, useState } from 'react'
import type { VisualAsset, VisualAssetInput, VisualAssetKind } from '../api'
import { createVisualAsset, deleteVisualAsset, getVisualAssets, updateVisualAsset } from '../api'

const KINDS: VisualAssetKind[] = ['Outfit', 'Object', 'Location', 'Creature', 'Vehicle', 'Other']
const KIND_LABELS: Record<VisualAssetKind, string> = {
  Outfit: '👗 Outfits',
  Object: '🗝 Objects',
  Location: '🏠 Places',
  Creature: '🐾 Creatures',
  Vehicle: '🚂 Vehicles',
  Other: '✦ Other',
}

interface Props {
  bookId: number
  editable: boolean
  /** Changes whenever the illustrations do — a plan may have registered new assets. */
  refreshKey: unknown
  onChanged: () => void
}

const EMPTY: Required<VisualAssetInput> = { name: '', kind: 'Object', description: '' }

/**
 * The book's recurring visual assets — props, outfits, places … The illustrator registers each one the first
 * time an image shows it and repeats its description word for word in every later prompt, which is what keeps
 * them looking the same from image to image.
 */
export default function VisualAssetsCard({ bookId, editable, refreshKey, onChanged }: Props) {
  const [assets, setAssets] = useState<VisualAsset[]>([])
  const [editingId, setEditingId] = useState<number | 'new' | null>(null)
  const [form, setForm] = useState(EMPTY)
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState('')
  const [error, setError] = useState('')

  const load = useCallback(() => {
    getVisualAssets(bookId).then(r => setAssets(r.data)).catch(() => {})
  }, [bookId])

  useEffect(load, [load, refreshKey])

  const startEdit = (asset: VisualAsset | null) => {
    setForm(asset ? { name: asset.name, kind: asset.kind, description: asset.description } : EMPTY)
    setEditingId(asset ? asset.id : 'new')
    setNotice('')
    setError('')
  }

  const save = async () => {
    setSaving(true)
    setError('')
    try {
      if (editingId === 'new') {
        await createVisualAsset(bookId, form)
        setNotice('')
      } else if (editingId !== null) {
        const r = await updateVisualAsset(bookId, editingId, form)
        const n = r.data.updatedIllustrations
        setNotice(n > 0 ? `Updated ${n} prompt${n === 1 ? '' : 's'} that showed it — re-render them to see the change.` : '')
      }
      setEditingId(null)
      load()
      onChanged()
    } catch (e: unknown) {
      const message = (e as { response?: { data?: { message?: string } } }).response?.data?.message
      setError(message ?? 'Could not save the asset.')
    } finally {
      setSaving(false)
    }
  }

  const remove = async (asset: VisualAsset) => {
    if (!confirm(`Remove "${asset.name}" from the registry? Prompts that already show it keep their wording.`)) return
    await deleteVisualAsset(bookId, asset.id)
    load()
    onChanged()
  }

  const editor = (
    <div className="illustration-edit" style={{ marginTop: '0.5rem' }}>
      <label>Name<input value={form.name} onChange={e => setForm(f => ({ ...f, name: e.target.value }))} /></label>
      <label>Kind
        <select value={form.kind} onChange={e => setForm(f => ({ ...f, kind: e.target.value as VisualAssetKind }))}>
          {KINDS.map(k => <option key={k} value={k}>{KIND_LABELS[k]}</option>)}
        </select>
      </label>
      <label>Description
        <textarea rows={3} value={form.description} onChange={e => setForm(f => ({ ...f, description: e.target.value }))} />
        <span className="hint">
          English prose of how it looks — shape, materials, colours, markings. Outfits start with “wearing”.
          Every prompt that quotes the old wording is updated.
        </span>
      </label>
      {error && <p className="hint" style={{ color: 'var(--danger, red)' }}>{error}</p>}
      <div className="illustration-edit-actions">
        <button className="btn-sm" disabled={saving || !form.name.trim()} onClick={save}>{saving ? 'Saving…' : 'Save'}</button>
        <button className="btn-sm btn-ghost" disabled={saving} onClick={() => setEditingId(null)}>Cancel</button>
      </div>
    </div>
  )

  return (
    <section className="illustrations-section">
      <div className="illustrations-section-header">
        <h3>3 · Recurring assets</h3>
        {editable && editingId === null && (
          <div className="illustrations-section-actions">
            <button className="btn-sm btn-ghost" onClick={() => startEdit(null)}>＋ Add</button>
          </div>
        )}
      </div>
      <p className="hint" style={{ marginBottom: '0.5rem' }}>
        Outfits, props, places, creatures and vehicles that appear in more than one image. The illustrator registers
        each the first time it is shown and repeats its description word for word in every later image, so it looks
        the same each time. Each character’s usual outfit is written with their visual description.
      </p>
      {editingId === 'new' && editor}
      {assets.length === 0 && editingId !== 'new' ? (
        <p className="empty">None yet — they are registered as chapters are planned.</p>
      ) : (
        KINDS.filter(k => assets.some(a => a.kind === k)).map(kind => (
          <div key={kind} className="visual-assets-group">
            <h4>{KIND_LABELS[kind]}</h4>
            <ul className="visual-assets-list">
              {assets.filter(a => a.kind === kind).map(a => (
                <li key={a.id}>
                  {editingId === a.id ? editor : (
                    <>
                      <div className="visual-asset-header">
                        <strong>{a.name}</strong>
                        {a.firstChapterNumber && <span className="hint">from Chapter {a.firstChapterNumber}</span>}
                        {editable && editingId === null && (
                          <span className="visual-asset-actions">
                            <button className="btn-sm btn-ghost" onClick={() => startEdit(a)} title="Edit">✎</button>
                            <button className="btn-sm btn-ghost" onClick={() => remove(a)} title="Remove">🗑</button>
                          </span>
                        )}
                      </div>
                      <p className="visual-asset-description">{a.description || <em>No description.</em>}</p>
                    </>
                  )}
                </li>
              ))}
            </ul>
          </div>
        ))
      )}
      {notice && <p className="hint" style={{ marginTop: '0.4rem' }}>{notice}</p>}
    </section>
  )
}
