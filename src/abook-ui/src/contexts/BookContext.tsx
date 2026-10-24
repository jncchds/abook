import { createContext, useContext, useEffect, useRef, useState, useCallback, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import axios from 'axios'
import type { Book, AgentMessage, AgentRunStatus, StoryBible, CharacterCard, PlotThread, Illustration, IllustrationPlanScope, RenderScope, Clarification } from '../api'
import {
  getBook, getMessages, postAnswer, getAgentStatus,
  startPlanning, completePlanningPhase, reopenPlanningPhase, clearPlanningPhase,
  startWorkflow, stopWorkflow,
  getTokenUsage, clearMessages, clearTokenUsage,
  getStoryBible, getCharacters, getPlotThreads, getChapters, getChapter,
  getIllustrations, planIllustrations, renderIllustrations, getClarifications,
} from '../api'
import { useBookHub } from '../hooks/useBookHub'
import { useNotifications } from '../hooks/useNotifications'

export interface TokenStat {
  id: number
  chapterId: number | null
  role: string
  prompt: number
  completion: number
  time: string
  endpoint?: string | null
  modelName?: string | null
  failed?: boolean
  failureReason?: string | null
  persisted?: boolean
}

export interface WorkflowStep {
  id: number
  step: string
  time: string
  endpoint?: string | null
  modelName?: string | null
}

interface BookContextValue {
  book: Book | null
  setBook: React.Dispatch<React.SetStateAction<Book | null>>
  messages: AgentMessage[]
  setMessages: React.Dispatch<React.SetStateAction<AgentMessage[]>>
  pendingQuestion: AgentMessage | null
  setPendingQuestion: React.Dispatch<React.SetStateAction<AgentMessage | null>>
  runStatus: AgentRunStatus | null
  setRunStatus: React.Dispatch<React.SetStateAction<AgentRunStatus | null>>
  isRunning: boolean
  /** True while an agent is parked on a question. The run holds no lock on the data in this state. */
  isWaitingForInput: boolean
  /** Content is editable when nothing is running, or when a run is paused waiting for the author. */
  canEdit: boolean

  plannerBuffer: string
  setPlannerBuffer: React.Dispatch<React.SetStateAction<string>>
  streamBuffer: string
  streamingChapterId: number | null
  storyBibleStream: string
  setStoryBibleStream: React.Dispatch<React.SetStateAction<string>>
  charactersStream: string
  setCharactersStream: React.Dispatch<React.SetStateAction<string>>
  plotThreadsStream: string
  setPlotThreadsStream: React.Dispatch<React.SetStateAction<string>>
  setStreamBuffer: React.Dispatch<React.SetStateAction<string>>
  setStreamingChapterId: React.Dispatch<React.SetStateAction<number | null>>

  storyBible: StoryBible | null
  setStoryBible: React.Dispatch<React.SetStateAction<StoryBible | null>>
  characters: CharacterCard[]
  setCharacters: React.Dispatch<React.SetStateAction<CharacterCard[]>>
  plotThreads: PlotThread[]
  setPlotThreads: React.Dispatch<React.SetStateAction<PlotThread[]>>

  /** All clarifications, archived included. Open ones (no answer yet) hold up a waiting planning run. */
  clarifications: Clarification[]
  setClarifications: React.Dispatch<React.SetStateAction<Clarification[]>>
  refreshClarifications: () => Promise<Clarification[] | null>
  openClarificationCount: number

  /** Every illustration of the book: portraits, cover and chapter scenes. */
  illustrations: Illustration[]
  refreshIllustrations: () => Promise<void>
  /** LLM run: writes image prompts and positions. */
  handlePlanIllustrations: (scope: IllustrationPlanScope, targetId?: number) => Promise<void>
  /** Image-model run: renders planned illustrations (no LLM calls). */
  handleRenderIllustrations: (scope: RenderScope, targetId?: number, reroll?: boolean) => Promise<void>

  tokenStats: TokenStat[]
  setTokenStats: React.Dispatch<React.SetStateAction<TokenStat[]>>
  workflowSteps: WorkflowStep[]
  setWorkflowSteps: React.Dispatch<React.SetStateAction<WorkflowStep[]>>
  workflowLog: string[]

  answerText: string
  setAnswerText: React.Dispatch<React.SetStateAction<string>>
  submittingAnswer: boolean
  /** Why the last answer could not be sent; cleared on the next attempt. */
  answerError: string | null

  refreshBook: () => Promise<void>
  refreshMessages: () => void

  /** Sends the typed answer, or `text` when given (Skip passes ''). */
  handleAnswer: (text?: string) => Promise<void>

  /** Interactive books: the reply the book chat LLM is streaming right now (raw JSON). */
  bookChatStream: string
  /** Bumped whenever the book chat changed on the server; the chat page re-reads on change. */
  bookChatVersion: number
  handleWriteBook: () => Promise<void>
  handlePlanBook: () => Promise<void>
  handleStop: () => Promise<void>
  handleCompletePhase: (phase: string) => Promise<void>
  handleReopenPhase: (phase: string) => Promise<void>
  handleClearPhase: (phase: string, clearLocal: () => void) => Promise<void>
  isPhaseComplete: (phase: string) => boolean

  clearMessagesForBook: () => Promise<void>
  clearTokenUsageForBook: () => Promise<void>
}

const BookContext = createContext<BookContextValue | null>(null)

interface LiveToken { agentRole: string; chapterId: number | null; token: string }

/** Length of the longest prefix of `tail` that `text` already ends with. */
function overlapLength(text: string, tail: string) {
  for (let k = Math.min(text.length, tail.length); k > 0; k--)
    if (text.endsWith(tail.slice(0, k))) return k
  return 0
}

export function useBookContext() {
  const ctx = useContext(BookContext)
  if (!ctx) throw new Error('useBookContext must be used within BookContextProvider')
  return ctx
}

export function BookContextProvider({ bookId, children }: { bookId: number; children: ReactNode }) {
  const navigate = useNavigate()

  const [book, setBook] = useState<Book | null>(null)
  const [messages, setMessages] = useState<AgentMessage[]>([])
  const [pendingQuestion, setPendingQuestion] = useState<AgentMessage | null>(null)
  const [runStatus, setRunStatus] = useState<AgentRunStatus | null>(null)

  const [plannerBuffer, setPlannerBuffer] = useState('')
  const [streamBuffer, setStreamBuffer] = useState('')
  const [streamingChapterId, setStreamingChapterId] = useState<number | null>(null)
  const [storyBibleStream, setStoryBibleStream] = useState('')
  const [charactersStream, setCharactersStream] = useState('')
  const [plotThreadsStream, setPlotThreadsStream] = useState('')
  const [bookChatStream, setBookChatStream] = useState('')
  const [bookChatVersion, setBookChatVersion] = useState(0)

  const [storyBible, setStoryBible] = useState<StoryBible | null>(null)
  const [characters, setCharacters] = useState<CharacterCard[]>([])
  const [plotThreads, setPlotThreads] = useState<PlotThread[]>([])
  const [illustrations, setIllustrations] = useState<Illustration[]>([])
  const [clarifications, setClarifications] = useState<Clarification[]>([])

  const [tokenStats, setTokenStats] = useState<TokenStat[]>([])
  const [workflowSteps, setWorkflowSteps] = useState<WorkflowStep[]>([])
  const [workflowLog, setWorkflowLog] = useState<string[]>([])

  const [answerText, setAnswerText] = useState('')
  const [submittingAnswer, setSubmittingAnswer] = useState(false)
  const [answerError, setAnswerError] = useState<string | null>(null)

  const { setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError, setOnMessagesUpdated, setOnIllustrationsUpdated, setOnBookChatUpdated, setOnBookRewound, setOnReconnected, setOnJoined } = useBookHub(bookId)
  const { notify } = useNotifications()
  const notifyRef = useRef(notify)
  useEffect(() => { notifyRef.current = notify })

  const refreshBook = useCallback(() =>
    getBook(bookId).then(r => setBook(r.data)), [bookId])

  // Several events can trigger a refresh in quick succession; only the newest response may win,
  // otherwise a slower, older response overwrites messages that arrived after it.
  const messagesRequestSeq = useRef(0)
  const refreshMessages = useCallback(() => {
    const seq = ++messagesRequestSeq.current
    getMessages(bookId).then(r => {
      if (seq !== messagesRequestSeq.current) return
      setMessages(r.data)
      // The question may have been answered from another tab or device.
      setPendingQuestion(prev => prev && r.data.some(m => m.id === prev.id && m.isResolved) ? null : prev)
    }).catch(() => {})
  }, [bookId])

  // Renders report one image at a time; only the newest listing may win.
  const illustrationsRequestSeq = useRef(0)
  const refreshIllustrations = useCallback(async () => {
    const seq = ++illustrationsRequestSeq.current
    try {
      const r = await getIllustrations(bookId)
      if (seq === illustrationsRequestSeq.current) setIllustrations(r.data)
    } catch { /* keep the current list */ }
  }, [bookId])

  const clarificationsRequestSeq = useRef(0)
  const refreshClarifications = useCallback(async () => {
    const seq = ++clarificationsRequestSeq.current
    try {
      const r = await getClarifications(bookId, true)
      if (seq === clarificationsRequestSeq.current) setClarifications(r.data)
      return r.data
    } catch { return null }
  }, [bookId])

  const refreshTokenStats = useCallback((signal?: AbortSignal) =>
    getTokenUsage(bookId, signal).then(r => {
      if (signal?.aborted) return
      setWorkflowSteps(r.data.filter(rec => rec.stepLabel).map(rec => ({
        id: rec.id,
        step: rec.stepLabel!,
        time: new Date(rec.createdAt).toLocaleString(),
        endpoint: rec.endpoint,
        modelName: rec.modelName,
      })))
      setTokenStats(r.data.filter(rec => !rec.stepLabel).map(rec => ({
        id: rec.id,
        chapterId: rec.chapterId,
        role: rec.agentRole,
        prompt: rec.promptTokens,
        completion: rec.completionTokens,
        time: new Date(rec.createdAt).toLocaleString(),
        endpoint: rec.endpoint,
        modelName: rec.modelName,
        failed: rec.failed ?? false,
        failureReason: rec.failureReason ?? null,
        persisted: true,
      })))
    }), [bookId])

  useEffect(() => {
    const controller = new AbortController()
    const signal = controller.signal

    refreshBook()
    getMessages(bookId, signal).then(r => { if (!signal.aborted) setMessages(r.data) }).catch(() => {})
    refreshTokenStats(signal).catch(() => {})
    getStoryBible(bookId, signal).then(r => { if (!signal.aborted) setStoryBible(r.data) }).catch(() => {})
    getCharacters(bookId, true, signal).then(r => { if (!signal.aborted) setCharacters(r.data) }).catch(() => {})
    getPlotThreads(bookId, true, signal).then(r => { if (!signal.aborted) setPlotThreads(r.data) }).catch(() => {})
    getIllustrations(bookId, undefined, signal).then(r => { if (!signal.aborted) setIllustrations(r.data) }).catch(() => {})
    getClarifications(bookId, true, signal).then(r => { if (!signal.aborted) setClarifications(r.data) }).catch(() => {})

    return () => { controller.abort() }
  }, [refreshBook, refreshTokenStats, bookId])

  // Routes streamed content to the buffer of the page that shows it.
  const writeStream = useCallback((agentRole: string, cId: number | null, update: (prev: string) => string) => {
    if (agentRole === 'BookChat') {
      setBookChatStream(update)
    } else if (agentRole === 'StoryBibleAgent') {
      setStoryBibleStream(update)
    } else if (agentRole === 'CharactersAgent') {
      setCharactersStream(update)
    } else if (agentRole === 'PlotThreadsAgent') {
      setPlotThreadsStream(update)
    } else if (agentRole === 'ContinuityChecker') {
      // Checker output is structural analysis saved to chat — never display in chapter view
    } else if (agentRole === 'ChaptersAgent' || cId === null) {
      setPlannerBuffer(update)
    } else {
      setStreamingChapterId(cId)
      setStreamBuffer(update)
    }
  }, [])

  // Tokens received while a status request is in flight. The server's snapshot may or may not
  // include them, so they are merged onto it by overlap instead of being dropped or doubled.
  const liveCaptures = useRef(new Set<LiveToken[]>())
  const statusRequestSeq = useRef(0)

  const clearStreams = useCallback(() => {
    setPlannerBuffer('')
    setStreamBuffer('')
    setStreamingChapterId(null)
    setStoryBibleStream('')
    setCharactersStream('')
    setPlotThreadsStream('')
    setBookChatStream('')
  }, [])

  /**
   * Brings the run state up to date from the server: status, current step, pending question and the
   * content streamed so far. Runs on load and after every (re)join of the hub group, so a refreshed
   * page or a dropped connection picks up exactly where the live events left off.
   */
  const syncRunState = useCallback(async () => {
    const seq = ++statusRequestSeq.current
    const captured: LiveToken[] = []
    liveCaptures.current.add(captured)
    try {
      const status = (await getAgentStatus(bookId)).data as AgentRunStatus | '' | null
      if (seq !== statusRequestSeq.current) return
      // Only restore active states — terminal states (Done/Failed/Cancelled) from a
      // previous run should not show the spinner or block the action buttons.
      if (!status || (status.state !== 'Running' && status.state !== 'WaitingForInput')) {
        setRunStatus(null)
        setPendingQuestion(null)
        clearStreams()
        return
      }
      setRunStatus({ role: status.role, state: status.state, chapterId: status.chapterId ?? undefined })
      const step = status.step
      if (step) setWorkflowLog(prev => prev[prev.length - 1] === step ? prev : [...prev, step])
      const stream = status.stream
      if (stream) {
        const tail = captured
          .filter(t => t.agentRole === stream.agentRole && t.chapterId === stream.chapterId)
          .map(t => t.token).join('')
        const merged = stream.content + tail.slice(overlapLength(stream.content, tail))
        writeStream(stream.agentRole, stream.chapterId, () => merged)
      }
      if (status.state === 'WaitingForInput') {
        getMessages(bookId).then(m => {
          const q = [...m.data].reverse().find(msg => msg.messageType === 'Question' && !msg.isResolved)
          if (q) setPendingQuestion(q)
        }).catch(() => {})
      }
    } catch {
      // Keep the current state; the next join retries.
    } finally {
      liveCaptures.current.delete(captured)
    }
  }, [bookId, clearStreams, writeStream])

  useEffect(() => { void syncRunState() }, [syncRunState])

  // ── Book chat (Interactive books) ─────────────────────────────────────────────
  // A chat change may have changed the book (applied proposals), so the book's parts are re-read too.
  useEffect(() => {
    setOnBookChatUpdated(() => {
      setBookChatVersion(v => v + 1)
      setBookChatStream('')
      refreshBook().catch(() => {})
      getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
      getCharacters(bookId, true).then(r => setCharacters(r.data)).catch(() => {})
      getPlotThreads(bookId, true).then(r => setPlotThreads(r.data)).catch(() => {})
    })
  }, [setOnBookChatUpdated, refreshBook, bookId])

  useEffect(() => {
    setOnStream((_bId, cId, agentRole, token) => {
      for (const captured of liveCaptures.current) captured.push({ agentRole, chapterId: cId, token })
      writeStream(agentRole, cId, prev => prev + token)
    })
    setOnQuestion((_bId, msg) => {
      const m = msg as AgentMessage
      setPendingQuestion(m)
      setMessages(prev => [...prev, m])
      const where = `/books/${bookId}/chat`
      navigate(where)
      notifyRef.current('ABook — Input needed', {
        body: m.content,
        tag: `question-${bookId}`,
        onClick: () => navigate(where),
      })
    })
    setOnStatus((_bId, role, state, cId) => {
      if (state === 'Failed' || state === 'Cancelled') {
        // Whole run aborted — clear everything immediately
        setRunStatus(null)
        clearStreams()
        refreshBook()
        refreshMessages()
      } else if (state === 'Running' || state === 'WaitingForInput') {
        // Transition to a new agent step — update role/state/chapter but keep the run active.
        setRunStatus({ role, state, chapterId: cId ?? undefined })
        // The planner waits for its clarifying questions on the Clarifications page, not in the chat.
        if (state === 'WaitingForInput' && role === 'Planner') {
          refreshClarifications().then(list => {
            const open = (list ?? []).filter(c => c.isOpen)
            if (open.length === 0) return
            navigate(`/books/${bookId}/clarifications`)
            notifyRef.current('ABook — Input needed', {
              body: `The planner has ${open.length} question${open.length === 1 ? '' : 's'} for you.`,
              tag: `clarifications-${bookId}`,
              onClick: () => navigate(`/books/${bookId}/clarifications`),
            })
          })
        } else if (state === 'Running' && role === 'Planner') {
          // Resumed after the last answer — the round's status has changed.
          refreshBook()
        }
      } else if (state === 'Done') {
        // An individual agent phase finished but the workflow may still be running.
        // Do NOT clear runStatus here — WorkflowProgress(isComplete=true) is the
        // authoritative "run finished" signal.
        // Clear the phase stream immediately and reload only the part that changed.
        if (role === 'StoryBibleAgent') {
          setStoryBibleStream('')
          setBook(prev => prev ? { ...prev, storyBibleStatus: 'Complete' } : prev)
          getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
        } else if (role === 'CharactersAgent') {
          setCharactersStream('')
          setBook(prev => prev ? { ...prev, charactersStatus: 'Complete' } : prev)
          getCharacters(bookId, true).then(r => setCharacters(r.data)).catch(() => {})
        } else if (role === 'PlotThreadsAgent') {
          setPlotThreadsStream('')
          setBook(prev => prev ? { ...prev, plotThreadsStatus: 'Complete' } : prev)
          getPlotThreads(bookId, true).then(r => setPlotThreads(r.data)).catch(() => {})
        } else if (role === 'ChaptersAgent') {
          setPlannerBuffer('')
          setBook(prev => prev ? { ...prev, chaptersStatus: 'Complete' } : prev)
          getChapters(bookId).then(r => setBook(prev => prev ? { ...prev, chapters: r.data } : prev))
        } else {
          // Writer/Editor/ContinuityChecker Done: patch only the affected chapter.
          // Fall back to reloading all chapters for full-manuscript continuity check (cId=null).
          if (cId !== null) {
            getChapter(bookId, cId)
              .then(r => setBook(prev => prev
                ? { ...prev, chapters: prev.chapters?.map(ch => ch.id === cId ? r.data : ch) }
                : prev
              ))
              .catch(() => {})
          } else {
            getChapters(bookId).then(r => setBook(prev => prev ? { ...prev, chapters: r.data } : prev))
          }
          refreshMessages()
        }
      }
    })
    setOnChapterUpdated((_bId, cId) => {
      setStreamBuffer('')
      setStreamingChapterId(null)
      // Reload only the single chapter whose outline/content changed.
      getChapter(bookId, cId)
        .then(r => setBook(prev => prev
          ? { ...prev, chapters: prev.chapters?.map(ch => ch.id === cId ? r.data : ch) }
          : prev
        ))
        .catch(() => {})
    })
    setOnWorkflowProgress((_bId, step, isComplete) => {
      setWorkflowLog(prev => [...prev, step])
      setWorkflowSteps(prev => [...prev, { id: Date.now(), step, time: new Date().toLocaleTimeString() }])
      if (isComplete) {
        notifyRef.current('ABook — Workflow complete', {
          body: step,
          tag: `complete-${bookId}`,
          onClick: () => navigate(`/books/${bookId}/chat`),
        })
        // Clear all live-streaming data immediately except plannerBuffer.
        // plannerBuffer is kept until refreshBook resolves so the chapter
        // streaming preview stays visible while persisted data loads from the
        // server — preventing a flash of the "No chapters yet" empty state.
        setStreamBuffer('')
        setStreamingChapterId(null)
        setStoryBibleStream('')
        setCharactersStream('')
        setPlotThreadsStream('')
        // After DB data arrives, clear the planning preview and unblock the UI.
        // The .catch branch ensures runStatus is always cleared even on network errors.
        refreshBook().then(
          () => { setPlannerBuffer(''); setRunStatus(null) },
          () => { setPlannerBuffer(''); setRunStatus(null) }
        )
        refreshMessages()
      }
      if (step.includes('Phase 2/4')) {
        setStoryBibleStream('')
        getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
      } else if (step.includes('Phase 3/4')) {
        setCharactersStream('')
        getCharacters(bookId, true).then(r => setCharacters(r.data)).catch(() => {})
      } else if (step.includes('Phase 4/4')) {
        setPlotThreadsStream('')
        getPlotThreads(bookId, true).then(r => setPlotThreads(r.data)).catch(() => {})
      }
    })
    setOnTokenStats((_bId, cId, role, prompt, completion) => {
      refreshTokenStats().catch(() => {
        setTokenStats(prev => [...prev, {
          id: Date.now(), chapterId: cId, role, prompt, completion,
          time: new Date().toLocaleTimeString()
        }])
      })
    })
    setOnAgentError((_bId, _role, errorMessage) => {
      refreshMessages()
      navigate(`/books/${bookId}/chat`)
      notifyRef.current('ABook — Agent error', {
        body: errorMessage,
        tag: `error-${bookId}`,
        onClick: () => navigate(`/books/${bookId}/chat`),
      })
    })
    setOnMessagesUpdated(() => {
      refreshMessages()
      // The planner posts a note after saving its questions; answers from another tab land here too.
      refreshClarifications()
    })
    setOnIllustrationsUpdated((_bId, illustrationId) => {
      refreshIllustrations()
      // A bulk change (planning) may also have rewritten the characters' visual descriptions or the art direction.
      if (illustrationId === null) {
        getCharacters(bookId, true).then(r => setCharacters(r.data)).catch(() => {})
        getBook(bookId).then(r => setBook(prev => prev ? {
          ...prev,
          illustrationStyle: r.data.illustrationStyle,
          illustrationPortraitFraming: r.data.illustrationPortraitFraming,
          illustrationNegativePrompt: r.data.illustrationNegativePrompt,
        } : prev)).catch(() => {})
      }
    })
    setOnReconnected(() => {
      // Only called when the events missed while disconnected could not be replayed (server restart,
      // gap too old) — re-read everything they would have updated.
      refreshMessages()
      refreshBook().catch(() => {})
      refreshTokenStats().catch(() => {})
      refreshIllustrations()
      refreshClarifications()
      // Run status and streams are re-read by the join handler.
    })
    setOnJoined(() => { void syncRunState() })
  }, [setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError, setOnMessagesUpdated, setOnIllustrationsUpdated, setOnReconnected, setOnJoined, refreshBook, refreshMessages, refreshTokenStats, refreshIllustrations, refreshClarifications, clearStreams, writeStream, syncRunState, bookId, navigate])

  // A rewind may have changed every part of the book: re-read all of it.
  useEffect(() => {
    setOnBookRewound(() => {
      setBookChatVersion(v => v + 1)
      refreshBook().catch(() => {})
      getChapters(bookId).then(r => setBook(prev => prev ? { ...prev, chapters: r.data } : prev)).catch(() => {})
      getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => setStoryBible(null))
      getCharacters(bookId, true).then(r => setCharacters(r.data)).catch(() => {})
      getPlotThreads(bookId, true).then(r => setPlotThreads(r.data)).catch(() => {})
      refreshMessages()
      refreshClarifications()
      refreshIllustrations()
    })
  }, [setOnBookRewound, refreshBook, refreshMessages, refreshClarifications, refreshIllustrations, bookId])

  const isWaitingForInput = runStatus?.state === 'WaitingForInput'
  const isRunning = runStatus?.state === 'Running' || isWaitingForInput
  // While an agent waits for an answer nothing is being generated, so the author is free to
  // revise the material the next step will read.
  const canEdit = !isRunning || isWaitingForInput

  const startAgentRun = useCallback(async (apiCall: () => Promise<unknown>, initialRole: string) => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await apiCall()
      setRunStatus({ role: initialRole, state: 'Running' })
    } catch (err: unknown) {
      const msg = err as { response?: { status?: number } }
      if (msg?.response?.status === 409) alert('An agent is already running for this book.')
    }
  }, [isRunning])

  const handleWriteBook = useCallback(() => startAgentRun(() => startWorkflow(bookId), 'Planner'), [startAgentRun, bookId])
  const handlePlanBook = useCallback(() => startAgentRun(() => startPlanning(bookId), 'Planner'), [startAgentRun, bookId])

  // Unlike startAgentRun these report every refusal (no text yet, no image endpoint), not only 409.
  const startIllustrationRun = useCallback(async (apiCall: () => Promise<unknown>, role: string) => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await apiCall()
      setRunStatus({ role, state: 'Running' })
    } catch (err: unknown) {
      alert(axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err))
    }
  }, [isRunning])

  const handlePlanIllustrations = useCallback((scope: IllustrationPlanScope, targetId?: number) =>
    startIllustrationRun(() => planIllustrations(bookId, scope, targetId), 'IllustrationPlanner'), [startIllustrationRun, bookId])

  const handleRenderIllustrations = useCallback((scope: RenderScope, targetId?: number, reroll = false) =>
    startIllustrationRun(() => renderIllustrations(bookId, scope, targetId, reroll), 'Illustrator'), [startIllustrationRun, bookId])

  const handleStop = async () => {
    try { await stopWorkflow(bookId) } catch { /* ignore */ }
  }

  const phaseStatusKey: Record<string, keyof Book> = {
    clarifications: 'clarificationsStatus',
    storybible: 'storyBibleStatus',
    characters: 'charactersStatus',
    plotthreads: 'plotThreadsStatus',
    chapters: 'chaptersStatus',
  }

  const handleCompletePhase = async (phase: string) => {
    await completePlanningPhase(bookId, phase)
    const key = phaseStatusKey[phase]
    if (key) setBook(prev => prev ? { ...prev, [key]: 'Complete' } : prev)
  }

  const handleReopenPhase = async (phase: string) => {
    await reopenPlanningPhase(bookId, phase)
    const key = phaseStatusKey[phase]
    if (key) setBook(prev => prev ? { ...prev, [key]: 'NotStarted' } : prev)
  }

  const handleClearPhase = async (phase: string, clearLocal: () => void) => {
    const labels: Record<string, string> = { clarifications: 'Clarifications', storybible: 'Story Bible', characters: 'Characters', plotthreads: 'Plot Threads', chapters: 'Chapter Outlines' }
    const prompt = phase === 'clarifications'
      ? 'Archive all clarifications and reopen the round? The next planning run will ask its questions again.'
      : `Clear all ${labels[phase] ?? phase} data? This cannot be undone.`
    if (!confirm(prompt)) return
    await clearPlanningPhase(bookId, phase)
    clearLocal()
    const key = phaseStatusKey[phase]
    if (key) setBook(prev => prev ? { ...prev, [key]: 'NotStarted' } : prev)
  }

  // `text` overrides the typed answer — Skip passes '' (setAnswerText('') would not be visible yet here).
  const handleAnswer = async (text: string = answerText) => {
    if (!pendingQuestion) return
    const answer = text.trim()
    if (!pendingQuestion.isOptional && !answer) return
    if (submittingAnswer) return
    setSubmittingAnswer(true)
    setAnswerError(null)
    try {
      await postAnswer(bookId, pendingQuestion.id, answer)
      setAnswerText('')
      setPendingQuestion(null)
      refreshMessages()
    } catch (err) {
      // Keep the question and the typed text so the author can retry.
      const message = axios.isAxiosError(err) ? (err.response?.data?.message ?? err.message) : String(err)
      setAnswerError(`Could not send the answer: ${message}`)
    } finally {
      setSubmittingAnswer(false)
    }
  }

  const isPhaseComplete = (phase: string) => {
    const key = phaseStatusKey[phase]
    return key ? book?.[key] === 'Complete' : false
  }

  const clearMessagesForBook = async () => {
    if (!confirm('Clear all agent messages for this book?')) return
    await clearMessages(bookId)
    setMessages([])
  }

  const clearTokenUsageForBook = async () => {
    if (!confirm('Clear all token usage stats for this book?')) return
    await clearTokenUsage(bookId)
    setTokenStats([])
    setWorkflowSteps([])
  }

  const value: BookContextValue = {
    book, setBook, messages, setMessages, pendingQuestion, setPendingQuestion,
    runStatus, setRunStatus, isRunning, isWaitingForInput, canEdit,
    plannerBuffer, setPlannerBuffer, streamBuffer, streamingChapterId,
    storyBibleStream, setStoryBibleStream, charactersStream, setCharactersStream,
    plotThreadsStream, setPlotThreadsStream, setStreamBuffer, setStreamingChapterId,
    storyBible, setStoryBible, characters, setCharacters, plotThreads, setPlotThreads,
    illustrations, refreshIllustrations, handlePlanIllustrations, handleRenderIllustrations,
    clarifications, setClarifications, refreshClarifications,
    openClarificationCount: clarifications.filter(c => c.isOpen).length,
    tokenStats, setTokenStats, workflowSteps, setWorkflowSteps, workflowLog,
    answerText, setAnswerText, submittingAnswer, answerError,
    refreshBook, refreshMessages,
    handleAnswer,
    bookChatStream, bookChatVersion,
    handleWriteBook, handlePlanBook, handleStop,
    handleCompletePhase, handleReopenPhase, handleClearPhase, isPhaseComplete,
    clearMessagesForBook, clearTokenUsageForBook,
  }

  return <BookContext.Provider value={value}>{children}</BookContext.Provider>
}
