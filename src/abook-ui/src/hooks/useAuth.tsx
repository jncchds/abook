import { createContext, useContext, useEffect, useState, type ReactNode } from 'react'
import type { AppUser } from '../api'
import { authMe } from '../api'

interface AuthCtx {
  user: AppUser | null
  loading: boolean
  setUser: (u: AppUser | null) => void
}

const AuthContext = createContext<AuthCtx>({ user: null, loading: true, setUser: () => {} })

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<AppUser | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    authMe()
      .then(r => setUser(r.data))
      .catch(() => setUser(null))
      .finally(() => setLoading(false))
  }, [])

  return (
    <AuthContext.Provider value={{ user, loading, setUser }}>
      {children}
    </AuthContext.Provider>
  )
}

export const useAuth = () => useContext(AuthContext)
