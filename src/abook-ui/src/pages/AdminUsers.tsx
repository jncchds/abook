import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { useAuth } from '../hooks/useAuth'
import type { AppUser } from '../api'
import { getUsers, createUser, changePassword, changeRole } from '../api'

export default function AdminUsers() {
  const { user: me } = useAuth()
  const [users, setUsers] = useState<AppUser[]>([])
  const [showForm, setShowForm] = useState(false)
  const [form, setForm] = useState({ username: '', password: '', isAdmin: false })
  const [pwdEdit, setPwdEdit] = useState<{ id: number; value: string } | null>(null)
  const [error, setError] = useState('')

  useEffect(() => {
    getUsers().then(r => setUsers(r.data)).catch(() => {})
  }, [])

  const handleCreate = async (e: React.FormEvent) => {
    e.preventDefault()
    setError('')
    try {
      const { data } = await createUser(form.username, form.password, form.isAdmin)
      setUsers(prev => [...prev, data])
      setShowForm(false)
      setForm({ username: '', password: '', isAdmin: false })
    } catch (err: unknown) {
      setError((err as { response?: { data?: { message?: string } } })?.response?.data?.message ?? 'Failed')
    }
  }

  const handlePasswordChange = async (id: number) => {
    if (!pwdEdit || pwdEdit.id !== id) return
    await changePassword(id, pwdEdit.value)
    setPwdEdit(null)
  }

  const handleRoleToggle = async (u: AppUser) => {
    if (u.id === me?.id) return // don't demote yourself
    await changeRole(u.id, !u.isAdmin)
    setUsers(prev => prev.map(x => x.id === u.id ? { ...x, isAdmin: !x.isAdmin } : x))
  }

  return (
    <div className="container">
      <div className="header">
        <Link to="/" className="back-link">← Books</Link>
        <h1>User Management</h1>
        <button onClick={() => setShowForm(v => !v)}>+ New User</button>
      </div>

      {showForm && (
        <form className="card" onSubmit={handleCreate}>
          <h2>New User</h2>
          <label>Username<input value={form.username} onChange={e => setForm(f => ({ ...f, username: e.target.value }))} required /></label>
          <label>Password<input type="password" value={form.password} onChange={e => setForm(f => ({ ...f, password: e.target.value }))} required /></label>
          <label className="checkbox-label">
            <input type="checkbox" checked={form.isAdmin} onChange={e => setForm(f => ({ ...f, isAdmin: e.target.checked }))} />
            Admin
          </label>
          {error && <p className="error-msg">{error}</p>}
          <div className="actions">
            <button type="submit">Create</button>
            <button type="button" onClick={() => setShowForm(false)}>Cancel</button>
          </div>
        </form>
      )}

      <div className="users-table">
        <table>
          <thead>
            <tr><th>Username</th><th>Role</th><th>Password</th><th>Actions</th></tr>
          </thead>
          <tbody>
            {users.map(u => (
              <tr key={u.id}>
                <td>{u.username} {u.id === me?.id && <span className="badge">(you)</span>}</td>
                <td>
                  <button
                    className={`role-badge ${u.isAdmin ? 'admin' : 'user'}`}
                    onClick={() => handleRoleToggle(u)}
                    disabled={u.id === me?.id}
                    title={u.id === me?.id ? "Can't change your own role" : 'Toggle role'}
                  >
                    {u.isAdmin ? 'Admin' : 'User'}
                  </button>
                </td>
                <td>
                  {pwdEdit?.id === u.id ? (
                    <span className="pwd-edit">
                      <input
                        type="password"
                        placeholder="New password"
                        value={pwdEdit.value}
                        onChange={e => setPwdEdit({ id: u.id, value: e.target.value })}
                        autoFocus
                      />
                      <button onClick={() => handlePasswordChange(u.id)}>Save</button>
                      <button onClick={() => setPwdEdit(null)}>Cancel</button>
                    </span>
                  ) : (
                    <button onClick={() => setPwdEdit({ id: u.id, value: '' })}>Change password</button>
                  )}
                </td>
                <td>—</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  )
}
