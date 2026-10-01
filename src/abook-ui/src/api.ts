import axios from 'axios'

const api = axios.create({ baseURL: '/api', withCredentials: true })

/**
 * Calls `handler` whenever a request fails with 401 (session expired or signed out elsewhere),
 * so the app can drop the user instead of every call failing silently. Returns an unsubscribe.
 * The sign-in probes are excluded — a 401 there just means "not signed in yet" / "wrong password".
 */
const SIGN_IN_PROBES = new Set(['/auth/me', '/auth/login', '/auth/register', '/auth/setup'])
export function onUnauthorized(handler: () => void): () => void {
  const id = api.interceptors.response.use(undefined, (error) => {
    if (axios.isAxiosError(error) && error.response?.status === 401 && !SIGN_IN_PROBES.has(error.config?.url ?? ''))
      handler()
    return Promise.reject(error)
  })
  return () => api.interceptors.response.eject(id)
}

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
/** Image URLs are immutable: a re-render produces a new image id. */
export const bookImageUrl = (bookId: number, imageId: number) => `/api/books/${bookId}/images/${imageId}`
export const publicBookImageUrl = (bookId: number, imageId: number) => `/api/public/books/${bookId}/images/${imageId}`

