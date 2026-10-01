import { useEffect, useState, useRef } from 'react'
import { Link } from 'react-router-dom'
import type { LlmPreset } from '../api'
import { getPresets, getDefaultPreset, setDefaultPreset, getApiToken, regenerateApiToken, updateProfile } from '../api'
import { PROVIDER_LABELS } from '../config/providers'
import { useNotifications } from '../hooks/useNotifications'
import { useAuth } from '../hooks/useAuth'

export default function GlobalSettings() {
  const { user, setUser } = useAuth()
  const { supported: notifSupported, permission, enabled: notifEnabled, setEnabled: setNotifEnabled } = useNotifications()
  const [pullModel, setPullModel] = useState('')
  const [pullStatus, setPullStatus] = useState('')

  const [displayName, setDisplayName] = useState('')
  const [profileSaved, setProfileSaved] = useState(false)
  const [profileError, setProfileError] = useState('')

  useEffect(() => {
    setDisplayName(user?.displayName ?? user?.username ?? '')
  }, [user])
  const abortRef = useRef<AbortController | null>(null)

  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [defaultPresetId, setDefaultPresetId] = useState<number | null>(null)
  const [defaultSaved, setDefaultSaved] = useState(false)
  const [defaultError, setDefaultError] = useState('')
  const defaultPreset = presets.find(p => p.id === defaultPresetId)

  const [mcpToken, setMcpToken] = useState<string | null>(null)
  const [mcpTokenVisible, setMcpTokenVisible] = useState(false)
  const [mcpTokenCopied, setMcpTokenCopied] = useState(false)
  const [mcpTokenRegenConfirm, setMcpTokenRegenConfirm] = useState(false)

  useEffect(() => {
    getPresets().then(r => setPresets(r.data)).catch(err => console.error('Failed to load presets', err))
    getDefaultPreset().then(r => setDefaultPresetId(r.data.presetId)).catch(err => console.error('Failed to load default preset', err))
    getApiToken().then(r => setMcpToken(r.data.token)).catch(err => console.error('Failed to load MCP token', err))
  }, [])

  const handleDefaultChange = async (id: number) => {
    setDefaultError('')
    try {
      await setDefaultPreset(id)
      setDefaultPresetId(id)
      setDefaultSaved(true)
      setTimeout(() => setDefaultSaved(false), 2000)
    } catch {
      setDefaultError('Failed to set the default preset. Please try again.')
    }
  }

  const handleRegenMcpToken = async () => {
    const r = await regenerateApiToken()
    setMcpToken(r.data.token)
    setMcpTokenVisible(true)
    setMcpTokenRegenConfirm(false)
  }

  const handlePull = async () => {
    if (!pullModel.trim()) return
    abortRef.current = new AbortController()
    setPullStatus('Connecting to Ollama…')
    try {
      const resp = await fetch('/api/ollama/pull', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ model: pullModel.trim() }),
        signal: abortRef.current.signal,
        credentials: 'include',
      })
      if (!resp.body) { setPullStatus('No response stream.'); return }
      const reader = resp.body.getReader()
      const dec = new TextDecoder()
      let buf = ''
      while (true) {
        const { done, value } = await reader.read()
        if (done) break
        buf += dec.decode(value, { stream: true })
        const lines = buf.split('\n')
        buf = lines.pop() ?? ''
        for (const line of lines) {
          if (!line.startsWith('data: ')) continue
          try {
            const obj = JSON.parse(line.slice(6))
            const status = obj.status ?? ''
            const total = obj.total ? ` (${Math.round(obj.completed / obj.total * 100)}%)` : ''
            setPullStatus(status + total)
          } catch { /* skip malformed */ }
        }
      }
      setPullStatus('Done!')
    } catch (err) {
      if ((err as Error).name !== 'AbortError') setPullStatus('Error: ' + (err as Error).message)
    }
  }

  const handleSaveProfile = async (e: React.FormEvent) => {
    e.preventDefault()
    setProfileError('')
    try {
      const r = await updateProfile(displayName)
      setUser(prev => prev ? { ...prev, displayName: r.data.displayName } : prev)
      setProfileSaved(true)
      setTimeout(() => setProfileSaved(false), 2000)
    } catch {
      setProfileError('Failed to save profile. Please try again.')
    }
  }

  return (
    <>
      <div className="page-header"><h2>Settings</h2></div>

      {/* Profile */}
      <section className="settings-section">
        <h2>Profile</h2>
        <form className="card settings-form" onSubmit={handleSaveProfile}>
          <label>
            Display name
            <input
              value={displayName}
              onChange={e => setDisplayName(e.target.value)}
              maxLength={100}
              placeholder={user?.username ?? ''}
            />
            <span className="hint">Shown publicly as author name. Leave blank to use your login.</span>
          </label>
          <div className="actions">
            <button type="submit">Save Profile</button>
            {profileSaved && <span style={{ color: 'var(--success)', fontSize: '0.85rem' }}>Saved!</span>}
            {profileError && <span style={{ color: 'var(--danger)', fontSize: '0.85rem' }}>{profileError}</span>}
          </div>
        </form>
      </section>

      {/* MCP Access */}
      <section className="settings-section">
        <h2>MCP Access</h2>
        <div className="card settings-form">
          <p style={{ margin: '0 0 0.75rem', color: 'var(--text-secondary)' }}>
            Use this API token to connect an MCP client (Claude Desktop, VS Code Copilot, etc.) to ABook.
          </p>
          {!mcpToken ? (
            <div>
              <p style={{ color: 'var(--text-secondary)' }}>No token generated yet.</p>
              <button type="button" className="btn" onClick={handleRegenMcpToken}>Generate Token</button>
            </div>
          ) : (
            <>
              <label>
                API Token
                <div className="endpoint-row">
                  <input
                    type={mcpTokenVisible ? 'text' : 'password'}
                    readOnly
                    value={mcpToken}
                    style={{ fontFamily: 'monospace' }}
                  />
                  <button type="button" className="btn btn-sm" onClick={() => setMcpTokenVisible(v => !v)}>
                    {mcpTokenVisible ? 'Hide' : 'Show'}
                  </button>
                  <button type="button" className="btn btn-sm" onClick={() => {
                    navigator.clipboard.writeText(mcpToken)
                    setMcpTokenCopied(true)
                    setTimeout(() => setMcpTokenCopied(false), 2000)
                  }}>
                    {mcpTokenCopied ? 'Copied!' : 'Copy'}
                  </button>
                </div>
              </label>
              {!mcpTokenRegenConfirm ? (
                <button type="button" className="btn btn-secondary" onClick={() => setMcpTokenRegenConfirm(true)}>
                  Regenerate Token…
                </button>
              ) : (
                <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
                  <span style={{ color: 'var(--danger)' }}>Old token will stop working immediately.</span>
                  <button type="button" className="btn btn-danger" onClick={handleRegenMcpToken}>Confirm Regenerate</button>
                  <button type="button" className="btn btn-secondary" onClick={() => setMcpTokenRegenConfirm(false)}>Cancel</button>
                </div>
              )}
            </>
          )}
          <details style={{ marginTop: '1rem' }}>
            <summary style={{ cursor: 'pointer', fontWeight: 600 }}>How to connect MCP clients</summary>
            <div style={{ marginTop: '0.75rem', display: 'flex', flexDirection: 'column', gap: '0.75rem' }}>
              <div>
                <strong>MCP endpoint URL:</strong>
                <code style={{ display: 'block', margin: '0.25rem 0', padding: '0.4rem 0.6rem', background: 'var(--bg-secondary)', borderRadius: '4px' }}>
                  http://localhost:5000/mcp
                </code>
              </div>
              <div>
                <strong>Claude Desktop</strong> — add to <code>claude_desktop_config.json</code>:
                <pre style={{ background: 'var(--bg-secondary)', borderRadius: '4px', padding: '0.6rem', overflow: 'auto', fontSize: '0.8rem' }}>{`{
  "mcpServers": {
    "abook": {
      "type": "http",
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN"
      }
    }
  }
}`}</pre>
              </div>
              <div>
                <strong>VS Code / GitHub Copilot</strong> — add to <code>.vscode/mcp.json</code>:
                <pre style={{ background: 'var(--bg-secondary)', borderRadius: '4px', padding: '0.6rem', overflow: 'auto', fontSize: '0.8rem' }}>{`{
  "servers": {
    "abook": {
      "type": "http",
      "url": "http://localhost:5000/mcp",
      "headers": {
        "Authorization": "Bearer YOUR_TOKEN"
      }
    }
  }
}`}</pre>
              </div>
            </div>
          </details>
        </div>
      </section>

      {/* Default preset */}
      <section className="settings-section">
        <h2>Default LLM Preset</h2>
        <div className="card settings-form">
          <p style={{ margin: '0 0 0.75rem', color: 'var(--text-secondary)' }}>
            Every new book gets a copy of this preset's settings. Changing the default (or editing the preset) does not
            affect existing books — change those on each book's Settings page.
          </p>
          {presets.length === 0 ? (
            <p className="hint">You have no presets yet. <Link to="/presets">Create one →</Link></p>
          ) : (
            <label>
              Default preset
              <select value={defaultPresetId ?? ''} onChange={e => handleDefaultChange(Number(e.target.value))}>
                {defaultPresetId === null && <option value="" disabled>— none: new books get no LLM settings —</option>}
                {presets.map(p => (
                  <option key={p.id} value={p.id}>{p.name}{p.userId == null ? ' (shared)' : ''}</option>
                ))}
              </select>
              {defaultPreset && (
                <span className="hint">
                  {PROVIDER_LABELS[defaultPreset.provider] ?? defaultPreset.provider} · {defaultPreset.modelName}
                  {defaultPreset.endpoint ? ` · ${defaultPreset.endpoint}` : ''}
                </span>
              )}
            </label>
          )}
          <div className="actions">
            {defaultSaved && <span className="saved-msg">✓ Saved</span>}
            {defaultError && <span style={{ color: 'var(--danger, red)', fontSize: '0.85em' }}>{defaultError}</span>}
          </div>
          <p style={{ marginTop: '0.5rem' }} className="hint">
            <Link to="/presets">Manage presets →</Link>
          </p>
        </div>
      </section>

      {/* Ollama model pull */}
      {defaultPreset?.provider === 'Ollama' && (
        <section className="settings-section">
          <h2>Pull Ollama Model</h2>
          <div className="card pull-section">
            <div className="pull-row">
              <input
                placeholder="Model name (e.g. llama3.2:3b)"
                value={pullModel}
                onChange={e => setPullModel(e.target.value)}
              />
              <button type="button" onClick={handlePull} disabled={!pullModel.trim()}>Pull</button>
            </div>
            {pullStatus && <p className="pull-status">{pullStatus}</p>}
          </div>
        </section>
      )}

      {/* Browser Notifications */}
      <section className="settings-section">
        <h2>Browser Notifications</h2>
        <div className="card">
          {!notifSupported ? (
            <p className="muted">Browser notifications are not supported in this browser.</p>
          ) : (
            <>
              <label className="toggle-row">
                <span>Notify when agent needs input or workflow completes</span>
                <input
                  type="checkbox"
                  checked={notifEnabled}
                  onChange={e => setNotifEnabled(e.target.checked)}
                />
              </label>
              <p className="muted" style={{ marginTop: '0.5rem' }}>
                {permission === 'granted' && 'Permission granted. Notifications fire only when the tab is in the background.'}
                {permission === 'denied' && 'Permission denied. Enable notifications for this site in your browser settings.'}
                {permission === 'default' && 'Enabling will prompt for permission.'}
              </p>
            </>
          )}
        </div>
      </section>
    </>
  )
}
