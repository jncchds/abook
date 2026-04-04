import { useState } from 'react'
import { useNavigate } from 'react-router-dom'
import { authLogin, authRegister, authSetup } from '../api'
import { useAuth } from '../hooks/useAuth'
import { useEffect } from 'react'

export default function Login() {
  const [username, setUsername] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState('')
  const [isSetup, setIsSetup] = useState(false)
  const { setUser } = useAuth()
  const navigate = useNavigate()

  useEffect(() => {
    authSetup().then(r => setIsSetup(r.data.needsSetup)).catch(() => {})
  }, [])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    try {
      const fn = isSetup ? authRegister : authLogin
      const { data } = await fn(username, password)
      setUser(data)
      navigate('/')
    } catch (err: unknown) {
      const msg = (err as { response?: { data?: { message?: string } } })?.response?.data?.message
      setError(msg ?? 'Login failed')
    }
  }

  return (
    <div className="login-page">
      <div className="login-card card">
        <h1>ABook</h1>
        <h2>{isSetup ? 'Create Admin Account' : 'Sign In'}</h2>
        {isSetup && <p className="setup-hint">No users yet. The first account becomes admin.</p>}
        <form onSubmit={handleSubmit}>
          <label>
            Username
            <input value={username} onChange={e => setUsername(e.target.value)} required autoFocus />
          </label>
          <label>
            Password
            <input type="password" value={password} onChange={e => setPassword(e.target.value)} required />
          </label>
          {error && <p className="error-msg">{error}</p>}
          <button type="submit">{isSetup ? 'Create Account' : 'Sign In'}</button>
        </form>
      </div>
    </div>
  )
}
