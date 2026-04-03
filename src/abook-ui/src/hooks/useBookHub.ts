import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'

type StreamHandler = (bookId: number, chapterId: number | null, token: string) => void
type QuestionHandler = (bookId: number, message: unknown) => void
type StatusHandler = (bookId: number, role: string, state: string) => void
type ChapterUpdatedHandler = (bookId: number, chapterId: number) => void

export function useBookHub(bookId: number | null) {
  const connRef = useRef<HubConnection | null>(null)
  const [connected, setConnected] = useState(false)

  const onStream = useRef<StreamHandler | null>(null)
  const onQuestion = useRef<QuestionHandler | null>(null)
  const onStatus = useRef<StatusHandler | null>(null)
  const onChapterUpdated = useRef<ChapterUpdatedHandler | null>(null)

  useEffect(() => {
    if (!bookId) return

    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/book')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('AgentStreaming', (bId, cId, token) => onStream.current?.(bId, cId, token))
    conn.on('AgentQuestion', (bId, msg) => onQuestion.current?.(bId, msg))
    conn.on('AgentStatusChanged', (bId, role, state) => onStatus.current?.(bId, role, state))
    conn.on('ChapterUpdated', (bId, cId) => onChapterUpdated.current?.(bId, cId))

    conn.start()
      .then(() => {
        conn.invoke('JoinBook', String(bookId))
        setConnected(true)
      })
      .catch(console.error)

    connRef.current = conn

    return () => {
      conn.invoke('LeaveBook', String(bookId)).catch(() => {})
      conn.stop()
      setConnected(false)
    }
  }, [bookId])

  return {
    connected,
    setOnStream: (fn: StreamHandler) => { onStream.current = fn },
    setOnQuestion: (fn: QuestionHandler) => { onQuestion.current = fn },
    setOnStatus: (fn: StatusHandler) => { onStatus.current = fn },
    setOnChapterUpdated: (fn: ChapterUpdatedHandler) => { onChapterUpdated.current = fn },
  }
}
