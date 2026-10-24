import axios from 'axios'
import { clearChatCaches } from './utils/chatCache'

const api = axios.create({ baseURL: '/api' })

// ── Session ───────────────────────────────────────────────────────────────────
// Sign-in uses a short-lived JWT access token kept only in memory (never in storage), sent as
// `Authorization: Bearer`. It is renewed with POST /auth/refresh, which reads an httpOnly cookie the
// browser sends only to /api/auth — so a reload or a new tab signs in silently while that cookie lives.

export interface Session {
  user: AppUser
  accessToken: string
  accessTokenExpiresAt: string
  /** Deployment problems only an admin can fix (empty for everyone else). */
  serverWarnings: string[]
}

let accessToken: string | null = null
let accessTokenExpiresAt = 0
let refreshInFlight: Promise<Session | null> | null = null
const sessionListeners = new Set<(session: Session | null) => void>()

function applySession(session: Session | null) {
  accessToken = session?.accessToken ?? null
  accessTokenExpiresAt = session ? Date.parse(session.accessTokenExpiresAt) : 0
  sessionListeners.forEach(l => l(session))
}

/** Notified on every sign-in, refresh and sign-out (null = signed out). Returns an unsubscribe. */
export function onSessionChange(listener: (session: Session | null) => void): () => void {
  sessionListeners.add(listener)
  return () => { sessionListeners.delete(listener) }
}

/** Renews the access token from the refresh cookie; concurrent callers share one request. */
export function refreshSession(): Promise<Session | null> {
  refreshInFlight ??= axios.post<Session>('/api/auth/refresh')
    .then(r => { applySession(r.data); return r.data })
    .catch(err => {
      // Only a rejected cookie ends the session; a network error leaves it for the next attempt.
      if (axios.isAxiosError(err) && err.response?.status === 401) applySession(null)
      return null
    })
    .finally(() => { refreshInFlight = null })
  return refreshInFlight
}

/** A usable access token (refreshed first when it expires within a minute), or null when signed out. */
export async function getAccessToken(): Promise<string | null> {
  if (accessToken && accessTokenExpiresAt - Date.now() > 60_000) return accessToken
  return (await refreshSession())?.accessToken ?? null
}

/** Endpoints that work without an access token; a 401 there means "wrong password", not "session expired". */
const PUBLIC_AUTH_ENDPOINTS = new Set(['/auth/setup', '/auth/login', '/auth/register', '/auth/logout', '/auth/refresh'])

api.interceptors.request.use(async config => {
  if (!PUBLIC_AUTH_ENDPOINTS.has(config.url ?? '')) {
    const token = await getAccessToken()
    if (token) config.headers.Authorization = `Bearer ${token}`
  }
  return config
})

// A 401 outside the auth endpoints means the access token was rejected (e.g. the server's signing key
// changed): refresh once and retry; if that fails too, the session is over and listeners see null.
api.interceptors.response.use(undefined, async error => {
  const config = axios.isAxiosError(error) ? error.config as (typeof error.config & { _retried?: boolean }) : undefined
  if (axios.isAxiosError(error) && error.response?.status === 401 && config && !config._retried && !PUBLIC_AUTH_ENDPOINTS.has(config.url ?? '')) {
    config._retried = true
    accessToken = null
    if (await refreshSession()) return api.request(config)
  }
  return Promise.reject(error)
})

/** `fetch` with the current access token, for streaming responses axios cannot read incrementally. */
export async function authFetch(url: string, init: RequestInit = {}): Promise<Response> {
  const token = await getAccessToken()
  const headers = new Headers(init.headers)
  if (token) headers.set('Authorization', `Bearer ${token}`)
  return fetch(url, { ...init, headers })
}

/** Downloads an authenticated file (a plain link cannot carry the access token). `url` is relative to /api. */
export async function downloadFile(url: string): Promise<void> {
  const r = await api.get<Blob>(url, { responseType: 'blob' })
  const disposition = String(r.headers['content-disposition'] ?? '')
  const match = /filename\*=UTF-8''([^;]+)|filename="?([^";]+)"?/i.exec(disposition)
  const name = match ? decodeURIComponent(match[1] ?? match[2]) : url.split('/').pop() ?? 'download'
  const href = URL.createObjectURL(r.data)
  const a = document.createElement('a')
  a.href = href
  a.download = name
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(href), 60_000)
}

/** Loads an authenticated image as a blob URL for `<img src>`; the caller revokes it. `url` is relative to /api. */
export const fetchImageObjectUrl = (url: string, signal?: AbortSignal) =>
  api.get<Blob>(url, { responseType: 'blob', signal }).then(r => URL.createObjectURL(r.data))

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
  illustratorSystemPrompt?: string
  illustrationsEnabled?: boolean
  /** Art direction, added word for word to every image so they share one style. */
  illustrationStyle?: string | null
  illustrationPortraitFraming?: string | null
  illustrationNegativePrompt?: string | null
  clarificationsStatus?: string
  storyBibleStatus?: string
  charactersStatus?: string
  plotThreadsStatus?: string
  chaptersStatus?: string
  mode?: BookMode
  /** Interactive books: set once writing started — the premise and Story Bible are then fixed. */
  storyBibleLockedAt?: string | null
  createdAt: string
  updatedAt: string
  chapters?: Chapter[]
}

export interface CreateBookRequest {
  title: string
  premise: string
  genre: string
  /** Required for Autonomous books; 0 = open-ended. */
  targetChapterCount?: number
  language?: string
  mode?: BookMode
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
  /** What the written prose tells, with the facts it establishes (Interactive books). */
  summary?: string
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
  /** On an Answer: the question it answers. */
  questionMessageId?: number | null
  /** Where it came from when not the web UI, e.g. "Telegram · @anna · Writers' room". */
  origin?: string | null
  createdAt: string
}

export type BookMode = 'Autonomous' | 'Interactive'

export type ChatProposalStatus = 'Pending' | 'Applying' | 'Applied' | 'Rejected' | 'Superseded' | 'Failed' | 'Undone'

/** A change the book chat LLM proposed; nothing happens to the book until it is applied. */
export interface ChatProposal {
  id: number
  messageId: number
  ordinal: number
  /** upsert_character, write_chapter, … */
  type: string
  label: string
  payloadJson: string
  /** premise, bible, character:Name, thread:Name, chapter:Id, prose:Id, note:Id */
  target?: string | null
  dependsOnProposalId?: number | null
  /** Alternatives offered for a conflict ("A", "B"); applying one rejects the others. */
  optionGroup?: string | null
  status: ChatProposalStatus
  previewBefore: string
  previewAfter: string
  resultNote?: string | null
  appliedAt?: string | null
}

export interface ChatConflict {
  description: string
  canon: string
  chapter: number | null
}

export interface BookChatMessage {
  id: number
  role: 'Author' | 'Assistant' | 'Event'
  content: string
  /** JSON array of ChatConflict. */
  conflictsJson: string
  status: 'Pending' | 'Done' | 'Failed'
  /** Author messages not typed in the web UI, e.g. "Telegram · @anna · Writers' room". */
  origin?: string | null
  createdAt: string
  proposals: ChatProposal[]
}

export interface BookChatState {
  mode: BookMode
  storyBibleLockedAt: string | null
  /** A reply or change is in progress (any run of the book). */
  busy: boolean
  busyRole: string | null
  hasMore: boolean
  messages: BookChatMessage[]
}

/** A 409 from apply/undo when a target changed: confirm, then send again with force. */
export interface BookChatConflict {
  message: string
  result: string
  stale?: { id: number; label: string }[] | null
}

export interface LlmSettings {
  provider: string
  modelName: string
  endpoint: string
  apiKey?: string | null
  embeddingModelName?: string | null
  temperature?: number | null
  timeoutMs?: number | null
  reasoningEffort?: string | null
  maxTokens?: number | null
  // Image generation (Z-Image or Unsloth Studio) — part of the configuration and of presets
  imageProvider?: string | null
  imageEndpoint?: string | null
  imageApiKey?: string | null
  /** Unsloth Studio: the model to load when none is loaded — `owner/repo` or `owner/repo:file.gguf`. */
  imageModel?: string | null
  imageSteps?: number | null
  imageCfg?: number | null
  imagePortraitWidth?: number | null
  imagePortraitHeight?: number | null
  imageLandscapeWidth?: number | null
  imageLandscapeHeight?: number | null
  imageTimeoutMs?: number | null
  imageVisualStyle?: string | null
  /** Base number of illustrations per chapter; the planner may go over it when a chapter needs more. */
  imagesPerChapter?: number | null
  unloadLlmBeforeRender?: boolean
}
export interface LlmConfig extends LlmSettings {
  id?: number
  bookId?: number
}

export interface AgentRunStatus {
  role: string
  state: string
  chapterId?: number
  /** Latest progress message of an active run. */
  step?: string | null
  /** The call streaming to the UI and the content it has sent so far (active runs only). */
  stream?: { agentRole: string; chapterId: number | null; content: string } | null
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
  api.post<Session>('/auth/login', { username, password }).then(r => { applySession(r.data); return r.data })
export const authRegister = (username: string, password: string) =>
  api.post<Session>('/auth/register', { username, password }).then(r => { applySession(r.data); return r.data })
/** Revokes this browser's refresh cookie; the in-memory access token is dropped either way. */
export const authLogout = () => api.post('/auth/logout').finally(() => { clearChatCaches(); applySession(null) })
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
  illustratorSystemPrompt: string
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

// Book chat (Interactive books). Replies and applied changes arrive over SignalR (BookChatUpdated).
export const getBookChat = (bookId: number, before?: number, limit = 50) =>
  api.get<BookChatState>(`/books/${bookId}/book-chat`, { params: { before, limit } })
export const sendBookChat = (bookId: number, message: string) =>
  api.post<{ messageId: number }>(`/books/${bookId}/book-chat`, { message })
export const retryBookChat = (bookId: number, messageId: number) =>
  api.post(`/books/${bookId}/book-chat/retry/${messageId}`)
export const kickoffBookChat = (bookId: number) =>
  api.post(`/books/${bookId}/book-chat/kickoff`)
export const applyProposals = (bookId: number, ids: number[], force = false) =>
  api.post(`/books/${bookId}/book-chat/proposals/apply`, { ids, force })
export const rejectProposals = (bookId: number, ids: number[]) =>
  api.post(`/books/${bookId}/book-chat/proposals/reject`, { ids })
export const undoProposal = (bookId: number, proposalId: number, force = false) =>
  api.post(`/books/${bookId}/book-chat/proposals/${proposalId}/undo`, { force })
export const lockStoryBible = (bookId: number) => api.post(`/books/${bookId}/book-chat/lock-bible`)
export const unlockStoryBible = (bookId: number) => api.post(`/books/${bookId}/book-chat/unlock-bible`)

// Agent actions
export const startPlanning = (bookId: number) => api.post(`/books/${bookId}/agent/plan`)

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
  failureReason?: string | null
  createdAt: string
}
export const getTokenUsage = (bookId: number, signal?: AbortSignal) =>
  api.get<TokenUsageRecord[]>(`/books/${bookId}/token-usage`, { signal })
export const clearTokenUsage = (bookId: number) =>
  api.delete(`/books/${bookId}/token-usage`)


// LLM Config
/** A book's own LLM settings (null when it has none) — there is no user-level or global configuration. */
export const getLlmConfig = (bookId: number) =>
  api.get<LlmConfig | null>('/configuration', { params: { bookId } })
export const updateLlmConfig = (data: LlmConfig & { bookId: number }) => api.put<LlmConfig>('/configuration', data)
export interface ImageGenHealth { ready: boolean; status: string; detail?: string | null }
/** A POST so the image API key stays out of the URL. */
export const getImageHealth = (endpoint: string, provider?: string | null, apiKey?: string | null, model?: string | null) =>
  api.post<ImageGenHealth>('/configuration/image-health', {
    endpoint,
    provider: provider ?? undefined,
    apiKey: apiKey || undefined,
    model: model || undefined,
  })

// Models
// The key travels in a header, never the URL, so it can't end up in access logs.
export const getModels = (endpoint?: string | null, provider?: string | null, apiKey?: string | null) =>
  api.get<ProviderModel[]>('/models', {
    params: { endpoint: endpoint ?? undefined, provider: provider ?? undefined },
    headers: apiKey ? { 'X-Api-Key': apiKey } : undefined,
  })
/** The server's explanation of a failed model list request, for showing next to the model picker. */
export const modelsErrorMessage = (err: unknown) =>
  (axios.isAxiosError(err) && err.response?.data?.message) || 'Could not load the model list.'

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
  /** Canonical appearance wording the illustrator repeats in every image prompt. */
  visualPrompt?: string
  imageSeed?: number | null
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
export type CharacterCardInput = Omit<CharacterCard, 'id' | 'bookId' | 'createdAt' | 'updatedAt' | 'visualPrompt' | 'imageSeed'>
export const getCharacters = (bookId: number, includeArchived = false, signal?: AbortSignal) =>
  api.get<CharacterCard[]>(`/books/${bookId}/characters`, { params: includeArchived ? { includeArchived: true } : undefined, signal })
export const createCharacter = (bookId: number, data: CharacterCardInput) =>
  api.post<CharacterCard>(`/books/${bookId}/characters`, data)
export const updateCharacter = (bookId: number, cardId: number, data: CharacterCardInput) =>
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

// Clarifications — answered planning questions and author notes, appended to the premise in every prompt
export interface Clarification {
  id: number
  bookId: number
  question: string
  /** Empty while the planner's question awaits an answer. */
  answer: string
  isArchived: boolean
  /** Awaiting an answer (active and unanswered); a waiting planning run resumes once none are left. */
  isOpen: boolean
  /** Who answered (or added) it when not in the web UI, e.g. "Telegram · @anna · Writers' room". */
  origin?: string | null
  createdAt: string
  updatedAt: string
}
export const getClarifications = (bookId: number, includeArchived = false, signal?: AbortSignal) =>
  api.get<Clarification[]>(`/books/${bookId}/clarifications`, { params: includeArchived ? { includeArchived: true } : undefined, signal })
export const createClarification = (bookId: number, data: { question: string; answer: string }) =>
  api.post<Clarification>(`/books/${bookId}/clarifications`, data)
export const updateClarification = (bookId: number, id: number, data: { question: string; answer: string }) =>
  api.put<Clarification>(`/books/${bookId}/clarifications/${id}`, data)
export const archiveClarification = (bookId: number, id: number) =>
  api.post<Clarification>(`/books/${bookId}/clarifications/${id}/archive`)
export const unarchiveClarification = (bookId: number, id: number) =>
  api.post<Clarification>(`/books/${bookId}/clarifications/${id}/unarchive`)

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
  api.get<PlotThread[]>(`/books/${bookId}/plot-threads`, { params: includeArchived ? { includeArchived: true } : undefined, signal })
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
export interface LlmPreset extends LlmSettings {
  id: number
  /** Null for a shared preset: usable by everyone, changed only by admins. */
  userId?: number | null
  name: string
  createdAt?: string
  updatedAt?: string
}
/** `shared` (admins only) makes a new preset — or an admin's own one — shared; sharing cannot be undone. */
export type PresetInput = Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'> & { shared?: boolean }
export const getPresets = () => api.get<LlmPreset[]>('/presets')
export const createPreset = (data: PresetInput) =>
  api.post<LlmPreset>('/presets', data)
export const updatePreset = (id: number, data: PresetInput) =>
  api.put<LlmPreset>(`/presets/${id}`, data)
export const deletePreset = (id: number) => api.delete(`/presets/${id}`)
/** The preset copied into every new book (null when the user has none). */
export const getDefaultPreset = () => api.get<{ presetId: number | null }>('/presets/default')
export const setDefaultPreset = (id: number) => api.put<{ presetId: number }>(`/presets/${id}/default`)

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

// Shared metadata type for Characters and Plot Threads snapshots (no DataJson)
export interface ContentSnapshotMeta {
  id: number
  bookId: number
  reason: string
  source: string
  createdAt: string
}
/** @deprecated Use ContentSnapshotMeta */
export type CharactersSnapshotMeta = ContentSnapshotMeta
/** @deprecated Use ContentSnapshotMeta */
export type PlotThreadsSnapshotMeta = ContentSnapshotMeta

export const getCharactersHistory = (bookId: number) =>
  api.get<ContentSnapshotMeta[]>(`/books/${bookId}/characters/history`)
export const getCharactersSnapshot = (bookId: number, snapshotId: number) =>
  api.get<{ id: number; bookId: number; dataJson: string; reason: string; source: string; createdAt: string }>(`/books/${bookId}/characters/history/${snapshotId}`)
export const restoreCharactersSnapshot = (bookId: number, snapshotId: number) =>
  api.post<CharacterCard[]>(`/books/${bookId}/characters/history/${snapshotId}/restore`)

export const getPlotThreadsHistory = (bookId: number) =>
  api.get<ContentSnapshotMeta[]>(`/books/${bookId}/plot-threads/history`)
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
  illustrations?: Illustration[]
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

// Illustrations
export type IllustrationKind = 'Scene' | 'Cover' | 'Portrait'
export type IllustrationOrientation = 'Portrait' | 'Landscape' | 'Square'
export type IllustrationStatus = 'Planned' | 'Rendered' | 'Failed'
export interface Illustration {
  id: number
  chapterId: number | null
  kind: IllustrationKind
  characterCardId: number | null
  paragraphNumber: number
  /** 0-based paragraph the image follows in the current chapter text; -1 for covers and portraits. */
  paragraphIndex: number
  anchorText: string
  caption: string
  prompt: string
  negativePrompt: string
  orientation: IllustrationOrientation
  focusCharacterId: number | null
  characters: string[]
  seed: number | null
  /** Planned = needs rendering (an edited illustration keeps its previous image until then). */
  status: IllustrationStatus
  error: string | null
  imageId: number | null
  updatedAt: string
}
export interface IllustrationInput {
  kind?: IllustrationKind
  chapterId?: number | null
  paragraphNumber?: number
  caption?: string
  prompt?: string
  negativePrompt?: string
  orientation?: IllustrationOrientation
  /** 0 clears the focus character. */
  focusCharacterId?: number
  characters?: string[]
}
export type IllustrationPlanScope = 'Missing' | 'Chapter' | 'Cover' | 'Characters' | 'Style'
export type RenderScope = 'All' | 'Portraits' | 'Chapter' | 'Character' | 'Single'

export const getIllustrations = (bookId: number, params?: { chapterId?: number; kind?: IllustrationKind }, signal?: AbortSignal) =>
  api.get<Illustration[]>(`/books/${bookId}/illustrations`, { params, signal })
export const createIllustration = (bookId: number, data: IllustrationInput) =>
  api.post<Illustration>(`/books/${bookId}/illustrations`, data)
export const updateIllustration = (bookId: number, id: number, data: IllustrationInput) =>
  api.put<Illustration>(`/books/${bookId}/illustrations/${id}`, data)
export const deleteIllustration = (bookId: number, id: number) =>
  api.delete(`/books/${bookId}/illustrations/${id}`)
export const updateCharacterVisual = (bookId: number, cardId: number, visualPrompt: string) =>
  api.put<{ id: number; visualPrompt: string; imageSeed: number | null; updatedIllustrations: number }>(
    `/books/${bookId}/illustrations/characters/${cardId}/visual`, { visualPrompt })
export interface ArtDirection { style: string; portraitFraming: string; negativePrompt: string }
export const updateArtDirection = (bookId: number, data: ArtDirection) =>
  api.put<ArtDirection & { markedForRerender: number }>(`/books/${bookId}/illustrations/style`, data)
export type VisualAssetKind = 'Object' | 'Outfit' | 'Location' | 'Creature' | 'Vehicle' | 'Other'
/** A recurring prop, outfit, place … whose description every prompt showing it repeats word for word. */
export interface VisualAsset {
  id: number
  name: string
  kind: VisualAssetKind
  description: string
  /** Set for a character's usual outfit. */
  characterCardId: number | null
  firstChapterNumber: number | null
}
export interface VisualAssetInput { name?: string; kind?: VisualAssetKind; description?: string }
export const getVisualAssets = (bookId: number) =>
  api.get<VisualAsset[]>(`/books/${bookId}/illustrations/assets`)
export const createVisualAsset = (bookId: number, data: VisualAssetInput) =>
  api.post<VisualAsset>(`/books/${bookId}/illustrations/assets`, data)
export const updateVisualAsset = (bookId: number, id: number, data: VisualAssetInput) =>
  api.put<{ asset: VisualAsset; updatedIllustrations: number }>(`/books/${bookId}/illustrations/assets/${id}`, data)
export const deleteVisualAsset = (bookId: number, id: number) =>
  api.delete(`/books/${bookId}/illustrations/assets/${id}`)
export const planIllustrations = (bookId: number, scope: IllustrationPlanScope, targetId?: number) =>
  api.post(`/books/${bookId}/illustrations/plan`, { scope, targetId })
export const renderIllustrations = (bookId: number, scope: RenderScope, targetId?: number, reroll = false) =>
  api.post(`/books/${bookId}/illustrations/render`, { scope, targetId, reroll })
/**
 * Image paths relative to /api, loaded with `useAuthedImage` (they need the access token).
 * They are immutable: a re-render produces a new image id.
 */
export const bookImagePath = (bookId: number, imageId: number) => `/books/${bookId}/images/${imageId}`
export const publicBookImagePath = (bookId: number, imageId: number) => `/public/books/${bookId}/images/${imageId}`


// ── Telegram ──────────────────────────────────────────────────────────────────
// Chats and topics bound to the book: whoever can post there uses the book at the binding's level.

export type TelegramAccessLevel = 'Read' | 'Participate' | 'Control'
export type TelegramFileFormat = 'epub' | 'fb2' | 'html'

export interface TelegramBinding {
  id: number
  chatType: 'private' | 'group' | 'supergroup' | 'channel'
  chatTitle: string
  isTopic: boolean
  level: TelegramAccessLevel
  /** Starting, stopping and applying need Telegram admin rights in the chat. */
  controlAdminsOnly: boolean
  fileFormat: TelegramFileFormat
  /** Private chats: show the text being written as a live draft. */
  liveDrafts: boolean
  createdAt: string
}

export interface TelegramState {
  /** TELEGRAM_BOT_TOKEN is set. */
  enabled: boolean
  ready: boolean
  problem: string | null
  botUsername: string | null
  /** Topic mode in private chats is on: each book gets its own topic. */
  privateTopics: boolean
  webhook: boolean
  bindings: TelegramBinding[]
}

export interface TelegramCode {
  code: string
  expiresAt: string
  privateLink: string | null
  groupLink: string | null
  bindCommand: string
}

export const getTelegram = (bookId: number) => api.get<TelegramState>(`/books/${bookId}/telegram`)
export const createTelegramCode = (bookId: number, level: TelegramAccessLevel) =>
  api.post<TelegramCode>(`/books/${bookId}/telegram/codes`, { level })
export const updateTelegramBinding = (bookId: number, id: number,
  data: Partial<Pick<TelegramBinding, 'level' | 'controlAdminsOnly' | 'fileFormat' | 'liveDrafts'>>) =>
  api.put<TelegramBinding>(`/books/${bookId}/telegram/bindings/${id}`, data)
export const revokeTelegramBinding = (bookId: number, id: number) =>
  api.delete(`/books/${bookId}/telegram/bindings/${id}`)
