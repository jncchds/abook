import type { BookMode } from '../api'

const OPTIONS: { value: BookMode; label: string; hint: string }[] = [
  {
    value: 'Autonomous',
    label: '⚙ Autonomous',
    hint: 'The LLM plans the whole book (Story Bible, characters, plot threads, chapter outlines), then writes, checks and edits every chapter.',
  },
  {
    value: 'Interactive',
    label: '✍ Interactive',
    hint: 'Write the book in a chat with the LLM: add characters, plot threads and chapters in any order, approving each change. No chapter count needed.',
  },
]

/** How the book is written: in the book chat (Interactive) or by the automatic pipeline (Autonomous). */
export default function ModeField({ value, onChange, disabled }: { value: BookMode; onChange: (mode: BookMode) => void; disabled?: boolean }) {
  return (
    <fieldset className="mode-field" disabled={disabled}>
      <legend>How is this book written?</legend>
      {OPTIONS.map(o => (
        <label key={o.value} className={`mode-option${value === o.value ? ' selected' : ''}`}>
          <input type="radio" name="book-mode" value={o.value} checked={value === o.value} onChange={() => onChange(o.value)} />
          <span className="mode-option-text">
            <span className="mode-option-label">{o.label}</span>
            <span className="hint">{o.hint}</span>
          </span>
        </label>
      ))}
    </fieldset>
  )
}
