import { useEffect, useState, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import type { LlmConfig, LlmPreset, OllamaModel, Book, DefaultPrompts } from '../api'
import { getLlmConfig, updateLlmConfig, updateBook, getBook, getOllamaModels, getDefaultPrompts, getPresets, createPreset, updatePreset, deletePreset } from '../api'

const PROVIDERS = ['Ollama', 'LMStudio', 'OpenAI', 'AzureOpenAI', 'Anthropic'] as const

const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  LMStudio: 'http://host.docker.internal:1234',
  Anthropic: 'http://localhost:4000',  // LiteLLM or other OpenAI-compatible Anthropic proxy
}

// Providers that expose a model list endpoint (via /api/tags or /v1/models)
const MODEL_LIST_PROVIDERS = new Set(['Ollama', 'LMStudio'])

// Providers that need an OpenAI-compatible proxy for Anthropic models (no native SK connector for .NET 10 yet)
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
  const [ollamaModels, setOllamaModels] = useState<OllamaModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)
  const [customModel, setCustomModel] = useState('')
  const [useCustomModel, setUseCustomModel] = useState(false)
  const [useCustomEmbeddingModel, setUseCustomEmbeddingModel] = useState(false)
  const [customEmbeddingModel, setCustomEmbeddingModel] = useState('')
  const [pullModel, setPullModel] = useState('')
  const [pullStatus, setPullStatus] = useState('')
  const [saved, setSaved] = useState(false)
  const [bookSaved, setBookSaved] = useState(false)
  const [defaultPrompts, setDefaultPrompts] = useState<DefaultPrompts | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  // ── Presets state ────────────────────────────────────────────
  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [presetForm, setPresetForm] = useState<Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>>(
    { name: '', provider: 'Ollama', modelName: '', endpoint: '', apiKey: '', embeddingModelName: '' }
  )
  const [editingPresetId, setEditingPresetId] = useState<number | null>(null)
  const [presetSaved, setPresetSaved] = useState(false)

  const fetchModels = (endpoint: string, provider?: string, currentModel?: string, currentEmbeddingModel?: string) => {
    const prov = provider ?? config.provider
    if (!MODEL_LIST_PROVIDERS.has(prov)) {
      setOllamaModels([])
      return
    }
    setModelsLoading(true)
    getOllamaModels(endpoint, prov)
      .then(r => {
        setOllamaModels(r.data)
        // If saved chat model isn't in the list, switch to custom mode
        const model = currentModel ?? config.modelName
        if (model && !r.data.some(m => m.name === model)) {
          setUseCustomModel(true)
          setCustomModel(model)
        }
        // If saved embedding model isn't in the list, switch to custom mode
        const embModel = currentEmbeddingModel ?? config.embeddingModelName ?? ''
        if (embModel && !r.data.some(m => m.name === embModel)) {
          setUseCustomEmbeddingModel(true)
          setCustomEmbeddingModel(embModel)
        }
      })
      .catch(() => {
        setOllamaModels([])
      })
      .finally(() => setModelsLoading(false))
  }

  useEffect(() => {
    getLlmConfig(bookId).then(r => {
      if (r.data) {
        setConfig(r.data)
        fetchModels(r.data.endpoint ?? '', r.data.provider, r.data.modelName, r.data.embeddingModelName ?? '')
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
    const modelName = useCustomModel ? customModel : config.modelName
    const embeddingModelName = useCustomEmbeddingModel ? customEmbeddingModel : config.embeddingModelName
    await updateLlmConfig({ ...config, modelName, embeddingModelName, bookId })
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
  const emptyPresetForm = { name: '', provider: 'Ollama', modelName: '', endpoint: '', apiKey: '', embeddingModelName: '' } as const

  const handlePresetSave = async (e: React.FormEvent) => {
    e.preventDefault()
    if (editingPresetId !== null) {
      const updated = await updatePreset(editingPresetId, presetForm)
      setPresets(ps => ps.map(p => p.id === editingPresetId ? updated.data : p))
    } else {
      const created = await createPreset(presetForm)
      setPresets(ps => [...ps, created.data])
    }
    setPresetForm(emptyPresetForm)
    setEditingPresetId(null)
    setPresetSaved(true)
    setTimeout(() => setPresetSaved(false), 2000)
  }

  const handlePresetEdit = (preset: LlmPreset) => {
    setEditingPresetId(preset.id)
    setPresetForm({
      name: preset.name,
      provider: preset.provider,
      modelName: preset.modelName,
      endpoint: preset.endpoint,
      apiKey: preset.apiKey ?? '',
      embeddingModelName: preset.embeddingModelName ?? '',
    })
  }

  const handlePresetDelete = async (id: number) => {
    if (!confirm('Delete this preset?')) return
    await deletePreset(id)
    setPresets(ps => ps.filter(p => p.id !== id))
    if (editingPresetId === id) { setEditingPresetId(null); setPresetForm(emptyPresetForm) }
  }

  const applyPreset = (preset: LlmPreset) => {
    const defaultEndpoint = DEFAULT_ENDPOINTS[preset.provider] ?? ''
    setConfig({
      ...config,
      provider: preset.provider,
      modelName: preset.modelName,
      endpoint: preset.endpoint || defaultEndpoint,
      apiKey: preset.apiKey ?? undefined,
      embeddingModelName: preset.embeddingModelName ?? undefined,
    })
    setUseCustomModel(false)
    setUseCustomEmbeddingModel(false)
    fetchModels(preset.endpoint || defaultEndpoint, preset.provider, preset.modelName, preset.embeddingModelName ?? '')
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
              fetchModels(config.endpoint, config.provider)
            }
          } catch { /* skip malformed */ }
        }
      }
      setPullStatus('Done!')
    } catch (err) {
      if ((err as Error).name !== 'AbortError') setPullStatus('Error: ' + (err as Error).message)
    }
  }

  const fetchedModelNames = ollamaModels.map(m => m.name)

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
              setOllamaModels([])
              setUseCustomModel(false)
              setUseCustomEmbeddingModel(false)
              fetchModels(newEndpoint, prov)
            }}>
              {PROVIDERS.map(p => <option key={p}>{p}</option>)}
            </select>
          </label>
          <label>
            Model
            <div className="model-selector">
              <select
                value={useCustomModel ? '__custom__' : config.modelName}
                onChange={e => {
                  if (e.target.value === '__custom__') { setUseCustomModel(true) }
                  else { setUseCustomModel(false); setConfig(c => ({ ...c, modelName: e.target.value })) }
                }}
              >
                {fetchedModelNames.length === 0 && !useCustomModel && (
                  <option value={config.modelName}>{config.modelName || '— no models fetched —'}</option>
                )}
                {fetchedModelNames.map(m => <option key={m} value={m}>{m}</option>)}
                <option value="__custom__">— Custom model name —</option>
              </select>
              {useCustomModel && (
                <input
                  placeholder="e.g. llama3.2:3b"
                  value={customModel}
                  onChange={e => setCustomModel(e.target.value)}
                />
              )}
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
              onClick={() => fetchModels(config.endpoint, config.provider)}
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
          </label>
          <label>
            Embedding Model
            {MODEL_LIST_PROVIDERS.has(config.provider) ? (
              <div className="model-selector">
                <select
                  value={useCustomEmbeddingModel ? '__custom__' : (config.embeddingModelName ?? '')}
                  onChange={e => {
                    if (e.target.value === '__custom__') { setUseCustomEmbeddingModel(true) }
                    else { setUseCustomEmbeddingModel(false); setConfig(c => ({ ...c, embeddingModelName: e.target.value })) }
                  }}
                >
                  {fetchedModelNames.length === 0 && !useCustomEmbeddingModel && (
                    <option value={config.embeddingModelName ?? ''}>{config.embeddingModelName || '— no models fetched —'}</option>
                  )}
                  {fetchedModelNames.map(m => <option key={m} value={m}>{m}</option>)}
                  <option value="__custom__">— Custom model name —</option>
                </select>
                {useCustomEmbeddingModel && (
                  <input
                    placeholder="e.g. nomic-embed-text"
                    value={customEmbeddingModel}
                    onChange={e => setCustomEmbeddingModel(e.target.value)}
                  />
                )}
                {modelsLoading && <span className="hint">Loading…</span>}
              </div>
            ) : (
              <input
                placeholder="e.g. text-embedding-3-small"
                value={config.embeddingModelName ?? ''}
                onChange={e => setConfig(c => ({ ...c, embeddingModelName: e.target.value }))}
              />
            )}
          </label>
          <label>
            API Key (optional)
            <input type="password" value={config.apiKey ?? ''} onChange={e => setConfig(c => ({ ...c, apiKey: e.target.value }))} />
          </label>
          <button type="submit">Save LLM Config</button>
          {saved && <span className="saved-msg">✓ Saved</span>}
        </form>
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

      {/* Credential Presets */}
      <section className="settings-section">
        <h2>Credential Presets</h2>
        <p className="hint">Save named LLM configurations that can be quickly applied to any book's settings.</p>

        {presets.length > 0 && (
          <div className="card presets-list">
            {presets.map(p => (
              <div key={p.id} className="preset-row">
                <div className="preset-info">
                  <strong>{p.name}</strong>
                  <span className="preset-meta">{p.provider} · {p.modelName} · {p.endpoint}</span>
                  {p.embeddingModelName && <span className="preset-meta">embed: {p.embeddingModelName}</span>}
                  {!p.userId && <span className="preset-badge">global</span>}
                </div>
                <div className="preset-actions">
                  <button type="button" className="btn-secondary btn-sm" onClick={() => handlePresetEdit(p)}>Edit</button>
                  <button type="button" className="btn-danger btn-sm" onClick={() => handlePresetDelete(p.id)}>Delete</button>
                </div>
              </div>
            ))}
          </div>
        )}

        <form className="card settings-form" onSubmit={handlePresetSave}>
          <h3 style={{ margin: '0 0 0.5rem' }}>{editingPresetId !== null ? 'Edit Preset' : 'New Preset'}</h3>
          <label>
            Preset Name
            <input
              required
              placeholder="e.g. Local Ollama"
              value={presetForm.name}
              onChange={e => setPresetForm(f => ({ ...f, name: e.target.value }))}
            />
          </label>
          <label>
            Provider
            <select value={presetForm.provider} onChange={e => setPresetForm(f => ({ ...f, provider: e.target.value }))}>
              {PROVIDERS.map(p => <option key={p}>{p}</option>)}
            </select>
          </label>
          <label>
            Model Name
            <input
              required
              placeholder="e.g. llama3"
              value={presetForm.modelName}
              onChange={e => setPresetForm(f => ({ ...f, modelName: e.target.value }))}
            />
          </label>
          <label>
            Endpoint
            <input
              placeholder={DEFAULT_ENDPOINTS[presetForm.provider] ?? ''}
              value={presetForm.endpoint}
              onChange={e => setPresetForm(f => ({ ...f, endpoint: e.target.value }))}
            />
          </label>
          <label>
            Embedding Model (optional)
            <input
              placeholder="e.g. nomic-embed-text"
              value={presetForm.embeddingModelName ?? ''}
              onChange={e => setPresetForm(f => ({ ...f, embeddingModelName: e.target.value }))}
            />
          </label>
          <label>
            API Key (optional)
            <input
              type="password"
              value={presetForm.apiKey ?? ''}
              onChange={e => setPresetForm(f => ({ ...f, apiKey: e.target.value }))}
            />
          </label>
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center' }}>
            <button type="submit">{editingPresetId !== null ? 'Update Preset' : 'Save Preset'}</button>
            {editingPresetId !== null && (
              <button type="button" className="btn-secondary" onClick={() => { setEditingPresetId(null); setPresetForm(emptyPresetForm) }}>
                Cancel
              </button>
            )}
            {presetSaved && <span className="saved-msg">✓ Saved</span>}
          </div>
        </form>
      </section>
    </div>
  )
}
