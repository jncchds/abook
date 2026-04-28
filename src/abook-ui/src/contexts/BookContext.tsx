import { createContext, useContext, useEffect, useState, useCallback, useRef, type ReactNode } from 'react'
import { useNavigate } from 'react-router-dom'
import type { Book, AgentMessage, AgentRunStatus, StoryBible, CharacterCard, PlotThread } from '../api'
import {
  getBook, getMessages, postAnswer, getAgentStatus,
  startPlanning, continuePlanning, completePlanningPhase, reopenPlanningPhase, clearPlanningPhase,
  startWorkflow, continueWorkflow, stopWorkflow,
  getTokenUsage, clearMessages, clearTokenUsage,
  getStoryBible, getCharacters, getPlotThreads,
} from '../api'
import { useBookHub } from '../hooks/useBookHub'

export interface TokenStat {
  id: number
  chapterId: number | null
  role: string
  prompt: number
  completion: number
  time: string
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

  plannerBuffer: string
  streamBuffer: string
  streamingChapterId: number | null
  storyBibleStream: string
  charactersStream: string
  plotThreadsStream: string

  storyBible: StoryBible | null
  setStoryBible: React.Dispatch<React.SetStateAction<StoryBible | null>>
  characters: CharacterCard[]
  setCharacters: React.Dispatch<React.SetStateAction<CharacterCard[]>>
  plotThreads: PlotThread[]
  setPlotThreads: React.Dispatch<React.SetStateAction<PlotThread[]>>

  tokenStats: TokenStat[]
  setTokenStats: React.Dispatch<React.SetStateAction<TokenStat[]>>
  workflowSteps: WorkflowStep[]
  setWorkflowSteps: React.Dispatch<React.SetStateAction<WorkflowStep[]>>
  workflowLog: string[]

  answerText: string
  setAnswerText: React.Dispatch<React.SetStateAction<string>>

  refreshBook: () => Promise<void>
  refreshMessages: () => void

  handleAnswer: () => Promise<void>
  handleWriteBook: () => Promise<void>
  handlePlanBook: () => Promise<void>
  handleStop: () => Promise<void>
  handleContinue: () => Promise<void>
  handleCompletePhase: (phase: string) => Promise<void>
  handleReopenPhase: (phase: string) => Promise<void>
  handleClearPhase: (phase: string, clearLocal: () => void) => Promise<void>
  isPhaseComplete: (phase: string) => boolean

  clearMessagesForBook: () => Promise<void>
  clearTokenUsageForBook: () => Promise<void>
}

const BookContext = createContext<BookContextValue | null>(null)

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

  const [storyBible, setStoryBible] = useState<StoryBible | null>(null)
  const [characters, setCharacters] = useState<CharacterCard[]>([])
  const [plotThreads, setPlotThreads] = useState<PlotThread[]>([])

  const [tokenStats, setTokenStats] = useState<TokenStat[]>([])
  const [workflowSteps, setWorkflowSteps] = useState<WorkflowStep[]>([])
  const [workflowLog, setWorkflowLog] = useState<string[]>([])

  const [answerText, setAnswerText] = useState('')

  const chatBottomRef = useRef<null>(null)
  void chatBottomRef

  const { setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError } = useBookHub(bookId)

  const refreshBook = useCallback(() =>
    getBook(bookId).then(r => setBook(r.data)), [bookId])

  const refreshMessages = useCallback(() => {
    getMessages(bookId).then(r => setMessages(r.data)).catch(() => {})
  }, [bookId])

  useEffect(() => {
    refreshBook()
    Promise.all([
      getMessages(bookId),
      getAgentStatus(bookId).catch(() => ({ data: null as AgentRunStatus | null }))
    ]).then(([msgRes, statusRes]) => {
      setMessages(msgRes.data)
      if (statusRes.data) setRunStatus(statusRes.data)
      if (statusRes.data?.state === 'WaitingForInput') {
        const q = [...msgRes.data].reverse().find(m => m.messageType === 'Question' && !m.isResolved)
        if (q) setPendingQuestion(q)
      }
    }).catch(() => {})
    getTokenUsage(bookId).then(r => {
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
        persisted: true,
      })))
    }).catch(() => {})
    getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
    getCharacters(bookId).then(r => setCharacters(r.data)).catch(() => {})
    getPlotThreads(bookId).then(r => setPlotThreads(r.data)).catch(() => {})
  }, [refreshBook, bookId])

  const clearStreams = useCallback(() => {
    setPlannerBuffer('')
    setStreamBuffer('')
    setStreamingChapterId(null)
    setStoryBibleStream('')
    setCharactersStream('')
    setPlotThreadsStream('')
  }, [])

  useEffect(() => {
    setOnStream((_bId, cId, agentRole, token) => {
      if (agentRole === 'StoryBibleAgent') {
        setStoryBibleStream(prev => prev + token)
      } else if (agentRole === 'CharactersAgent') {
        setCharactersStream(prev => prev + token)
      } else if (agentRole === 'PlotThreadsAgent') {
        setPlotThreadsStream(prev => prev + token)
      } else if (cId === null) {
        setPlannerBuffer(prev => prev + token)
      } else {
        setStreamingChapterId(cId)
        setStreamBuffer(prev => prev + token)
      }
    })
    setOnQuestion((_bId, msg) => {
      const m = msg as AgentMessage
      setPendingQuestion(m)
      setMessages(prev => [...prev, m])
      navigate(`/books/${bookId}/chat`)
    })
    setOnStatus((_bId, role, state) => {
      setRunStatus(state === 'Done' || state === 'Failed' || state === 'Cancelled' ? null : { role, state, chapterId: undefined })
      if (state === 'Done' || state === 'Failed' || state === 'Cancelled') {
        clearStreams()
        refreshBook()
        refreshMessages()
      }
    })
    setOnChapterUpdated((_bId, cId) => {
      setStreamBuffer('')
      setStreamingChapterId(null)
      getBook(bookId).then(r => setBook(r.data))
      getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
      getCharacters(bookId).then(r => setCharacters(r.data)).catch(() => {})
      getPlotThreads(bookId).then(r => setPlotThreads(r.data)).catch(() => {})
      void cId
    })
    setOnWorkflowProgress((_bId, step, isComplete) => {
      setWorkflowLog(prev => [...prev, step])
      setWorkflowSteps(prev => [...prev, { id: Date.now(), step, time: new Date().toLocaleTimeString() }])
      if (isComplete) {
        setRunStatus(null)
        clearStreams()
        refreshBook()
        refreshMessages()
      }
      if (step.includes('Phase 2/4')) {
        setStoryBibleStream('')
        getStoryBible(bookId).then(r => setStoryBible(r.data)).catch(() => {})
      } else if (step.includes('Phase 3/4')) {
        setCharactersStream('')
        getCharacters(bookId).then(r => setCharacters(r.data)).catch(() => {})
      } else if (step.includes('Phase 4/4')) {
        setPlotThreadsStream('')
        getPlotThreads(bookId).then(r => setPlotThreads(r.data)).catch(() => {})
      }
    })
    setOnTokenStats((_bId, cId, role, prompt, completion) => {
      getTokenUsage(bookId).then(r => {
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
          persisted: true,
        })))
      }).catch(() => {
        setTokenStats(prev => [...prev, {
          id: Date.now(), chapterId: cId, role, prompt, completion,
          time: new Date().toLocaleTimeString()
        }])
      })
    })
    setOnAgentError(() => {
      refreshMessages()
      navigate(`/books/${bookId}/chat`)
    })
  }, [setOnStream, setOnQuestion, setOnStatus, setOnChapterUpdated, setOnWorkflowProgress, setOnTokenStats, setOnAgentError, refreshBook, refreshMessages, clearStreams, bookId, navigate])

  const isRunning = runStatus?.state === 'Running' || runStatus?.state === 'WaitingForInput'

  const handleWriteBook = async () => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await startWorkflow(bookId)
      setRunStatus({ role: 'Planner', state: 'Running' })
    } catch (err: unknown) {
      const msg = err as { response?: { status?: number } }
      if (msg?.response?.status === 409) alert('An agent is already running for this book.')
    }
  }

  const handlePlanBook = async () => {
    if (isRunning) return
    setWorkflowLog([])
    try {
      await startPlanning(bookId)
      setRunStatus({ role: 'Planner', state: 'Running' })
    } catch (err: unknown) {
      const msg = err as { response?: { status?: number } }
      if (msg?.response?.status === 409) alert('An agent is already running for this book.')
    }
  }

  const handleStop = async () => {
    try { await stopWorkflow(bookId) } catch { /* ignore */ }
  }

  const handleContinue = async () => {
    if (isRunning || !book) return
    setWorkflowLog([])
    const allPlanningComplete =
      book.storyBibleStatus === 'Complete' && book.charactersStatus === 'Complete' &&
      book.plotThreadsStatus === 'Complete' && book.chaptersStatus === 'Complete'
    const anyPlanningComplete =
      book.storyBibleStatus === 'Complete' || book.charactersStatus === 'Complete' ||
      book.plotThreadsStatus === 'Complete' || book.chaptersStatus === 'Complete'
    try {
      if (!allPlanningComplete && anyPlanningComplete) {
        await continuePlanning(bookId)
        setRunStatus({ role: 'Planner', state: 'Running' })
      } else {
        await continueWorkflow(bookId)
        setRunStatus({ role: 'Writer', state: 'Running' })
      }
    } catch (err: unknown) {
      const msg = err as { response?: { status?: number } }
      if (msg?.response?.status === 409) alert('An agent is already running for this book.')
    }
  }

  const handleCompletePhase = async (phase: string) => {
    await completePlanningPhase(bookId, phase)
    await refreshBook()
  }

  const handleReopenPhase = async (phase: string) => {
    await reopenPlanningPhase(bookId, phase)
    await refreshBook()
  }

  const handleClearPhase = async (phase: string, clearLocal: () => void) => {
    const labels: Record<string, string> = { storybible: 'Story Bible', characters: 'Characters', plotthreads: 'Plot Threads', chapters: 'Chapter Outlines' }
    if (!confirm(`Clear all ${labels[phase] ?? phase} data? This cannot be undone.`)) return
    await clearPlanningPhase(bookId, phase)
    clearLocal()
    await refreshBook()
  }

  const handleAnswer = async () => {
    if (!pendingQuestion || !answerText.trim()) return
    await postAnswer(bookId, pendingQuestion.id, answerText.trim())
    setAnswerText('')
    setPendingQuestion(null)
    refreshMessages()
  }

  const isPhaseComplete = (phase: string) => {
    switch (phase) {
      case 'storybible':  return book?.storyBibleStatus  === 'Complete'
      case 'characters':  return book?.charactersStatus  === 'Complete'
      case 'plotthreads': return book?.plotThreadsStatus === 'Complete'
      case 'chapters':    return book?.chaptersStatus    === 'Complete'
      default: return false
    }
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
    runStatus, setRunStatus, isRunning,
    plannerBuffer, streamBuffer, streamingChapterId,
    storyBibleStream, charactersStream, plotThreadsStream,
    storyBible, setStoryBible, characters, setCharacters, plotThreads, setPlotThreads,
    tokenStats, setTokenStats, workflowSteps, setWorkflowSteps, workflowLog,
    answerText, setAnswerText,
    refreshBook, refreshMessages,
    handleAnswer, handleWriteBook, handlePlanBook, handleStop, handleContinue,
    handleCompletePhase, handleReopenPhase, handleClearPhase, isPhaseComplete,
    clearMessagesForBook, clearTokenUsageForBook,
  }

  return <BookContext.Provider value={value}>{children}</BookContext.Provider>
}
