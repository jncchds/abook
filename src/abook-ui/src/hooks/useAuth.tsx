import { createContext, useContext, useEffect, useState, type ReactNode, type Dispatch, type SetStateAction } from 'react'
import type { AppUser } from '../api'
import { onSessionChange, refreshSession } from '../api'

interface AuthCtx {
  user: AppUser | null
  loading: boolean
  setUser: Dispatch<SetStateAction<AppUser | null>>
  /** Deployment problems only an admin can fix, shown as a banner. */
  serverWarnings: string[]
}

const AuthContext = createContext<AuthCtx>({ user: null, loading: true, setUser: () => {}, serverWarnings: [] })

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AppUser | null>(null)
  const [serverWarnings, setServerWarnings] = useState<string[]>([])
  const [loading, setLoading] = useState(true)

  // Every sign-in, refresh and sign-out goes through the session store in api.ts — including a refresh
  // that fails because the session ended elsewhere, which drops the user so protected routes redirect.
  useEffect(() => onSessionChange(session => {
    setUser(session?.user ?? null)
    setServerWarnings(session?.serverWarnings ?? [])
  }), [])

  // Sign in silently from the refresh cookie, if there is one.
  useEffect(() => {
    refreshSession().finally(() => setLoading(false))
  }, [])

  return (
    <AuthContext.Provider value={{ user, loading, setUser, serverWarnings }}>
      {children}
    </AuthContext.Provider>
  )
}

export const useAuth = () => useContext(AuthContext)
