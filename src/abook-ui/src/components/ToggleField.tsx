interface ToggleFieldProps {
  checked: boolean
  onChange: (checked: boolean) => void
  label: string
  hint?: string
  disabled?: boolean
}

/**
 * Switch-style boolean field. Replaces the bare native checkbox, which sat oddly next to the
 * app's rounded inputs and buttons.
 */
export default function ToggleField({ checked, onChange, label, hint, disabled }: ToggleFieldProps) {
  return (
    <label className={'toggle-field' + (disabled ? ' toggle-field-disabled' : '')}>
      <input
        type="checkbox"
        className="toggle-input"
        checked={checked}
        disabled={disabled}
        onChange={e => onChange(e.target.checked)}
      />
      <span className="toggle-track" aria-hidden="true"><span className="toggle-thumb" /></span>
      <span className="toggle-text">
        <span className="toggle-label">{label}</span>
        {hint && <span className="toggle-hint">{hint}</span>}
      </span>
    </label>
  )
}
