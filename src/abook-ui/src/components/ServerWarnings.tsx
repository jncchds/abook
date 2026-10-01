import { useAuth } from '../hooks/useAuth'

/** Deployment problems only an admin can fix (the server sends them to admins only). Backticks become code. */
export default function ServerWarnings() {
  const { serverWarnings } = useAuth()
  if (serverWarnings.length === 0) return null
  return (
    <>
      {serverWarnings.map(w => (
        <div key={w} className="server-warning" role="alert">
          ⚠️ {w.split('`').map((part, i) => i % 2 === 1 ? <code key={i}>{part}</code> : part)}
        </div>
      ))}
    </>
  )
}
