import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'

type StreamHandler = (bookId: number, chapterId: number | null, agentRole: string, token: string) => void
type QuestionHandler = (bookId: number, message: unknown) => void
type StatusHandler = (bookId: number, role: string, state: string) => void
type ChapterUpdatedHandler = (bookId: number, chapterId: number) => void
type WorkflowProgressHandler = (bookId: number, step: string, isComplete: boolean) => void
type TokenStatsHandler = (bookId: number, chapterId: number | null, agentRole: string, promptTokens: number, completionTokens: number) => void
type AgentErrorHandler = (bookId: number, agentRole: string, errorMessage: string) => void

export function useBookHub(bookId: number | null) {
  const connRef = useRef<HubConnection | null>(null)
  const [connected, setConnected] = useState(false)

  const onStream = useRef<StreamHandler | null>(null)
  const onQuestion = useRef<QuestionHandler | null>(null)
  const onStatus = useRef<StatusHandler | null>(null)
  const onChapterUpdated = useRef<ChapterUpdatedHandler | null>(null)
  const onWorkflowProgress = useRef<WorkflowProgressHandler | null>(null)
  const onTokenStats = useRef<TokenStatsHandler | null>(null)
  const onAgentError = useRef<AgentErrorHandler | null>(null)

  useEffect(() => {
    if (!bookId) return

    const conn = new HubConnectionBuilder()
      .withUrl('/hubs/book')
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('AgentStreaming', (bId, cId, agentRole, token) => onStream.current?.(bId, cId, agentRole, token))
    conn.on('AgentQuestion', (bId, msg) => onQuestion.current?.(bId, msg))
    conn.on('AgentStatusChanged', (bId, role, state) => onStatus.current?.(bId, role, state))
    conn.on('ChapterUpdated', (bId, cId) => onChapterUpdated.current?.(bId, cId))
    conn.on('WorkflowProgress', (bId, step, isComplete) => onWorkflowProgress.current?.(bId, step, isComplete))
    conn.on('TokenStats', (bId, cId, role, prompt, completion) => onTokenStats.current?.(bId, cId, role, prompt, completion))
    conn.on('AgentError', (bId, role, msg) => onAgentError.current?.(bId, role, msg))

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
    setOnWorkflowProgress: (fn: WorkflowProgressHandler) => { onWorkflowProgress.current = fn },
    setOnTokenStats: (fn: TokenStatsHandler) => { onTokenStats.current = fn },
    setOnAgentError: (fn: AgentErrorHandler) => { onAgentError.current = fn },
  }
}
