import { useState } from 'react'
import type { CharacterCard, Illustration } from '../api'
import { updateCharacterVisual } from '../api'
import IllustrationFigure from './IllustrationFigure'

interface Props {
  bookId: number
  character: CharacterCard
  portrait?: Illustration
  editable: boolean
  canRender: boolean
  onRender: (illustration: Illustration, reroll: boolean) => void
  onRenderScenes: (characterId: number) => void
  onChanged: () => void
}

/**
 * A character's reference portrait and the canonical visual description the illustrator repeats in every
 * prompt showing them. The portrait's seed is reused for every scene centred on the character.
 */
export default function CharacterVisualPanel({
  bookId, character, portrait, editable, canRender, onRender, onRenderScenes, onChanged,
}: Props) {
  const [editing, setEditing] = useState(false)
  const [text, setText] = useState(character.visualPrompt ?? '')
  const [saving, setSaving] = useState(false)
  const [notice, setNotice] = useState('')

  if (!character.visualPrompt && !portrait) return null

  const save = async () => {
    setSaving(true)
    try {
      const r = await updateCharacterVisual(bookId, character.id, text)
      setEditing(false)
      setNotice(r.data.updatedIllustrations > 0
        ? `Updated ${r.data.updatedIllustrations} prompt${r.data.updatedIllustrations === 1 ? '' : 's'} that quoted the old description — re-render them.`
        : '')
      onChanged()
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="character-visual">
      <div>
        {portrait && (
          <IllustrationFigure
            bookId={bookId}
            illustration={portrait}
            editable={editable}
            canRender={canRender}
            onRender={onRender}
            onChanged={onChanged}
            compact
          />
        )}
      </div>
      <div>
        <p className="blc-premise"><em>Visual description</em> (repeated word for word in every image prompt):</p>
        {editing ? (
          <>
            <textarea rows={4} value={text} onChange={e => setText(e.target.value)} />
            <div className="actions" style={{ marginTop: '0.4rem' }}>
              <button className="btn-sm" disabled={saving || !text.trim()} onClick={save}>{saving ? 'Saving…' : 'Save'}</button>
              <button className="btn-sm btn-ghost" disabled={saving} onClick={() => setEditing(false)}>Cancel</button>
            </div>
          </>
        ) : (
          <p className="blc-premise">{character.visualPrompt || <span className="hint">Not described yet.</span>}</p>
        )}
        {notice && <p className="hint">{notice}</p>}
        {editable && !editing && (
          <div className="actions" style={{ marginTop: '0.4rem', display: 'flex', gap: '0.4rem', flexWrap: 'wrap' }}>
            <button className="btn-sm btn-ghost" onClick={() => { setText(character.visualPrompt ?? ''); setEditing(true) }}>✎ Edit description</button>
            <button className="btn-sm btn-ghost" disabled={!canRender} onClick={() => onRenderScenes(character.id)}
              title="Re-render the portrait and every scene centred on this character with their current seed">
              ↻ Re-render portrait &amp; scenes
            </button>
          </div>
        )}
      </div>
    </div>
  )
}
