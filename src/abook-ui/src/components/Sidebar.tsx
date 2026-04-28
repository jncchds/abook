import { useState } from 'react'
import { useTheme, type Theme } from '../hooks/useTheme'

interface SidebarProps {
  hasPendingQuestion?: boolean
  children: React.ReactNode
  bottomChildren?: React.ReactNode
}

export default function Sidebar({ hasPendingQuestion, children, bottomChildren }: SidebarProps) {
  const [collapsed, setCollapsed] = useState(() => localStorage.getItem('sidebarCollapsed') === 'true')
  const { theme, setTheme } = useTheme()

  const toggle = () => {
    const next = !collapsed
    setCollapsed(next)
    localStorage.setItem('sidebarCollapsed', String(next))
  }

  const nextTheme: Theme = theme === 'light' ? 'dark' : theme === 'dark' ? 'system' : 'light'
  const themeIcon = theme === 'light' ? '☀' : theme === 'dark' ? '🌙' : '⬡'
  const themeLabel = `Theme: ${theme}`

  return (
    <aside className={`app-sidebar ${collapsed ? 'collapsed' : 'expanded'}`}>
      <div className="sidebar-fixed-top">
        <button
          className="sidebar-btn sidebar-toggle-btn"
          onClick={toggle}
          title={collapsed ? 'Expand sidebar' : 'Collapse sidebar'}
        >
          <span className="s-icon">{collapsed ? '›' : '‹'}</span>
          {!collapsed && <span className="s-label">Collapse</span>}
          {hasPendingQuestion && <span className="s-pending-dot" title="Agent waiting for your answer" />}
        </button>
      </div>

      <div className="sidebar-scroll">
        {children}
      </div>

      <div className="sidebar-fixed-bottom">
        {bottomChildren}
        <button
          className="sidebar-btn"
          onClick={() => setTheme(nextTheme)}
          title={themeLabel}
        >
          <span className="s-icon">{themeIcon}</span>
          <span className="s-label">{themeLabel}</span>
        </button>
      </div>
    </aside>
  )
}

interface SidebarBtnProps {
  icon: string
  label: string
  onClick?: () => void
  active?: boolean
  disabled?: boolean
  dot?: boolean
  title?: string
  className?: string
  hideWhenCollapsed?: boolean
}

export function SidebarBtn({ icon, label, onClick, active, disabled, dot, title, className = '', hideWhenCollapsed }: SidebarBtnProps) {
  return (
    <button
      className={`sidebar-btn${active ? ' active' : ''}${hideWhenCollapsed ? ' hide-when-collapsed' : ''}${className ? ' ' + className : ''}`}
      onClick={onClick}
      disabled={disabled}
      title={title ?? label}
    >
      <span className="s-icon">{icon}</span>
      <span className="s-label">{label}</span>
      {dot && <span className="s-dot" />}
    </button>
  )
}

export function SidebarDivider() {
  return <div className="sidebar-divider" />
}

export function SidebarSection({ title, hideWhenCollapsed }: { title: string; hideWhenCollapsed?: boolean }) {
  return <div className={`sidebar-section-title${hideWhenCollapsed ? ' hide-when-collapsed' : ''}`}><span className="s-label">{title}</span></div>
}
