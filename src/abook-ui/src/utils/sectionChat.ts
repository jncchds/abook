import type { AgentMessage, ChatSection } from '../api'

export interface SectionTarget { section: ChatSection; chapterId: number | null }

/**
 * The section a pause question is about, or null when it is not a human-assisted section pause.
 * Mirrors `SectionChatTarget.FromPause` on the server.
 */
export function pauseTarget(q: AgentMessage | null | undefined): SectionTarget | null {
  if (!q || q.messageType !== 'Question' || !q.isOptional) return null
  switch (q.agentRole) {
    case 'StoryBibleAgent':  return { section: 'StoryBible', chapterId: null }
    case 'CharactersAgent':  return { section: 'Characters', chapterId: null }
    case 'PlotThreadsAgent': return { section: 'PlotThreads', chapterId: null }
    case 'Planner':          return q.chapterId ? null : { section: 'ChapterOutlines', chapterId: null }
    case 'Editor':           return q.chapterId ? { section: 'Chapter', chapterId: q.chapterId } : null
    case 'Writer':           return q.chapterId ? { section: 'ChapterOutline', chapterId: q.chapterId } : null
    default:                 return null
  }
}

/** The page that shows the section. */
export function sectionPath(bookId: number, t: SectionTarget): string {
  switch (t.section) {
    case 'StoryBible':      return `/books/${bookId}/story-bible`
    case 'Characters':      return `/books/${bookId}/characters`
    case 'PlotThreads':     return `/books/${bookId}/plot-threads`
    case 'ChapterOutlines': return `/books/${bookId}/chapters`
    case 'ChapterOutline':
    case 'Chapter':         return `/books/${bookId}/chapters/${t.chapterId}`
  }
}

export function sectionLabel(t: SectionTarget, chapterNumber?: number | null): string {
  switch (t.section) {
    case 'StoryBible':      return 'Story Bible'
    case 'Characters':      return 'Characters'
    case 'PlotThreads':     return 'Plot Threads'
    case 'ChapterOutlines': return 'Chapter Outlines'
    case 'ChapterOutline':  return chapterNumber ? `Chapter ${chapterNumber} outline` : 'chapter outline'
    case 'Chapter':         return chapterNumber ? `Chapter ${chapterNumber}` : 'Chapter'
  }
}

/** The reply text streamed so far, without the start of the update marker the server stops streaming at. */
export function visibleReply(stream: string): string {
  const cut = stream.indexOf('<<<')
  return (cut >= 0 ? stream.slice(0, cut) : stream).trim()
}
