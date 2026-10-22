import { foldContext, lineDiff, type DiffLine } from '../utils/lineDiff'

/** A blank line or an empty JSON field (`"notes": "",`): adding or removing one says nothing. */
const EMPTY_LINE = /^\s*("[^"]*"\s*:\s*"")?\s*,?\s*$/

/** Line diff of a change (unchanged stretches folded): what the book chat would change, or changed. */
export default function ChangeDiff({ before, after }: { before: string; after: string }) {
  const changed = lineDiff(before, after).filter((l: DiffLine) => l.kind === 'same' || !EMPTY_LINE.test(l.text))
  const lines = foldContext(changed)
  return (
    <div className="change-diff">
      {lines.map((l, i) => 'skipped' in l
        ? <div key={i} className="diff-skip">⋯ {l.skipped} unchanged line{l.skipped === 1 ? '' : 's'}</div>
        : <div key={i} className={`diff-${l.kind}`}>{l.kind === 'added' ? '+ ' : l.kind === 'removed' ? '− ' : '  '}{l.text || ' '}</div>)}
    </div>
  )
}
