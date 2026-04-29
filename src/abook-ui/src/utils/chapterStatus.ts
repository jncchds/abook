export const CHAPTER_STATUS_COLOR: Record<string, string> = {
  Outlined: '#94a3b8',
  Writing:  '#f59e0b',
  Review:   '#3b82f6',
  Editing:  '#a855f7',
  Done:     '#22c55e',
}

export const chapterStatusColor = (status: string): string =>
  CHAPTER_STATUS_COLOR[status] ?? '#94a3b8'
