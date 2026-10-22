import type { StoryBible, CharacterCard, PlotThread } from '../api'

export function parsePlanningStream(raw: string): { number: number; title: string; outline: string }[] {
  const results: { number: number; title: string; outline: string }[] = []
  let depth = 0, start = -1
  for (let i = 0; i < raw.length; i++) {
    if (raw[i] === '{') { if (depth === 0) start = i; depth++ }
    else if (raw[i] === '}') {
      depth--
      if (depth === 0 && start >= 0) {
        try {
          const obj = JSON.parse(raw.slice(start, i + 1))
          if (obj.number != null && obj.title) results.push({ number: +obj.number, title: obj.title, outline: obj.outline ?? '' })
        } catch { /* skip malformed */ }
        start = -1
      }
    }
  }
  return results
}

export function parseStoryBibleStream(raw: string): Partial<StoryBible> {
  const result: Partial<StoryBible> = {}
  const fields = ['settingDescription', 'timePeriod', 'themes', 'toneAndStyle', 'worldRules', 'notes'] as const
  for (const field of fields) {
    try {
      const re = new RegExp(`"${field}"\\s*:\\s*"((?:[^"\\\\]|\\\\.)*)"`, 's')
      const m = re.exec(raw)
      if (m) (result as Record<string, string>)[field] = m[1].replace(/\\n/g, '\n').replace(/\\"/g, '"')
    } catch { /* skip */ }
  }
  return result
}

export function parseCharactersStream(raw: string): Partial<CharacterCard>[] {
  const results: Partial<CharacterCard>[] = []
  let depth = 0, start = -1
  for (let i = 0; i < raw.length; i++) {
    if (raw[i] === '{') { if (depth === 0) start = i; depth++ }
    else if (raw[i] === '}') {
      depth--
      if (depth === 0 && start >= 0) {
        try {
          const obj = JSON.parse(raw.slice(start, i + 1))
          if (obj.name) results.push(obj)
        } catch { /* skip malformed */ }
        start = -1
      }
    }
  }
  return results
}

/**
 * The value of the top-level string field `key` of a JSON object that is still streaming in — as much of it as
 * has arrived. The book chat streams its reply as JSON; this shows the "message" while the rest is written.
 */
export function partialJsonString(raw: string, key: string): string {
  const match = new RegExp(`"${key}"\\s*:\\s*"`).exec(raw)
  if (!match) return ''
  let out = ''
  for (let i = match.index + match[0].length; i < raw.length; i++) {
    const c = raw[i]
    if (c === '"') break
    if (c !== '\\') { out += c; continue }
    const next = raw[++i]
    if (next === undefined) break
    if (next === 'n') out += '\n'
    else if (next === 't') out += '\t'
    else if (next === 'u') {
      const hex = raw.slice(i + 1, i + 5)
      if (hex.length < 4) break
      out += String.fromCharCode(parseInt(hex, 16))
      i += 4
    }
    else if (next !== 'r') out += next
  }
  return out
}

export function parsePlotThreadsStream(raw: string): Partial<PlotThread>[] {
  const results: Partial<PlotThread>[] = []
  let depth = 0, start = -1
  for (let i = 0; i < raw.length; i++) {
    if (raw[i] === '{') { if (depth === 0) start = i; depth++ }
    else if (raw[i] === '}') {
      depth--
      if (depth === 0 && start >= 0) {
        try {
          const obj = JSON.parse(raw.slice(start, i + 1))
          if (obj.name) results.push(obj)
        } catch { /* skip malformed */ }
        start = -1
      }
    }
  }
  return results
}
