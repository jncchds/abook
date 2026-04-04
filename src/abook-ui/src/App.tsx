import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import Dashboard from './pages/Dashboard'
import BookDetail from './pages/BookDetail'
import Settings from './pages/Settings'
import Login from './pages/Login'
import AdminUsers from './pages/AdminUsers'
import { AuthProvider, useAuth } from './hooks/useAuth'

function ProtectedRoutes() {
  const { user, loading } = useAuth()
  if (loading) return <div className="loading">Loading…</div>
  if (!user) return <Navigate to="/login" replace />
  return (
    <Routes>
      <Route path="/" element={<Dashboard />} />
      <Route path="/books/:id" element={<BookDetail />} />
      <Route path="/books/:id/settings" element={<Settings />} />
      <Route path="/settings" element={<Settings />} />
      <Route path="/admin/users" element={<AdminUsers />} />
      <Route path="*" element={<Navigate to="/" replace />} />
    </Routes>
  )
}

export default function App() {
  return (
    <BrowserRouter>
      <AuthProvider>
        <Routes>
          <Route path="/login" element={<Login />} />
          <Route path="/*" element={<ProtectedRoutes />} />
        </Routes>
      </AuthProvider>
    </BrowserRouter>
  )
}
