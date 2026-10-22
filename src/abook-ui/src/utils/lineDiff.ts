export type DiffLine = { kind: 'same' | 'added' | 'removed'; text: string }

/** Lines this far from any change are folded away by `foldContext`. */
const CONTEXT = 2

/**
 * Line diff of two texts (longest common subsequence). Lines are paragraphs for prose and lines of the
 * pretty-printed JSON for planning sections, so the sizes stay small enough for the O(n·m) table.
 */
export function lineDiff(before: string, after: string): DiffLine[] {
  // An empty text has no lines: something new is all additions, not an empty line replaced.
  const a = before === '' ? [] : before.split('\n')
  const b = after === '' ? [] : after.split('\n')
  // Common prefix and suffix first — most changes touch a few lines of a long section.
  let start = 0
  while (start < a.length && start < b.length && a[start] === b[start]) start++
  let endA = a.length, endB = b.length
  while (endA > start && endB > start && a[endA - 1] === b[endB - 1]) { endA--; endB-- }

  const midA = a.slice(start, endA), midB = b.slice(start, endB)
  const n = midA.length, m = midB.length
  const lcs: number[][] = Array.from({ length: n + 1 }, () => new Array<number>(m + 1).fill(0))
  for (let i = n - 1; i >= 0; i--)
    for (let j = m - 1; j >= 0; j--)
      lcs[i][j] = midA[i] === midB[j] ? lcs[i + 1][j + 1] + 1 : Math.max(lcs[i + 1][j], lcs[i][j + 1])

  const out: DiffLine[] = a.slice(0, start).map(text => ({ kind: 'same', text }))
  let i = 0, j = 0
  while (i < n || j < m) {
    if (i < n && j < m && midA[i] === midB[j]) { out.push({ kind: 'same', text: midA[i] }); i++; j++ }
    else if (j < m && (i >= n || lcs[i][j + 1] >= lcs[i + 1][j])) { out.push({ kind: 'added', text: midB[j] }); j++ }
    else { out.push({ kind: 'removed', text: midA[i] }); i++ }
  }
  return out.concat(a.slice(endA).map(text => ({ kind: 'same' as const, text })))
}

/** Replaces unchanged stretches away from any change with `{ skipped }` entries counting the lines left out. */
export function foldContext(lines: DiffLine[]): (DiffLine | { skipped: number })[] {
  const near = lines.map((_, idx) =>
    lines.slice(Math.max(0, idx - CONTEXT), idx + CONTEXT + 1).some(l => l.kind !== 'same'))
  const out: (DiffLine | { skipped: number })[] = []
  let skipped = 0
  lines.forEach((line, idx) => {
    if (line.kind !== 'same' || near[idx]) {
      if (skipped > 0) { out.push({ skipped }); skipped = 0 }
      out.push(line)
    } else skipped++
  })
  if (skipped > 0) out.push({ skipped })
  return out
}
