import { foldContext, lineDiff } from '../utils/lineDiff'

/** Line diff of a change (unchanged stretches folded): what the book chat would change, or changed. */
export default function ChangeDiff({ before, after }: { before: string; after: string }) {
  const lines = foldContext(lineDiff(before, after))
  return (
    <div className="change-diff">
      {lines.map((l, i) => 'skipped' in l
        ? <div key={i} className="diff-skip">⋯ {l.skipped} unchanged line{l.skipped === 1 ? '' : 's'}</div>
        : <div key={i} className={`diff-${l.kind}`}>{l.kind === 'added' ? '+ ' : l.kind === 'removed' ? '− ' : '  '}{l.text || ' '}</div>)}
    </div>
  )
}
