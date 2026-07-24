import { useEffect, useState, useRef } from 'react'
import { Link } from 'react-router-dom'
import type { LlmConfig, LlmPreset, ProviderModel } from '../api'
import { getLlmConfig, updateLlmConfig, getModels, getPresets, createPreset, updatePreset, getApiToken, regenerateApiToken, updateProfile } from '../api'
import { PROVIDERS, PROVIDER_LABELS, DEFAULT_ENDPOINTS, MODEL_LIST_PROVIDERS, API_KEY_REQUIRED_PROVIDERS, INITIAL_LLM_CONFIG } from '../config/providers'
import { useNotifications } from '../hooks/useNotifications'
import { useAuth } from '../hooks/useAuth'

export default function GlobalSettings() {
  const { user, setUser } = useAuth()
  const [config, setConfig] = useState<LlmConfig>({ ...INITIAL_LLM_CONFIG, temperature: undefined as number | undefined, timeoutMs: undefined as number | undefined, reasoningEffort: undefined as string | undefined, maxTokens: undefined as number | undefined })
  const { supported: notifSupported, permission, enabled: notifEnabled, setEnabled: setNotifEnabled } = useNotifications()
  const [models, setModels] = useState<ProviderModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)
  const [pullModel, setPullModel] = useState('')
  const [pullStatus, setPullStatus] = useState('')
  const [saved, setSaved] = useState(false)

  const [displayName, setDisplayName] = useState('')
  const [profileSaved, setProfileSaved] = useState(false)
  const [profileError, setProfileError] = useState('')

  useEffect(() => {
    setDisplayName(user?.displayName ?? user?.username ?? '')
  }, [user])
  const [saveError, setSaveError] = useState('')
  const abortRef = useRef<AbortController | null>(null)

  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [showSaveAsPreset, setShowSaveAsPreset] = useState(false)
  const [saveAsPresetName, setSaveAsPresetName] = useState('')
  const [presetSaved, setPresetSaved] = useState(false)

  const [mcpToken, setMcpToken] = useState<string | null>(null)
  const [mcpTokenVisible, setMcpTokenVisible] = useState(false)
  const [mcpTokenCopied, setMcpTokenCopied] = useState(false)
  const [mcpTokenRegenConfirm, setMcpTokenRegenConfirm] = useState(false)

  const fetchModels = (endpoint: string, provider?: string, apiKey?: string) => {
    const prov = provider ?? config.provider
    if (!MODEL_LIST_PROVIDERS.has(prov)) { setModels([]); return }
    setModelsLoading(true)
    getModels(endpoint, prov, apiKey)
      .then(r => setModels(r.data))
      .catch(() => setModels([]))
      .finally(() => setModelsLoading(false))
  }

  useEffect(() => {
    getLlmConfig(undefined).then(r => {
      if (r.data) {
        setConfig(r.data)
        fetchModels(r.data.endpoint ?? '', r.data.provider, r.data.apiKey ?? undefined)
      }
    })
    getPresets().then(r => setPresets(r.data)).catch(err => console.error('Failed to load presets', err))
    getApiToken().then(r => setMcpToken(r.data.token)).catch(err => console.error('Failed to load MCP token', err))
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    setSaveError('')
    try {
      await updateLlmConfig(config)
      setSaved(true)
      setTimeout(() => setSaved(false), 2000)
    } catch (err) {
      setSaveError('Failed to save LLM config. Please try again.')
      console.error('Failed to save LLM config', err)
    }
  }

  const handleSaveAsPreset = async () => {
    const name = saveAsPresetName.trim()
    if (!name) return
    const data = {
      name,
      provider: config.provider,
      modelName: config.modelName,
      endpoint: config.endpoint,
      apiKey: config.apiKey ?? '',
      embeddingModelName: config.embeddingModelName ?? '',
      temperature: config.temperature || undefined,
      timeoutMs: config.timeoutMs || undefined,
      reasoningEffort: config.reasoningEffort || undefined,
      maxTokens: config.maxTokens || undefined,
    }
    const existing = presets.find(p => p.userId !== null && p.name.toLowerCase() === name.toLowerCase())
    if (existing) {
      if (!confirm(`A preset named "${name}" already exists. Overwrite it?`)) return
      const updated = await updatePreset(existing.id, data)
      setPresets(ps => ps.map(p => p.id === existing.id ? updated.data : p))
    } else {
      const created = await createPreset(data)
      setPresets(ps => [...ps, created.data])
    }
    setShowSaveAsPreset(false)
    setPresetSaved(true)
    setTimeout(() => setPresetSaved(false), 2000)
  }

  const applyPreset = (preset: LlmPreset) => {
    const defaultEndpoint = DEFAULT_ENDPOINTS[preset.provider] ?? ''
    const newConfig = {
      ...config,
      provider: preset.provider,
      modelName: preset.modelName,
      endpoint: preset.endpoint || defaultEndpoint,
      apiKey: preset.apiKey ?? undefined,
      embeddingModelName: preset.embeddingModelName ?? undefined,
      temperature: preset.temperature ?? undefined,
      timeoutMs: preset.timeoutMs ?? undefined,
      reasoningEffort: preset.reasoningEffort ?? undefined,
      maxTokens: preset.maxTokens ?? undefined,
    }
    setConfig(newConfig)
    fetchModels(newConfig.endpoint, newConfig.provider, newConfig.apiKey)
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
            if (status === 'success') {
              fetchModels(config.endpoint, config.provider, config.apiKey)
            }
          } catch { /* skip malformed */ }
        }
      }
      setPullStatus('Done!')
    } catch (err) {
      if ((err as Error).name !== 'AbortError') setPullStatus('Error: ' + (err as Error).message)
    }
  }

  const fetchedModelNames = models.map(m => m.name)

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

      {/* LLM Config */}
      <section className="settings-section">
        <h2>LLM Configuration</h2>
        <form className="card settings-form" onSubmit={handleSave}>
          {presets.length > 0 && (
            <label>
              Apply Preset
              <div className="endpoint-row">
                <select defaultValue="" onChange={e => {
                  const p = presets.find(x => x.id === Number(e.target.value))
                  if (p) applyPreset(p)
                  e.target.value = ''
                }}>
                  <option value="" disabled>— select a preset —</option>
                  {presets.map(p => <option key={p.id} value={p.id}>{p.name}</option>)}
                </select>
              </div>
              <span className="hint">Selecting a preset pre-fills the fields below; save to persist.</span>
            </label>
          )}
          <label>
            Provider
            <select value={config.provider} onChange={e => {
              const prov = e.target.value
              const defaultEndpoint = DEFAULT_ENDPOINTS[prov] ?? ''
              const newEndpoint = DEFAULT_ENDPOINTS[config.provider] === config.endpoint
                ? defaultEndpoint
                : config.endpoint
              setConfig(c => ({ ...c, provider: prov, endpoint: newEndpoint }))
              setModels([])
              fetchModels(newEndpoint, prov, config.apiKey)
            }}>
              {PROVIDERS.map(p => <option key={p} value={p}>{PROVIDER_LABELS[p]}</option>)}
            </select>
          </label>
          <label>
            Model
            <div className="model-selector">
              <input
                list="models-list"
                value={config.modelName}
                onChange={e => setConfig(c => ({ ...c, modelName: e.target.value }))}
                placeholder="e.g. llama3"
                required
              />
              <datalist id="models-list">
                {fetchedModelNames.map(m => <option key={m} value={m} />)}
              </datalist>
              {modelsLoading && <span className="hint">Loading…</span>}
            </div>
          </label>
          <label>
            Endpoint
            <div className="endpoint-row">
              <input value={config.endpoint} onChange={e => setConfig(c => ({ ...c, endpoint: e.target.value }))} required />
              <button
                type="button"
                className="btn-secondary btn-sm"
                title="Refresh model list from this endpoint"
                onClick={() => fetchModels(config.endpoint, config.provider, config.apiKey)}
                disabled={modelsLoading}
              >{modelsLoading ? '…' : '↺ Refresh'}</button>
            </div>

            {API_KEY_REQUIRED_PROVIDERS.has(config.provider) && (
              <span className="hint">
                Enter your API key below, then click ↺ Refresh to load the model list.{' '}
                {config.provider === 'GoogleAIStudio' && (
                  <a href="https://aistudio.google.com/apikey" target="_blank" rel="noreferrer">Get a key at Google AI Studio →</a>
                )}
              </span>
            )}
          </label>
          <label>
            Embedding Model
            <div className="model-selector">
              <input
                list="embedding-list"
                value={config.embeddingModelName ?? ''}
                onChange={e => setConfig(c => ({ ...c, embeddingModelName: e.target.value }))}
                placeholder={config.provider === 'GoogleAIStudio' ? 'e.g. text-embedding-004' : 'e.g. nomic-embed-text'}
              />
              <datalist id="embedding-list">
                {fetchedModelNames.map(m => <option key={m} value={m} />)}
              </datalist>
              {modelsLoading && <span className="hint">Loading…</span>}
            </div>
          </label>
          <label>
            API Key (optional)
            <input type="password" value={config.apiKey ?? ''} onChange={e => setConfig(c => ({ ...c, apiKey: e.target.value }))} />
          </label>
          <details style={{ marginTop: '0.75rem' }}>
            <summary style={{ cursor: 'pointer', fontWeight: 600, userSelect: 'none' }}>⚙ Advanced LLM Parameters</summary>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem', marginTop: '0.75rem' }}>
              <label>
                Temperature (0–1)
                <input
                  type="text"
                  inputMode="decimal"
                  placeholder="default (0.8)"
                  value={config.temperature ?? ''}
                  onChange={e => setConfig(c => ({ ...c, temperature: e.target.value === '' ? undefined : parseFloat(e.target.value) }))}
                />
                <span className="hint">Higher = more creative. Leave blank for default.</span>
              </label>
              <label>
                Timeout (ms)
                <input
                  type="text"
                  inputMode="numeric"
                  pattern="[0-9]+"
                  placeholder="default (no timeout)"
                  value={config.timeoutMs ?? ''}
                  onChange={e => setConfig(c => ({ ...c, timeoutMs: e.target.value === '' ? undefined : parseInt(e.target.value, 10) }))}
                />
                <span className="hint">Request timeout. Leave blank for no override.</span>
              </label>
              {config.provider !== 'OpenAICompatible' && (
              <label>
                Reasoning effort
                <select value={config.reasoningEffort ?? ''} onChange={e => setConfig(c => ({ ...c, reasoningEffort: e.target.value || undefined }))}>
                  <option value="">Default</option>
                  <option value="none">Disabled</option>
                  <option value="low">Low</option>
                  <option value="medium">Medium</option>
                  <option value="high">High</option>
                </select>
                <span className="hint">For reasoning models (DeepSeek-R1, Qwen3). Leave blank for model default.</span>
              </label>
              )}
              <label>
                Max tokens
                <input
                  type="text"
                  inputMode="numeric"
                  pattern="[0-9]+"
                  placeholder="default (provider default)"
                  value={config.maxTokens ?? ''}
                  onChange={e => setConfig(c => ({ ...c, maxTokens: e.target.value === '' ? undefined : parseInt(e.target.value, 10) }))}
                />
                <span className="hint">Maximum output tokens. Leave blank for provider default.</span>
              </label>
            </div>
          </details>
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap', marginTop: '0.25rem' }}>
            <button type="submit">Save LLM Config</button>
            {saved && <span className="saved-msg">✓ Saved</span>}
            {saveError && <span style={{ color: 'var(--danger, red)', fontSize: '0.85em' }}>{saveError}</span>}
            {!showSaveAsPreset && (
              <button
                type="button"
                className="btn-secondary"
                onClick={() => {
                  setSaveAsPresetName(`${config.provider} - ${config.modelName}`)
                  setShowSaveAsPreset(true)
                  setPresetSaved(false)
                }}
              >
                Save as Preset…
              </button>
            )}
          </div>
          {showSaveAsPreset && (
            <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap', marginTop: '0.5rem' }}>
              <input
                style={{ flex: 1, minWidth: '180px' }}
                placeholder="Preset name"
                value={saveAsPresetName}
                onChange={e => setSaveAsPresetName(e.target.value)}
                onKeyDown={e => { if (e.key === 'Enter') { e.preventDefault(); handleSaveAsPreset() } }}
                autoFocus
              />
              <button type="button" onClick={handleSaveAsPreset} disabled={!saveAsPresetName.trim()}>Save Preset</button>
              <button type="button" className="btn-secondary" onClick={() => setShowSaveAsPreset(false)}>Cancel</button>
              {presetSaved && <span className="saved-msg">✓ Preset saved</span>}
            </div>
          )}
        </form>
        <p style={{ marginTop: '0.5rem' }} className="hint">
          <Link to="/presets">Manage credential presets →</Link>
        </p>
      </section>

      {/* Ollama model pull */}
      {config.provider === 'Ollama' && (
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
