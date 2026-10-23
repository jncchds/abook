import { HubConnection, HubConnectionBuilder, HubConnectionState, LogLevel } from '@microsoft/signalr'
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
const HEARTBEAT_MS = 15000
const HEARTBEAT_TIMEOUT_MS = 10000

/** One hub event of the book, numbered per book by the server in the order it was sent. */
interface BookEvent { seq: number; name: string; args: unknown[] }
/** Server session + last sequence number; `events` is what was missed, or null when it can't be replayed. */
interface BookSyncState { epoch: string; seq: number; events: BookEvent[] | null }

class TimeoutError extends Error {}

function withTimeout<T>(p: Promise<T>, ms: number): Promise<T> {
  return new Promise((resolve, reject) => {
    const t = setTimeout(() => reject(new TimeoutError('timeout')), ms)
    p.then(v => { clearTimeout(t); resolve(v) }, e => { clearTimeout(t); reject(e) })
  })
}

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

    // Where this client stands in the book's event sequence: the server session that numbered the
    // events and the last one handled. Events arriving while a join is in flight wait in `queued`.
    let epoch: string | null = null
    let lastSeq = 0
    let joining: Promise<void> | null = null
    let queued: BookEvent[] = []

    const conn = new HubConnectionBuilder()
      // Asked again on every (re)connect, so a reconnect after the token expired gets a fresh one.
      .withUrl('/hubs/book', { accessTokenFactory: async () => (await getAccessToken()) ?? '' })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build()

    const dispatch = (e: BookEvent) => {
      lastSeq = e.seq
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const a = e.args as any[]
      try {
        switch (e.name) {
          case 'AgentStreaming': onStream.current?.(a[0], a[1] ?? null, a[2], a[3]); break
          case 'AgentQuestion': onQuestion.current?.(a[0], a[1]); break
          case 'AgentStatusChanged': onStatus.current?.(a[0], a[1], a[2], a[3] ?? null); break
          case 'ChapterUpdated': onChapterUpdated.current?.(a[0], a[1]); break
          case 'WorkflowProgress': onWorkflowProgress.current?.(a[0], a[1], a[2]); break
          case 'TokenStats': onTokenStats.current?.(a[0], a[1] ?? null, a[2], a[3], a[4]); break
          case 'AgentError': onAgentError.current?.(a[0], a[1], a[2]); break
          case 'MessagesUpdated': onMessagesUpdated.current?.(a[0]); break
          case 'IllustrationsUpdated': onIllustrationsUpdated.current?.(a[0], a[1] ?? null); break
          case 'BookChatUpdated': onBookChatUpdated.current?.(a[0], a[1] ?? null); break
        }
      } catch (err) {
        console.error(err)
      }
    }

    /**
     * Joins (or rejoins) the book's group, telling the server what this client already has so it
     * replays exactly the events that were missed. Only when they can't be replayed (first join,
     * server restarted, gap too old) does the consumer re-read the book's state.
     */
    const join = (): Promise<void> => {
      if (joining) return joining
      const run = async () => {
        const wasJoined = epoch !== null
        const res = await conn.invoke<BookSyncState>('JoinBook', String(bookId), epoch, wasJoined ? lastSeq : null)
        if (disposed) return
        const replayed = res.events !== null
        if (res.events) for (const e of res.events) if (e.seq > lastSeq) dispatch(e)
        epoch = res.epoch
        lastSeq = replayed ? Math.max(lastSeq, res.seq) : res.seq
        // Everything up to the join point is covered; later events arrived live while joining.
        const pending = queued.filter(e => e.seq > lastSeq).sort((x, y) => x.seq - y.seq)
        queued = []
        for (const e of pending) {
          if (e.seq !== lastSeq + 1) break // still a gap: the next heartbeat rejoins
          dispatch(e)
        }
        setConnected(true)
        if (!replayed) {
          onJoined.current?.()
          if (wasJoined) onReconnected.current?.()
        }
      }
      joining = run().finally(() => { joining = null })
      return joining
    }

    // Handles events in sequence: duplicates (already replayed) are dropped, and a gap means something
    // was lost — the event waits while a rejoin replays what came before it.
    conn.on('BookEvent', (e: BookEvent) => {
      if (joining) { queued.push(e); return }
      if (e.seq <= lastSeq) return
      if (e.seq !== lastSeq + 1) {
        queued.push(e)
        join().catch(err => console.error(err))
        return
      }
      dispatch(e)
    })

    // A reconnect gets a new connection id, so the server-side group membership is gone: rejoin.
    conn.onreconnecting(() => setConnected(false))
    conn.onreconnected(() => { join().catch(err => { console.error(err); void conn.stop() }) })
    conn.onclose(() => {
      // Automatic reconnect gave up, or the heartbeat found the connection dead — restart periodically.
      setConnected(false)
      if (!disposed) retryTimer = setTimeout(() => { void start() }, START_RETRY_MS)
    })

    // withAutomaticReconnect only covers connections that were once established, so the
    // initial start (e.g. while the server is still booting) is retried here.
    const start = async () => {
      try {
        await conn.start()
        if (disposed) return
        await join()
      } catch (err) {
        console.error(err)
        if (disposed) return
        // Connected but the join failed: stopping goes through onclose, which restarts.
        if (conn.state === HubConnectionState.Connected) void conn.stop()
        else retryTimer = setTimeout(() => { void start() }, START_RETRY_MS)
      }
    }
    void start()

    /**
     * Checks that the connection still answers and that no event was lost. A connection that doesn't
     * answer is dropped and restarted; one that is no longer in the group, talks to a restarted server,
     * or is behind the server's sequence rejoins, which replays the missed events.
     */
    let beating = false
    const heartbeat = async () => {
      if (beating || disposed || joining || conn.state !== HubConnectionState.Connected) return
      beating = true
      try {
        const res = await withTimeout(conn.invoke<BookSyncState>('Heartbeat', String(bookId)), HEARTBEAT_TIMEOUT_MS)
        if (!disposed && (res.epoch !== epoch || res.seq > lastSeq)) await join()
      } catch (err) {
        if (disposed) return
        if (err instanceof TimeoutError) {
          console.warn('Book hub heartbeat timed out; reconnecting')
          void conn.stop()
        } else if (conn.state === HubConnectionState.Connected) {
          // "Not joined" (group membership lost) or a failed rejoin: try once more, else restart.
          await join().catch(e => { console.error(e); void conn.stop() })
        }
      } finally {
        beating = false
      }
    }
    const heartbeatTimer = setInterval(() => { void heartbeat() }, HEARTBEAT_MS)
    // Timers are throttled in background tabs and stop while the device sleeps: check at once on return.
    const onVisible = () => { if (document.visibilityState === 'visible') void heartbeat() }
    const onOnline = () => { void heartbeat() }
    document.addEventListener('visibilitychange', onVisible)
    window.addEventListener('online', onOnline)

    connRef.current = conn

    return () => {
      disposed = true
      clearTimeout(retryTimer)
      clearInterval(heartbeatTimer)
      document.removeEventListener('visibilitychange', onVisible)
      window.removeEventListener('online', onOnline)
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
    /** Fires after a rejoin whose missed events could not be replayed — re-read whatever they would have updated. */
    setOnReconnected: (fn: ReconnectedHandler) => { onReconnected.current = fn },
    /**
     * Fires after a join that could not replay missed events (the first join, a server restart, a gap
     * too old to replay) — re-read the run state. From then on no live event is missed.
     */
    setOnJoined: (fn: JoinedHandler) => { onJoined.current = fn },
  }
}
