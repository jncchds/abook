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
