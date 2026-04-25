import axios from 'axios'

const api = axios.create({ baseURL: '/api', withCredentials: true })

export interface Book {
  id: number
  title: string
  premise: string
  genre: string
  targetChapterCount: number
  status: string
  language: string
  storyBibleSystemPrompt?: string
  charactersSystemPrompt?: string
  plotThreadsSystemPrompt?: string
  chapterOutlinesSystemPrompt?: string
  writerSystemPrompt?: string
  editorSystemPrompt?: string
  continuityCheckerSystemPrompt?: string
  storyBibleStatus?: string
  charactersStatus?: string
  plotThreadsStatus?: string
  chaptersStatus?: string
  createdAt: string
  updatedAt: string
  chapters?: Chapter[]
}

export interface Chapter {
  id: number
  bookId: number
  number: number
  title: string
  outline: string
  content: string
  status: string
  povCharacter: string
  charactersInvolvedJson: string
  plotThreadsJson: string
  foreshadowingNotes: string
  payoffNotes: string
  createdAt: string
  updatedAt: string
}

export interface AgentMessage {
  id: number
  bookId: number
  chapterId?: number
  agentRole: string
  messageType: string
  content: string
  isResolved: boolean
  createdAt: string
}

export interface LlmConfig {
  id?: number
  bookId?: number
  provider: string
  modelName: string
  endpoint: string
  apiKey?: string
  embeddingModelName?: string
}

export interface AgentRunStatus {
  role: string
  state: string
  chapterId?: number
}

export interface OllamaModel {
  name: string
  size: number
}

export interface AppUser {
  id: number
  username: string
  isAdmin: boolean
  createdAt?: string
}

// Auth
export const authSetup = () => api.get<{ needsSetup: boolean }>('/auth/setup')
export const authLogin = (username: string, password: string) =>
  api.post<AppUser>('/auth/login', { username, password })
export const authRegister = (username: string, password: string) =>
  api.post<AppUser>('/auth/register', { username, password })
export const authLogout = () => api.post('/auth/logout')
export const authMe = () => api.get<AppUser>('/auth/me')

// Users (admin)
export const getUsers = () => api.get<AppUser[]>('/users')
export const createUser = (username: string, password: string, isAdmin: boolean) =>
  api.post<AppUser>('/users', { username, password, isAdmin })
export const changePassword = (userId: number, newPassword: string) =>
  api.put(`/users/${userId}/password`, { newPassword })
export const changeRole = (userId: number, isAdmin: boolean) =>
  api.put(`/users/${userId}/role`, { isAdmin })

// Books
export const getBooks = () => api.get<Book[]>('/books')
export const getBook = (id: number) => api.get<Book>(`/books/${id}`)
export const createBook = (data: Omit<Book, 'id' | 'status' | 'createdAt' | 'updatedAt' | 'chapters'>) =>
  api.post<Book>('/books', data)
export const updateBook = (id: number, data: Partial<Book>) => api.put<Book>(`/books/${id}`, data)
export const deleteBook = (id: number) => api.delete(`/books/${id}`)

export interface DefaultPrompts {
  storyBibleSystemPrompt: string
  charactersSystemPrompt: string
  plotThreadsSystemPrompt: string
  chapterOutlinesSystemPrompt: string
  writerSystemPrompt: string
  editorSystemPrompt: string
  continuityCheckerSystemPrompt: string
}
export const getDefaultPrompts = (bookId: number) =>
  api.get<DefaultPrompts>(`/books/${bookId}/default-prompts`)

// Chapters
export const getChapters = (bookId: number) => api.get<Chapter[]>(`/books/${bookId}/chapters`)
export const getChapter = (bookId: number, chapterId: number) =>
  api.get<Chapter>(`/books/${bookId}/chapters/${chapterId}`)
export const createChapter = (bookId: number, data: { number: number; title: string; outline: string }) =>
  api.post<Chapter>(`/books/${bookId}/chapters`, data)
export const updateChapter = (bookId: number, chapterId: number, data: Partial<Chapter>) =>
  api.put<Chapter>(`/books/${bookId}/chapters/${chapterId}`, data)
export const clearChapterContent = (bookId: number, chapter: Chapter) =>
  api.put<Chapter>(`/books/${bookId}/chapters/${chapter.id}`, {
    title: chapter.title,
    outline: chapter.outline,
    content: '',
    status: 'Outlined',
  })

// Messages
export const getMessages = (bookId: number, chapterId?: number) =>
  api.get<AgentMessage[]>(`/books/${bookId}/messages`, { params: { chapterId } })
export const clearMessages = (bookId: number) =>
  api.delete(`/books/${bookId}/messages`)
export const postAnswer = (bookId: number, messageId: number, answer: string) =>
  api.post(`/books/${bookId}/messages/answer`, { messageId, answer })

// Agent actions
export const startPlanning = (bookId: number) => api.post(`/books/${bookId}/agent/plan`)
export const continuePlanning = (bookId: number) => api.post(`/books/${bookId}/agent/plan/continue`)

// Planning phases
export const completePlanningPhase = (bookId: number, phase: string) =>
  api.post(`/books/${bookId}/planning-phases/${phase}/complete`)
export const reopenPlanningPhase = (bookId: number, phase: string) =>
  api.post(`/books/${bookId}/planning-phases/${phase}/reopen`)
export const clearPlanningPhase = (bookId: number, phase: string) =>
  api.delete(`/books/${bookId}/planning-phases/${phase}`)
export const startWriting = (bookId: number, chapterId: number) =>
  api.post(`/books/${bookId}/agent/write/${chapterId}`)
export const startEditing = (bookId: number, chapterId: number) =>
  api.post(`/books/${bookId}/agent/edit/${chapterId}`)
export const startContinuityCheck = (bookId: number) => api.post(`/books/${bookId}/agent/continuity`)
export const startWorkflow = (bookId: number) => api.post(`/books/${bookId}/agent/workflow/start`)
export const continueWorkflow = (bookId: number) => api.post(`/books/${bookId}/agent/workflow/continue`)
export const stopWorkflow = (bookId: number) => api.post(`/books/${bookId}/agent/workflow/stop`)
export const getAgentStatus = (bookId: number) =>
  api.get<AgentRunStatus>(`/books/${bookId}/agent/status`)

// Token usage
export interface TokenUsageRecord {
  id: number
  chapterId: number | null
  agentRole: string
  promptTokens: number
  completionTokens: number
  createdAt: string
}
export const getTokenUsage = (bookId: number) =>
  api.get<TokenUsageRecord[]>(`/books/${bookId}/token-usage`)
export const clearTokenUsage = (bookId: number) =>
  api.delete(`/books/${bookId}/token-usage`)

// LLM Config
export const getLlmConfig = (bookId?: number) =>
  api.get<LlmConfig>('/configuration', { params: { bookId } })
export const updateLlmConfig = (data: LlmConfig) => api.put<LlmConfig>('/configuration', data)

// Ollama
export const getOllamaModels = (endpoint?: string, provider?: string) => {
  const params = new URLSearchParams()
  if (endpoint) params.set('endpoint', endpoint)
  if (provider) params.set('provider', provider)
  const qs = params.toString()
  return api.get<OllamaModel[]>('/ollama/models' + (qs ? `?${qs}` : ''))
}

// Story Bible
export interface StoryBible {
  id?: number
  bookId: number
  settingDescription: string
  timePeriod: string
  themes: string
  toneAndStyle: string
  worldRules: string
  notes: string
  createdAt?: string
  updatedAt?: string
}
export const getStoryBible = (bookId: number) =>
  api.get<StoryBible>(`/books/${bookId}/story-bible`)
export const updateStoryBible = (bookId: number, data: Omit<StoryBible, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.put<StoryBible>(`/books/${bookId}/story-bible`, data)

// Character Cards
export interface CharacterCard {
  id: number
  bookId: number
  name: string
  role: string
  physicalDescription: string
  personality: string
  backstory: string
  goalMotivation: string
  arc: string
  firstAppearanceChapterNumber?: number | null
  notes: string
  createdAt?: string
  updatedAt?: string
}
export const getCharacters = (bookId: number) =>
  api.get<CharacterCard[]>(`/books/${bookId}/characters`)
export const createCharacter = (bookId: number, data: Omit<CharacterCard, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.post<CharacterCard>(`/books/${bookId}/characters`, data)
export const updateCharacter = (bookId: number, cardId: number, data: Omit<CharacterCard, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.put<CharacterCard>(`/books/${bookId}/characters/${cardId}`, data)
export const deleteCharacter = (bookId: number, cardId: number) =>
  api.delete(`/books/${bookId}/characters/${cardId}`)

// Plot Threads
export interface PlotThread {
  id: number
  bookId: number
  name: string
  description: string
  type: string
  introducedChapterNumber?: number | null
  resolvedChapterNumber?: number | null
  status: string
  createdAt?: string
  updatedAt?: string
}
export const getPlotThreads = (bookId: number) =>
  api.get<PlotThread[]>(`/books/${bookId}/plot-threads`)
export const createPlotThread = (bookId: number, data: Omit<PlotThread, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.post<PlotThread>(`/books/${bookId}/plot-threads`, data)
export const updatePlotThread = (bookId: number, threadId: number, data: Omit<PlotThread, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.put<PlotThread>(`/books/${bookId}/plot-threads/${threadId}`, data)
export const deletePlotThread = (bookId: number, threadId: number) =>
  api.delete(`/books/${bookId}/plot-threads/${threadId}`)

// LLM Presets
export interface LlmPreset {
  id: number
  userId?: number | null
  name: string
  provider: string
  modelName: string
  endpoint: string
  apiKey?: string | null
  embeddingModelName?: string | null
  createdAt?: string
  updatedAt?: string
}
export const getPresets = () => api.get<LlmPreset[]>('/presets')
export const createPreset = (data: Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>) =>
  api.post<LlmPreset>('/presets', data)
export const updatePreset = (id: number, data: Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>) =>
  api.put<LlmPreset>(`/presets/${id}`, data)
export const deletePreset = (id: number) => api.delete(`/presets/${id}`)
