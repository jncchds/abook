import type { BookChatState } from '../api'

/**
 * The newest page of a book chat, kept in localStorage so the chat shows at once when it is opened again;
 * the server's copy replaces it as soon as it loads. Keyed by user and book, removed on sign-out.
 * Storage can be full, blocked or cleared — every access is best-effort.
 */
const PREFIX = 'bookChat:'
/** Keep each book's entry small: localStorage holds ~5 MB for the whole site. */
const MAX_CHARS = 500_000

const key = (userId: number, bookId: number) => `${PREFIX}${userId}:${bookId}`

export function readChatCache(userId: number | undefined, bookId: number): BookChatState | null {
  if (!userId || !bookId) return null
  try {
    const raw = localStorage.getItem(key(userId, bookId))
    if (!raw) return null
    const state = JSON.parse(raw) as BookChatState
    return Array.isArray(state.messages) ? state : null
  } catch { return null }
}

export function writeChatCache(userId: number | undefined, bookId: number, state: BookChatState) {
  if (!userId || !bookId) return
  // A run in progress is only known from the server.
  let cached: BookChatState = { ...state, busy: false, busyRole: null }
  let raw = JSON.stringify(cached)
  // Too big (long previews): drop the oldest messages until it fits; the rest loads with "Load earlier messages".
  while (raw.length > MAX_CHARS && cached.messages.length > 1) {
    const drop = Math.max(1, Math.ceil(cached.messages.length / 4))
    cached = { ...cached, hasMore: true, messages: cached.messages.slice(drop) }
    raw = JSON.stringify(cached)
  }
  try {
    if (raw.length <= MAX_CHARS) localStorage.setItem(key(userId, bookId), raw)
    else localStorage.removeItem(key(userId, bookId))
  } catch { /* full or blocked */ }
}

export function removeChatCache(userId: number | undefined, bookId: number) {
  if (!userId || !bookId) return
  try { localStorage.removeItem(key(userId, bookId)) } catch { /* blocked */ }
}

/** Every cached chat, of every user — on sign-out, so a shared browser keeps none. */
export function clearChatCaches() {
  try {
    for (let i = localStorage.length - 1; i >= 0; i--) {
      const k = localStorage.key(i)
      if (k?.startsWith(PREFIX)) localStorage.removeItem(k)
    }
  } catch { /* blocked */ }
}
