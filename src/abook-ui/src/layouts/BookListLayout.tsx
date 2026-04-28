import { useEffect, useState } from 'react'
import { useNavigate, useLocation, Outlet } from 'react-router-dom'
import type { Book } from '../api'
import { getBooks, authLogout } from '../api'
import { useAuth } from '../hooks/useAuth'
import Sidebar, { SidebarBtn, SidebarDivider, SidebarSection } from '../components/Sidebar'

export default function BookListLayout() {
  const [books, setBooks] = useState<Book[]>([])
  const navigate = useNavigate()
  const location = useLocation()
  const { user, setUser } = useAuth()

  useEffect(() => {
    getBooks().then(r => setBooks(r.data)).catch(console.error)
  }, [])

  const handleLogout = async () => {
    await authLogout()
    setUser(null)
    navigate('/login')
  }

  return (
    <div className="app-layout">
      <Sidebar bottomChildren={
        <SidebarBtn icon="🚪" label={`Sign Out (${user?.username ?? '…'})`} onClick={handleLogout} />
      }>
        <SidebarBtn icon="➕" label="New Book" active={location.pathname === '/' && location.search.includes('new=1')} onClick={() => navigate('/?new=1')} />
        {user?.isAdmin && <SidebarBtn icon="👥" label="Users" active={location.pathname === '/admin/users'} onClick={() => navigate('/admin/users')} />}
        <SidebarBtn icon="🔑" label="Presets" active={location.pathname === '/presets'} onClick={() => navigate('/presets')} />
        <SidebarBtn icon="⚙" label="Settings" active={location.pathname === '/settings'} onClick={() => navigate('/settings')} />
        <SidebarDivider />
        <SidebarSection title="Books" hideWhenCollapsed />
        <div className="sidebar-book-list">
          {books.map(b => (
            <button
              key={b.id}
              className="sidebar-book-entry"
              onClick={() => navigate(`/books/${b.id}`)}
              title={b.title}
            >
              <span className="s-be-icon">📖</span>
              <span className="s-be-content">
                <span className="s-be-title">{b.title}</span>
                <span className="s-be-premise">{b.premise.slice(0, 60)}{b.premise.length > 60 ? '…' : ''}</span>
              </span>
            </button>
          ))}
          {books.length === 0 && <p style={{ padding: '0.5rem', fontSize: '0.8rem', color: 'var(--text-muted)' }}>No books yet</p>}
        </div>
      </Sidebar>
      <main className="app-main">
        <Outlet context={{ books, setBooks }} />
      </main>
    </div>
  )
}
