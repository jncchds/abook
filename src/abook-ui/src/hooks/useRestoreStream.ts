import { useEffect, useRef } from 'react'
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
  // Stable ref so the fetch effect does not restart whenever the caller recreates its callback.
  const onRestoreRef = useRef(onRestore)
  useEffect(() => {
    onRestoreRef.current = onRestore
  }, [onRestore])

  useEffect(() => {
    if (!bookId || !isRunning || currentBuffer || !agentRole) return
    getStreamBuffer(bookId, agentRole, chapterId)
      .then(r => { if (r.data.content) onRestoreRef.current(r.data.content) })
      .catch(() => {})
  }, [bookId, chapterId, isRunning, agentRole, currentBuffer])
}
