/**
 * Splits chapter markdown into paragraphs — maximal runs of non-blank lines. Must match the server's
 * ParagraphAnchors.Split, which resolves each illustration's paragraphIndex against the same split.
 */
export function splitParagraphs(markdown: string | null | undefined): string[] {
  if (!markdown) return []
  const result: string[] = []
  let current: string[] = []
  for (const raw of markdown.split('\n')) {
    const line = raw.replace(/\r$/, '')
    if (line.trim() === '') {
      if (current.length > 0) { result.push(current.join('\n')); current = [] }
      continue
    }
    current.push(line)
  }
  if (current.length > 0) result.push(current.join('\n'))
  return result
}
