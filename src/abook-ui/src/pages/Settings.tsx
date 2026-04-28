import { useEffect, useState, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import type { LlmConfig, LlmPreset, ProviderModel, Book, DefaultPrompts } from '../api'
import { getLlmConfig, updateLlmConfig, updateBook, getBook, getModels, getDefaultPrompts, getPresets, createPreset, updatePreset } from '../api'

const PROVIDERS = ['Ollama', 'OpenAI', 'AzureOpenAI', 'Anthropic', 'GoogleAIStudio'] as const

const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  Anthropic: 'http://localhost:4000',  // LiteLLM or other OpenAI-compatible Anthropic proxy
  GoogleAIStudio: 'https://generativelanguage.googleapis.com/v1beta/openai',
}

// Providers that expose a model list endpoint (fetched server-side via /api/models)
const MODEL_LIST_PROVIDERS = new Set(['Ollama', 'OpenAI', 'GoogleAIStudio'])

// Providers that need an API key to fetch the model list
const API_KEY_REQUIRED_PROVIDERS = new Set(['OpenAI', 'GoogleAIStudio'])

// Providers that need an OpenAI-compatible proxy (no native SK connector)
const PROXY_REQUIRED_PROVIDERS = new Set(['Anthropic'])

export default function Settings() {
  const { id } = useParams<{ id: string }>()
  const bookId = id ? Number(id) : undefined

  const [config, setConfig] = useState<LlmConfig>({
    provider: 'Ollama', modelName: 'llama3',
    endpoint: 'http://host.docker.internal:11434', embeddingModelName: 'nomic-embed-text'
  })
  const [book, setBook] = useState<Book | null>(null)
  const [bookForm, setBookForm] = useState({
    language: 'English',
    storyBibleSystemPrompt: '', charactersSystemPrompt: '',
    plotThreadsSystemPrompt: '', chapterOutlinesSystemPrompt: '',
    writerSystemPrompt: '', editorSystemPrompt: '', continuityCheckerSystemPrompt: ''
  })
  const [models, setModels] = useState<ProviderModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)
  const [pullModel, setPullModel] = useState('')
  const [pullStatus, setPullStatus] = useState('')
  const [saved, setSaved] = useState(false)
  const [bookSaved, setBookSaved] = useState(false)
  const [defaultPrompts, setDefaultPrompts] = useState<DefaultPrompts | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  // ── Presets state (for dropdown + save-as-preset) ─────────────
  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [showSaveAsPreset, setShowSaveAsPreset] = useState(false)
  const [saveAsPresetName, setSaveAsPresetName] = useState('')
  const [presetSaved, setPresetSaved] = useState(false)

  const fetchModels = (endpoint: string, provider?: string, apiKey?: string) => {
    const prov = provider ?? config.provider
    if (!MODEL_LIST_PROVIDERS.has(prov)) {
      setModels([])
      return
    }
    setModelsLoading(true)
    getModels(endpoint, prov, apiKey)
      .then(r => setModels(r.data))
      .catch(() => setModels([]))
      .finally(() => setModelsLoading(false))
  }

  useEffect(() => {
    getLlmConfig(bookId).then(r => {
      if (r.data) {
        setConfig(r.data)
        fetchModels(r.data.endpoint ?? '', r.data.provider, r.data.apiKey ?? undefined)
      }
    })
    getPresets().then(r => setPresets(r.data)).catch(() => {})
    if (bookId) {
      getBook(bookId).then(r => {
        setBook(r.data)
        setBookForm({
          language: r.data.language ?? 'English',
          storyBibleSystemPrompt: r.data.storyBibleSystemPrompt ?? '',
          charactersSystemPrompt: r.data.charactersSystemPrompt ?? '',
          plotThreadsSystemPrompt: r.data.plotThreadsSystemPrompt ?? '',
          chapterOutlinesSystemPrompt: r.data.chapterOutlinesSystemPrompt ?? '',
          writerSystemPrompt: r.data.writerSystemPrompt ?? '',
          editorSystemPrompt: r.data.editorSystemPrompt ?? '',
          continuityCheckerSystemPrompt: r.data.continuityCheckerSystemPrompt ?? '',
        })
      })
      getDefaultPrompts(bookId).then(r => setDefaultPrompts(r.data)).catch(() => {})
    }
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [bookId])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    await updateLlmConfig({ ...config, bookId })
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const handleBookSave = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!book) return
    await updateBook(book.id, {
      ...book,
      language: bookForm.language,
      storyBibleSystemPrompt: bookForm.storyBibleSystemPrompt || undefined,
      charactersSystemPrompt: bookForm.charactersSystemPrompt || undefined,
      plotThreadsSystemPrompt: bookForm.plotThreadsSystemPrompt || undefined,
      chapterOutlinesSystemPrompt: bookForm.chapterOutlinesSystemPrompt || undefined,
      writerSystemPrompt: bookForm.writerSystemPrompt || undefined,
      editorSystemPrompt: bookForm.editorSystemPrompt || undefined,
      continuityCheckerSystemPrompt: bookForm.continuityCheckerSystemPrompt || undefined,
    })
    setBookSaved(true)
    setTimeout(() => setBookSaved(false), 2000)
  }

  // ── Preset handlers ──────────────────────────────────────────────────────
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
    // Only check user-owned presets (userId !== null) for duplicates
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
    <div className="container settings-page">
      <Link to={bookId ? `/books/${bookId}` : '/'} className="back-link">← Back</Link>
      <h1>Settings {bookId ? `— Book #${bookId}` : '(Global)'}</h1>

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
                placeholder="e.g. gemini-2.0-flash"
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
                placeholder={config.provider === 'GoogleAIStudio' ? 'e.g. text-embedding-004' : 'e.g. text-embedding-3-small'}
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

      {/* Ollama model pull — only relevant for Ollama */}
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

      {/* Book-specific settings */}
      {bookId && book && (
        <section className="settings-section">
          <h2>Book Settings</h2>
          <form className="card settings-form" onSubmit={handleBookSave}>
            <label>
              Language
              <input
                value={bookForm.language}
                onChange={e => setBookForm(f => ({ ...f, language: e.target.value }))}
                placeholder="English"
              />
            </label>
            <details>
              <summary>Custom Agent System Prompts (optional)</summary>
              <p className="hint">Leave blank to use agent defaults, or click "Load Defaults" to start editing from the defaults.</p>
              <div className="prompt-placeholders hint">
                <strong>Available placeholders</strong> (substituted at runtime):{' '}
                <code>{'{TITLE}'}</code> <code>{'{GENRE}'}</code> <code>{'{PREMISE}'}</code>{' '}
                <code>{'{LANGUAGE}'}</code> <code>{'{CHAPTER_COUNT}'}</code>{' '}
                <code>{'{SETTING}'}</code> <code>{'{THEMES}'}</code>{' '}
                <code>{'{TONE}'}</code> <code>{'{WORLD_RULES}'}</code>
                <br />
                Story Bible placeholders (<code>{'{SETTING}'}</code>, <code>{'{THEMES}'}</code>,{' '}
                <code>{'{TONE}'}</code>, <code>{'{WORLD_RULES}'}</code>) are available once Phase 1 is complete.
                <br />
                <strong>Editor prompt note:</strong> end your prompt with a section headed{' '}
                <code>## Editorial Notes</code> — the agent will split this off and store it as feedback.
              </div>
              {defaultPrompts && (
                <button
                  type="button"
                  className="btn-secondary"
                  onClick={() => setBookForm(f => ({
                    ...f,
                    storyBibleSystemPrompt: f.storyBibleSystemPrompt || defaultPrompts.storyBibleSystemPrompt,
                    charactersSystemPrompt: f.charactersSystemPrompt || defaultPrompts.charactersSystemPrompt,
                    plotThreadsSystemPrompt: f.plotThreadsSystemPrompt || defaultPrompts.plotThreadsSystemPrompt,
                    chapterOutlinesSystemPrompt: f.chapterOutlinesSystemPrompt || defaultPrompts.chapterOutlinesSystemPrompt,
                    writerSystemPrompt: f.writerSystemPrompt || defaultPrompts.writerSystemPrompt,
                    editorSystemPrompt: f.editorSystemPrompt || defaultPrompts.editorSystemPrompt,
                    continuityCheckerSystemPrompt: f.continuityCheckerSystemPrompt || defaultPrompts.continuityCheckerSystemPrompt,
                  }))}
                >
                  Load Defaults
                </button>
              )}
              {([
                ['storyBible', 'Story Bible Agent'],
                ['characters', 'Characters Agent'],
                ['plotThreads', 'Plot Threads Agent'],
                ['chapterOutlines', 'Chapter Outlines Agent'],
                ['writer', 'Writer Agent'],
                ['editor', 'Editor Agent'],
                ['continuityChecker', 'Continuity Checker Agent'],
              ] as const).map(([role, label]) => {
                const key = `${role}SystemPrompt` as keyof typeof bookForm
                const defaultText = defaultPrompts?.[key as keyof DefaultPrompts]
                return (
                  <label key={role}>
                    {label} prompt
                    <textarea
                      rows={6}
                      value={bookForm[key]}
                      onChange={e => setBookForm(f => ({ ...f, [key]: e.target.value }))}
                      placeholder={defaultText ?? `Custom system prompt for ${label}…`}
                    />
                  </label>
                )
              })}
            </details>
            <button type="submit">Save Book Settings</button>
            {bookSaved && <span className="saved-msg">✓ Saved</span>}
          </form>
        </section>
      )}

    </div>
  )
}
