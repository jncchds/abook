import { BrowserRouter, Routes, Route, Navigate } from 'react-router-dom'
import BookListLayout from './layouts/BookListLayout'
import BookLayout from './layouts/BookLayout'
import Dashboard from './pages/Dashboard'
import GlobalSettings from './pages/GlobalSettings'
import Login from './pages/Login'
import AdminUsers from './pages/AdminUsers'
import Presets from './pages/Presets'
import Overview from './pages/book/Overview'
import StoryBible from './pages/book/StoryBible'
import Characters from './pages/book/Characters'
import PlotThreads from './pages/book/PlotThreads'
import ChapterView from './pages/book/ChapterView'
import ChatPage from './pages/book/ChatPage'
import StatePage from './pages/book/StatePage'
import TokenStatsPage from './pages/book/TokenStatsPage'
import BookSettings from './pages/book/BookSettings'
import { AuthProvider, useAuth } from './hooks/useAuth'

function ProtectedRoutes() {
  const { user, loading } = useAuth()
  if (loading) return <div className="loading">Loading…</div>
  if (!user) return <Navigate to="/login" replace />
  return (
    <Routes>
      <Route element={<BookListLayout />}>
        <Route path="/" element={<Dashboard />} />
        <Route path="/settings" element={<GlobalSettings />} />
        <Route path="/presets" element={<Presets />} />
        <Route path="/admin/users" element={<AdminUsers />} />
      </Route>
      <Route path="/books/:bookId" element={<BookLayout />}>
        <Route index element={<Navigate to="overview" replace />} />
        <Route path="overview" element={<Overview />} />
        <Route path="story-bible" element={<StoryBible />} />
        <Route path="characters" element={<Characters />} />
        <Route path="plot-threads" element={<PlotThreads />} />
        <Route path="chapters/:chapterId" element={<ChapterView />} />
        <Route path="chat" element={<ChatPage />} />
        <Route path="state" element={<StatePage />} />
        <Route path="token-stats" element={<TokenStatsPage />} />
        <Route path="settings" element={<BookSettings />} />
      </Route>
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
