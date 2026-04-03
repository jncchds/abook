import axios from 'axios'

const api = axios.create({ baseURL: '/api' })

export interface Book {
  id: number
  title: string
  premise: string
  genre: string
  targetChapterCount: number
  status: string
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

// Books
export const getBooks = () => api.get<Book[]>('/books')
export const getBook = (id: number) => api.get<Book>(`/books/${id}`)
export const createBook = (data: Omit<Book, 'id' | 'status' | 'createdAt' | 'updatedAt' | 'chapters'>) =>
  api.post<Book>('/books', data)
export const updateBook = (id: number, data: Partial<Book>) => api.put<Book>(`/books/${id}`, data)
export const deleteBook = (id: number) => api.delete(`/books/${id}`)

// Chapters
export const getChapters = (bookId: number) => api.get<Chapter[]>(`/books/${bookId}/chapters`)
export const getChapter = (bookId: number, chapterId: number) =>
  api.get<Chapter>(`/books/${bookId}/chapters/${chapterId}`)
export const updateChapter = (bookId: number, chapterId: number, data: Partial<Chapter>) =>
  api.put<Chapter>(`/books/${bookId}/chapters/${chapterId}`, data)

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
export const getAgentStatus = (bookId: number) => api.get(`/books/${bookId}/agent/status`)

// LLM Config
export const getLlmConfig = (bookId?: number) =>
  api.get<LlmConfig>('/configuration', { params: { bookId } })
export const updateLlmConfig = (data: LlmConfig) => api.put<LlmConfig>('/configuration', data)
