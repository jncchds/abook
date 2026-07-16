import axios from 'axios'

const api = axios.create({ baseURL: '/api', withCredentials: true })

export interface Book {
  id: number
  baseBookId?: number | null
  settingsCopiedAt?: string | null
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
  humanAssisted?: boolean
  createdAt: string
  updatedAt: string
  chapters?: Chapter[]
}

export interface CreateBookRequest {
  title: string
  premise: string
  genre: string
  targetChapterCount: number
  language?: string
  humanAssisted?: boolean
  baseBookId?: number
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
  isArchived?: boolean
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
  isOptional?: boolean
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
  temperature?: number | null
  timeoutMs?: number | null
  reasoningEffort?: string | null
  maxTokens?: number | null
}

export interface AgentRunStatus {
  role: string
  state: string
  chapterId?: number
}

export interface ProviderModel {
  name: string
  size: number
}

export interface AppUser {
  id: number
  username: string
  displayName?: string
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
export const getApiToken = () => api.get<{ token: string | null }>('/auth/api-token')
export const regenerateApiToken = () => api.post<{ token: string }>('/auth/api-token/regenerate')

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
export const createBook = (data: CreateBookRequest) =>
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
export const archiveChapter = (bookId: number, chapterId: number) =>
  api.post(`/books/${bookId}/chapters/${chapterId}/archive`)
export const restoreChapter = (bookId: number, chapterId: number) =>
  api.post<Chapter>(`/books/${bookId}/chapters/${chapterId}/restore`)

// Messages
export const getMessages = (bookId: number, signal?: AbortSignal, chapterId?: number) =>
  api.get<AgentMessage[]>(`/books/${bookId}/messages`, { params: { chapterId }, signal })
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
  stepLabel?: string | null   // non-null = workflow step row (not token usage)
  endpoint?: string | null
  modelName?: string | null
  failed?: boolean
  createdAt: string
}
export const getTokenUsage = (bookId: number, signal?: AbortSignal) =>
  api.get<TokenUsageRecord[]>(`/books/${bookId}/token-usage`, { signal })
export const clearTokenUsage = (bookId: number) =>
  api.delete(`/books/${bookId}/token-usage`)

export const getStreamBuffer = (bookId: number, agentRole?: string, chapterId?: number) => {
  const params = new URLSearchParams()
  if (agentRole) params.set('agentRole', agentRole)
  if (chapterId !== undefined) params.set('chapterId', String(chapterId))
  const qs = params.toString()
  return api.get<{ content: string }>(`/books/${bookId}/agent/stream-buffer` + (qs ? `?${qs}` : ''))
}

// LLM Config
export const getLlmConfig = (bookId?: number) =>
  api.get<LlmConfig>('/configuration', { params: { bookId } })
export const updateLlmConfig = (data: LlmConfig) => api.put<LlmConfig>('/configuration', data)

// Models
export const getModels = (endpoint?: string, provider?: string, apiKey?: string) => {
  const params = new URLSearchParams()
  if (endpoint) params.set('endpoint', endpoint)
  if (provider) params.set('provider', provider)
  if (apiKey) params.set('apiKey', apiKey)
  const qs = params.toString()
  return api.get<ProviderModel[]>('/models' + (qs ? `?${qs}` : ''))
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
export const getStoryBible = (bookId: number, signal?: AbortSignal) =>
  api.get<StoryBible>(`/books/${bookId}/story-bible`, { signal })
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
  isArchived?: boolean
  createdAt?: string
  updatedAt?: string
}
export interface CharacterCardVersion {
  id: number
  characterCardId: number
  bookId: number
  versionNumber: number
  name: string
  role: string
  physicalDescription: string
  personality: string
  backstory: string
  goalMotivation: string
  arc: string
  firstAppearanceChapterNumber?: number | null
  notes: string
  createdBy: string
  createdAt: string
}
export const getCharacters = (bookId: number, includeArchived = false, signal?: AbortSignal) =>
  api.get<CharacterCard[]>(`/books/${bookId}/characters${includeArchived ? '?includeArchived=true' : ''}`, { signal })
export const createCharacter = (bookId: number, data: Omit<CharacterCard, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.post<CharacterCard>(`/books/${bookId}/characters`, data)
export const updateCharacter = (bookId: number, cardId: number, data: Omit<CharacterCard, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.put<CharacterCard>(`/books/${bookId}/characters/${cardId}`, data)
export const archiveCharacter = (bookId: number, cardId: number) =>
  api.post(`/books/${bookId}/characters/${cardId}/archive`)
export const unarchiveCharacter = (bookId: number, cardId: number) =>
  api.post<CharacterCard>(`/books/${bookId}/characters/${cardId}/unarchive`)
export const deleteCharacter = (bookId: number, cardId: number) =>
  api.delete(`/books/${bookId}/characters/${cardId}`)
export const getCharacterHistory = (bookId: number, cardId: number) =>
  api.get<CharacterCardVersion[]>(`/books/${bookId}/characters/${cardId}/history`)
export const restoreCharacterVersion = (bookId: number, cardId: number, versionId: number) =>
  api.post<CharacterCard>(`/books/${bookId}/characters/${cardId}/history/${versionId}/restore`)

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
  isArchived?: boolean
  createdAt?: string
  updatedAt?: string
}
export interface PlotThreadVersion {
  id: number
  plotThreadId: number
  bookId: number
  versionNumber: number
  name: string
  description: string
  type: string
  introducedChapterNumber?: number | null
  resolvedChapterNumber?: number | null
  status: string
  createdBy: string
  createdAt: string
}
export const getPlotThreads = (bookId: number, includeArchived = false, signal?: AbortSignal) =>
  api.get<PlotThread[]>(`/books/${bookId}/plot-threads${includeArchived ? '?includeArchived=true' : ''}`, { signal })
export const createPlotThread = (bookId: number, data: Omit<PlotThread, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.post<PlotThread>(`/books/${bookId}/plot-threads`, data)
export const updatePlotThread = (bookId: number, threadId: number, data: Omit<PlotThread, 'id' | 'bookId' | 'createdAt' | 'updatedAt'>) =>
  api.put<PlotThread>(`/books/${bookId}/plot-threads/${threadId}`, data)
export const archivePlotThread = (bookId: number, threadId: number) =>
  api.post(`/books/${bookId}/plot-threads/${threadId}/archive`)
export const unarchivePlotThread = (bookId: number, threadId: number) =>
  api.post<PlotThread>(`/books/${bookId}/plot-threads/${threadId}/unarchive`)
export const deletePlotThread = (bookId: number, threadId: number) =>
  api.delete(`/books/${bookId}/plot-threads/${threadId}`)
export const getPlotThreadHistory = (bookId: number, threadId: number) =>
  api.get<PlotThreadVersion[]>(`/books/${bookId}/plot-threads/${threadId}/history`)
export const restorePlotThreadVersion = (bookId: number, threadId: number, versionId: number) =>
  api.post<PlotThread>(`/books/${bookId}/plot-threads/${threadId}/history/${versionId}/restore`)

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
  temperature?: number | null
  timeoutMs?: number | null
  reasoningEffort?: string | null
  maxTokens?: number | null
  createdAt?: string
  updatedAt?: string
}
export const getPresets = () => api.get<LlmPreset[]>('/presets')
export const createPreset = (data: Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>) =>
  api.post<LlmPreset>('/presets', data)
export const updatePreset = (id: number, data: Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>) =>
  api.put<LlmPreset>(`/presets/${id}`, data)
export const deletePreset = (id: number) => api.delete(`/presets/${id}`)

// Story Bible history / snapshots
export interface StoryBibleSnapshotMeta {
  id: number
  bookId: number
  settingDescription: string
  timePeriod: string
  themes: string
  toneAndStyle: string
  worldRules: string
  notes: string
  reason: string
  createdAt: string
}
export const getStoryBibleHistory = (bookId: number) =>
  api.get<StoryBibleSnapshotMeta[]>(`/books/${bookId}/story-bible/history`)
export const getStoryBibleSnapshot = (bookId: number, snapshotId: number) =>
  api.get<StoryBibleSnapshotMeta>(`/books/${bookId}/story-bible/history/${snapshotId}`)
export const restoreStoryBibleSnapshot = (bookId: number, snapshotId: number) =>
  api.post<StoryBible>(`/books/${bookId}/story-bible/history/${snapshotId}/restore`)

// Characters history / snapshots (metadata — no DataJson)
export interface CharactersSnapshotMeta {
  id: number
  bookId: number
  reason: string
  source: string
  createdAt: string
}
export const getCharactersHistory = (bookId: number) =>
  api.get<CharactersSnapshotMeta[]>(`/books/${bookId}/characters/history`)
export const getCharactersSnapshot = (bookId: number, snapshotId: number) =>
  api.get<{ id: number; bookId: number; dataJson: string; reason: string; source: string; createdAt: string }>(`/books/${bookId}/characters/history/${snapshotId}`)
export const restoreCharactersSnapshot = (bookId: number, snapshotId: number) =>
  api.post<CharacterCard[]>(`/books/${bookId}/characters/history/${snapshotId}/restore`)

// Plot Threads history / snapshots (metadata — no DataJson)
export interface PlotThreadsSnapshotMeta {
  id: number
  bookId: number
  reason: string
  source: string
  createdAt: string
}
export const getPlotThreadsHistory = (bookId: number) =>
  api.get<PlotThreadsSnapshotMeta[]>(`/books/${bookId}/plot-threads/history`)
export const getPlotThreadsSnapshot = (bookId: number, snapshotId: number) =>
  api.get<{ id: number; bookId: number; dataJson: string; reason: string; source: string; createdAt: string }>(`/books/${bookId}/plot-threads/history/${snapshotId}`)
export const restorePlotThreadsSnapshot = (bookId: number, snapshotId: number) =>
  api.post<PlotThread[]>(`/books/${bookId}/plot-threads/history/${snapshotId}/restore`)

// Chapter version history
export interface ChapterVersionMeta {
  id: number
  versionNumber: number
  createdBy: string
  isActive: boolean
  hasEmbeddings: boolean
  wordCount: number
  createdAt: string
}
export interface ChapterVersionFull {
  id: number
  chapterId: number
  bookId: number
  versionNumber: number
  title: string
  outline: string
  content: string
  status: string
  povCharacter: string
  foreshadowingNotes: string
  payoffNotes: string
  createdBy: string
  isActive: boolean
  hasEmbeddings: boolean
  createdAt: string
}
export const getChapterVersions = (bookId: number, chapterId: number) =>
  api.get<ChapterVersionMeta[]>(`/books/${bookId}/chapters/${chapterId}/versions`)
export const getChapterVersion = (bookId: number, chapterId: number, versionId: number) =>
  api.get<ChapterVersionFull>(`/books/${bookId}/chapters/${chapterId}/versions/${versionId}`)
export const activateChapterVersion = (bookId: number, chapterId: number, versionId: number) =>
  api.post<{ chapter: Chapter; version: ChapterVersionMeta }>(`/books/${bookId}/chapters/${chapterId}/versions/${versionId}/activate`)

// Profile
export const updateProfile = (displayName: string) =>
  api.patch<{ displayName: string }>('/auth/profile', { displayName })

// Public library
export interface PublicBookItem {
  id: number
  title: string
  genre: string
  targetChapterCount: number
  writtenChapterCount: number
  status: string
  language: string
  authorDisplayName: string
  createdAt: string
  updatedAt: string
}

export interface PublicBooksResponse {
  items: PublicBookItem[]
  totalCount: number
  page: number
  pageSize: number
  totalPages: number
}

export interface PublicChapter {
  id: number
  number: number
  title: string
  outline: string
  content: string
  status: string
}

export interface PublicBookDetail {
  id: number
  title: string
  genre: string
  language: string
  premise: string
  status: string
  targetChapterCount: number
  writtenChapterCount: number
  createdAt: string
  updatedAt: string
  chapters: PublicChapter[]
}

export const getPublicConfig = () =>
  api.get<{ isPublicMode: boolean }>('/public/config')

export const getPublicGenres = () =>
  api.get<string[]>('/public/genres')

export interface PublicBooksParams {
  page?: number
  pageSize?: number
  author?: string
  genre?: string
  chapterCount?: number
}

export const getPublicBooks = (params: PublicBooksParams = {}) =>
  api.get<PublicBooksResponse>('/public/books', { params })

export const getPublicBook = (id: number) =>
  api.get<PublicBookDetail>(`/public/books/${id}`)

