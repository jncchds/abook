import { useEffect, useState, useRef } from 'react'
import { Link } from 'react-router-dom'
import type { LlmConfig, LlmPreset, ProviderModel } from '../api'
import { getLlmConfig, updateLlmConfig, getModels, getPresets, createPreset, updatePreset, getApiToken, regenerateApiToken } from '../api'

const PROVIDERS = ['Ollama', 'OpenAI', 'AzureOpenAI', 'Anthropic', 'GoogleAIStudio'] as const

const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  Anthropic: 'http://localhost:4000',
  GoogleAIStudio: 'https://generativelanguage.googleapis.com/v1beta/openai',
}

const MODEL_LIST_PROVIDERS = new Set(['Ollama', 'OpenAI', 'GoogleAIStudio'])
const API_KEY_REQUIRED_PROVIDERS = new Set(['OpenAI', 'GoogleAIStudio'])
const PROXY_REQUIRED_PROVIDERS = new Set(['Anthropic'])

export default function GlobalSettings() {
  const [config, setConfig] = useState<LlmConfig>({
    provider: 'Ollama', modelName: 'llama3',
    endpoint: 'http://host.docker.internal:11434', embeddingModelName: 'nomic-embed-text'
  })
  const [models, setModels] = useState<ProviderModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)
  const [pullModel, setPullModel] = useState('')
  const [pullStatus, setPullStatus] = useState('')
  const [saved, setSaved] = useState(false)
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
    getPresets().then(r => setPresets(r.data)).catch(() => {})
    getApiToken().then(r => setMcpToken(r.data.token)).catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    await updateLlmConfig(config)
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
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

  return (
    <>
      <h1>Settings</h1>

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
              {PROVIDERS.map(p => <option key={p}>{p}</option>)}
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
            {PROXY_REQUIRED_PROVIDERS.has(config.provider) && (
              <span className="hint">
                Anthropic's API is not OpenAI-compatible. Point this endpoint at an OpenAI-compatible proxy
                (e.g. <a href="https://docs.litellm.ai" target="_blank" rel="noreferrer">LiteLLM</a> at{' '}
                <code>http://localhost:4000</code>).
              </span>
            )}
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
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', flexWrap: 'wrap', marginTop: '0.25rem' }}>
            <button type="submit">Save LLM Config</button>
            {saved && <span className="saved-msg">✓ Saved</span>}
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
    </>
  )
}
