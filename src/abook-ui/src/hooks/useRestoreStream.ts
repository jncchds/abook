import { useEffect } from 'react'
import { getStreamBuffer } from '../api'

/**
 * Restores a streaming buffer from the server on hard-refresh
 * when an agent run is in progress and no local buffer exists yet.
 */
export function useRestoreStream(
  bookId: number | undefined,
  isRunning: boolean,
  currentBuffer: string,
  agentRole: string | undefined,
  chapterId: number | undefined,
  onRestore: (content: string) => void,
) {
  useEffect(() => {
    if (!bookId || !isRunning || currentBuffer) return
    getStreamBuffer(bookId, agentRole, chapterId)
      .then(r => { if (r.data.content) onRestore(r.data.content) })
      .catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bookId, chapterId])
}
