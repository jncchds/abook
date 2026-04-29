import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import type { LlmConfig, LlmPreset, ProviderModel, Book, DefaultPrompts } from '../../api'
import { getLlmConfig, updateLlmConfig, updateBook, getBook, getModels, getDefaultPrompts, getPresets, createPreset, updatePreset } from '../../api'
import { PROVIDERS, DEFAULT_ENDPOINTS, MODEL_LIST_PROVIDERS, API_KEY_REQUIRED_PROVIDERS, PROXY_REQUIRED_PROVIDERS, INITIAL_LLM_CONFIG } from '../../config/providers'

export default function BookSettings() {
  const { bookId } = useParams<{ bookId: string }>()
  const id = Number(bookId)

  const [config, setConfig] = useState<LlmConfig>({ ...INITIAL_LLM_CONFIG })
  const [book, setBook] = useState<Book | null>(null)
  const [bookForm, setBookForm] = useState({
    language: 'English',
    humanAssisted: false,
    storyBibleSystemPrompt: '', charactersSystemPrompt: '',
    plotThreadsSystemPrompt: '', chapterOutlinesSystemPrompt: '',
    writerSystemPrompt: '', editorSystemPrompt: '', continuityCheckerSystemPrompt: ''
  })
  const [models, setModels] = useState<ProviderModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)
  const [saved, setSaved] = useState(false)
  const [bookSaved, setBookSaved] = useState(false)
  const [defaultPrompts, setDefaultPrompts] = useState<DefaultPrompts | null>(null)

  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [showSaveAsPreset, setShowSaveAsPreset] = useState(false)
  const [saveAsPresetName, setSaveAsPresetName] = useState('')
  const [presetSaved, setPresetSaved] = useState(false)

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
    getLlmConfig(id).then(r => {
      if (r.data) {
        setConfig(r.data)
        fetchModels(r.data.endpoint ?? '', r.data.provider, r.data.apiKey ?? undefined)
      }
    })
    getPresets().then(r => setPresets(r.data)).catch(() => {})
    getBook(id).then(r => {
      setBook(r.data)
      setBookForm({
        language: r.data.language ?? 'English',
        humanAssisted: r.data.humanAssisted ?? false,
        storyBibleSystemPrompt: r.data.storyBibleSystemPrompt ?? '',
        charactersSystemPrompt: r.data.charactersSystemPrompt ?? '',
        plotThreadsSystemPrompt: r.data.plotThreadsSystemPrompt ?? '',
        chapterOutlinesSystemPrompt: r.data.chapterOutlinesSystemPrompt ?? '',
        writerSystemPrompt: r.data.writerSystemPrompt ?? '',
        editorSystemPrompt: r.data.editorSystemPrompt ?? '',
        continuityCheckerSystemPrompt: r.data.continuityCheckerSystemPrompt ?? '',
      })
    })
    getDefaultPrompts(id).then(r => setDefaultPrompts(r.data)).catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [id])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    await updateLlmConfig({ ...config, bookId: id })
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const handleBookSave = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!book) return
    await updateBook(book.id, {
      ...book,
      language: bookForm.language,
      humanAssisted: bookForm.humanAssisted,
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

  const fetchedModelNames = models.map(m => m.name)

  return (
    <>
      <h1>Book Settings</h1>

      {/* LLM Config (book-scoped) */}
      <section className="settings-section">
        <h2>LLM Configuration</h2>
        <p className="hint" style={{ marginBottom: '0.75rem' }}>
          These settings override the global LLM configuration for this book only.
        </p>
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

      {/* Book-specific settings */}
      {book && (
        <section className="settings-section">
          <h2>Agent Prompts &amp; Language</h2>
          <form className="card settings-form" onSubmit={handleBookSave}>
            <label>
              Language
              <input
                value={bookForm.language}
                onChange={e => setBookForm(f => ({ ...f, language: e.target.value }))}
                placeholder="English"
              />
            </label>
            <label style={{ flexDirection: 'row', gap: '0.5rem', alignItems: 'center' }}>
              <input
                type="checkbox"
                checked={bookForm.humanAssisted}
                onChange={e => setBookForm(f => ({ ...f, humanAssisted: e.target.checked }))}
              />
              Human-assisted generation (pause for your input after each planning phase and during the Checker-Editor loop)
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
                ['continuityChecker', 'Checker Agent'],
              ] as const).map(([role, label]) => {
                const key = `${role}SystemPrompt` as keyof typeof bookForm
                const defaultText = defaultPrompts?.[key as keyof DefaultPrompts]
                return (
                  <label key={role}>
                    {label} prompt
                    <textarea
                      rows={6}
                      value={bookForm[key] as string}
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
    </>
  )
}
