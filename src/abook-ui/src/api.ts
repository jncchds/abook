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
  plannerSystemPrompt?: string
  writerSystemPrompt?: string
  editorSystemPrompt?: string
  continuityCheckerSystemPrompt?: string
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
  plannerSystemPrompt: string
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
export const postAnswer = (bookId: number, messageId: number, answer: string) =>
  api.post(`/books/${bookId}/messages/answer`, { messageId, answer })

// Agent actions
export const startPlanning = (bookId: number) => api.post(`/books/${bookId}/agent/plan`)
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

// LLM Config
export const getLlmConfig = (bookId?: number) =>
  api.get<LlmConfig>('/configuration', { params: { bookId } })
export const updateLlmConfig = (data: LlmConfig) => api.put<LlmConfig>('/configuration', data)

// Ollama
export const getOllamaModels = () => api.get<OllamaModel[]>('/ollama/models')
