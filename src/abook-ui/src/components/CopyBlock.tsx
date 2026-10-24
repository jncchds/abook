import { useState } from 'react'
import { copyText } from '../utils/clipboard'

/** A block of code or a command, readable in both themes, with a Copy button in its corner. */
export default function CopyBlock({ text, inline = false }: { text: string; inline?: boolean }) {
  const [copied, setCopied] = useState(false)
  const copy = async () => {
    if (await copyText(text)) {
      setCopied(true)
      setTimeout(() => setCopied(false), 1500)
    }
  }
  return (
    <div className={'copy-block' + (inline ? ' copy-block-inline' : '')}>
      {inline ? <code>{text}</code> : <pre>{text}</pre>}
      <button type="button" className="btn-secondary btn-sm copy-block-btn" onClick={() => void copy()}>
        {copied ? '✓ Copied' : 'Copy'}
      </button>
    </div>
  )
}
