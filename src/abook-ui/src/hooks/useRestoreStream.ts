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
  // Stable ref so the callback doesn't need to be in the dependency array
  const onRestoreRef = useRef(onRestore)
  onRestoreRef.current = onRestore

  useEffect(() => {
    if (!bookId || !isRunning || currentBuffer || !agentRole) return
    getStreamBuffer(bookId, agentRole, chapterId)
      .then(r => { if (r.data.content) onRestoreRef.current(r.data.content) })
      .catch(() => {})
  }, [bookId, chapterId, isRunning, agentRole])  // currentBuffer intentionally omitted (read as gate, not trigger)
}
