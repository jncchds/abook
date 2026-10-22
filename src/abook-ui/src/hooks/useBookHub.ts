import { HubConnection, HubConnectionBuilder, LogLevel } from '@microsoft/signalr'
import { useEffect, useRef, useState } from 'react'
import { getAccessToken } from '../api'

type StreamHandler = (bookId: number, chapterId: number | null, agentRole: string, token: string) => void
type QuestionHandler = (bookId: number, message: unknown) => void
type StatusHandler = (bookId: number, role: string, state: string, chapterId: number | null) => void
type ChapterUpdatedHandler = (bookId: number, chapterId: number) => void
type WorkflowProgressHandler = (bookId: number, step: string, isComplete: boolean) => void
type TokenStatsHandler = (bookId: number, chapterId: number | null, agentRole: string, promptTokens: number, completionTokens: number) => void
type AgentErrorHandler = (bookId: number, agentRole: string, errorMessage: string) => void
type MessagesUpdatedHandler = (bookId: number) => void
type IllustrationsUpdatedHandler = (bookId: number, illustrationId: number | null) => void
type BookChatUpdatedHandler = (bookId: number, messageId: number | null) => void
type ReconnectedHandler = () => void
type JoinedHandler = () => void

const START_RETRY_MS = 5000

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
  const onMessagesUpdated = useRef<MessagesUpdatedHandler | null>(null)
  const onIllustrationsUpdated = useRef<IllustrationsUpdatedHandler | null>(null)
  const onBookChatUpdated = useRef<BookChatUpdatedHandler | null>(null)
  const onReconnected = useRef<ReconnectedHandler | null>(null)
  const onJoined = useRef<JoinedHandler | null>(null)

  useEffect(() => {
    if (!bookId) return
    let disposed = false
    let retryTimer: ReturnType<typeof setTimeout> | undefined

    const conn = new HubConnectionBuilder()
      // Asked again on every (re)connect, so a reconnect after the token expired gets a fresh one.
      .withUrl('/hubs/book', { accessTokenFactory: async () => (await getAccessToken()) ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    conn.on('AgentStreaming', (bId, cId, agentRole, token) => onStream.current?.(bId, cId, agentRole, token))
    conn.on('AgentQuestion', (bId, msg) => onQuestion.current?.(bId, msg))
    conn.on('AgentStatusChanged', (bId, role, state, cId) => onStatus.current?.(bId, role, state, cId ?? null))
    conn.on('ChapterUpdated', (bId, cId) => onChapterUpdated.current?.(bId, cId))
    conn.on('WorkflowProgress', (bId, step, isComplete) => onWorkflowProgress.current?.(bId, step, isComplete))
    conn.on('TokenStats', (bId, cId, role, prompt, completion) => onTokenStats.current?.(bId, cId, role, prompt, completion))
    conn.on('AgentError', (bId, role, msg) => onAgentError.current?.(bId, role, msg))
    conn.on('MessagesUpdated', (bId) => onMessagesUpdated.current?.(bId))
    conn.on('IllustrationsUpdated', (bId, iId) => onIllustrationsUpdated.current?.(bId, iId ?? null))
    conn.on('BookChatUpdated', (bId, mId) => onBookChatUpdated.current?.(bId, mId ?? null))

    // A reconnect gets a new connection id, so the server-side group membership is gone:
    // rejoin, then let the consumer re-read whatever it missed while disconnected.
    conn.onreconnecting(() => setConnected(false))
    conn.onreconnected(async () => {
      try {
        await conn.invoke('JoinBook', String(bookId))
        setConnected(true)
        onJoined.current?.()
        onReconnected.current?.()
      } catch (err) {
        console.error(err)
      }
    })
    conn.onclose(() => {
      // Automatic reconnect gave up — fall back to periodic full restarts.
      setConnected(false)
      if (!disposed) retryTimer = setTimeout(() => { void start(true) }, START_RETRY_MS)
    })

    // withAutomaticReconnect only covers connections that were once established, so the
    // initial start (e.g. while the server is still booting) is retried here.
    const start = async (isRestart: boolean) => {
      try {
        await conn.start()
        if (disposed) return
        await conn.invoke('JoinBook', String(bookId))
        setConnected(true)
        onJoined.current?.()
        if (isRestart) onReconnected.current?.()
      } catch (err) {
        console.error(err)
        if (!disposed) retryTimer = setTimeout(() => { void start(isRestart) }, START_RETRY_MS)
      }
    }
    void start(false)

    connRef.current = conn

    return () => {
      disposed = true
      clearTimeout(retryTimer)
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
    setOnMessagesUpdated: (fn: MessagesUpdatedHandler) => { onMessagesUpdated.current = fn },
    setOnIllustrationsUpdated: (fn: IllustrationsUpdatedHandler) => { onIllustrationsUpdated.current = fn },
    setOnBookChatUpdated: (fn: BookChatUpdatedHandler) => { onBookChatUpdated.current = fn },
    setOnReconnected: (fn: ReconnectedHandler) => { onReconnected.current = fn },
    /** Fires after every (re)join of the book group — from then on no live event is missed. */
    setOnJoined: (fn: JoinedHandler) => { onJoined.current = fn },
  }
}
