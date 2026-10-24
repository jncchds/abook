/** A value to hand to Telegram: shown in full (selectable), with Copy and, for links, Open. */
export default function CopyField({ label, value, hint, open, copied, onCopy }: {
  label: string; value: string; hint?: string; open?: string; copied: boolean; onCopy: () => void
}) {
  return (
    <div className="copy-field">
      <span className="copy-field-label">{label}</span>
      <div className="copy-field-row">
        <code className="copy-field-value" onClick={e => window.getSelection()?.selectAllChildren(e.currentTarget)}>{value}</code>
        <button type="button" className="btn-secondary btn-sm" onClick={onCopy}>{copied ? '✓ Copied' : 'Copy'}</button>
        {open && <a className="btn-secondary btn-sm copy-field-open" href={open} target="_blank" rel="noreferrer">Open</a>}
      </div>
      {hint && <span className="hint">{hint}</span>}
    </div>
  )
}
