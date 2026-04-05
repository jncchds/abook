import { useEffect, useState, useRef } from 'react'
import { useParams, Link } from 'react-router-dom'
import type { LlmConfig, OllamaModel, Book, DefaultPrompts } from '../api'
import { getLlmConfig, updateLlmConfig, updateBook, getBook, getOllamaModels, getDefaultPrompts } from '../api'

const PROVIDERS = ['Ollama', 'OpenAI', 'AzureOpenAI', 'Anthropic'] as const
const COMMON_MODELS = [
  'llama3', 'llama3:70b', 'llama3.1', 'llama3.2', 'llama3.2:3b',
  'mistral', 'mixtral', 'gemma2', 'gemma2:27b', 'phi3', 'phi4',
  'qwen2.5', 'qwen2.5:14b', 'codellama', 'deepseek-r1',
]

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
    plannerSystemPrompt: '', writerSystemPrompt: '',
    editorSystemPrompt: '', continuityCheckerSystemPrompt: ''
  })
  const [ollamaModels, setOllamaModels] = useState<OllamaModel[]>([])
  const [customModel, setCustomModel] = useState('')
  const [useCustomModel, setUseCustomModel] = useState(false)
  const [pullModel, setPullModel] = useState('')
  const [pullStatus, setPullStatus] = useState('')
  const [saved, setSaved] = useState(false)
  const [bookSaved, setBookSaved] = useState(false)
  const [defaultPrompts, setDefaultPrompts] = useState<DefaultPrompts | null>(null)
  const abortRef = useRef<AbortController | null>(null)

  useEffect(() => {
    getLlmConfig(bookId).then(r => { if (r.data) setConfig(r.data) })
    if (bookId) {
      getBook(bookId).then(r => {
        setBook(r.data)
        setBookForm({
          language: r.data.language ?? 'English',
          plannerSystemPrompt: r.data.plannerSystemPrompt ?? '',
          writerSystemPrompt: r.data.writerSystemPrompt ?? '',
          editorSystemPrompt: r.data.editorSystemPrompt ?? '',
          continuityCheckerSystemPrompt: r.data.continuityCheckerSystemPrompt ?? '',
        })
      })
      getDefaultPrompts(bookId).then(r => setDefaultPrompts(r.data)).catch(() => {})
    }
    // Load available Ollama models
    getOllamaModels()
      .then(r => setOllamaModels(r.data))
      .catch(() => {}) // Ollama may not be running
  }, [bookId])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    const modelName = useCustomModel ? customModel : config.modelName
    await updateLlmConfig({ ...config, modelName, bookId })
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const handleBookSave = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!book) return
    await updateBook(book.id, {
      ...book,
      language: bookForm.language,
      plannerSystemPrompt: bookForm.plannerSystemPrompt || undefined,
      writerSystemPrompt: bookForm.writerSystemPrompt || undefined,
      editorSystemPrompt: bookForm.editorSystemPrompt || undefined,
      continuityCheckerSystemPrompt: bookForm.continuityCheckerSystemPrompt || undefined,
    })
    setBookSaved(true)
    setTimeout(() => setBookSaved(false), 2000)
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
              getOllamaModels().then(r => setOllamaModels(r.data)).catch(() => {})
            }
          } catch { /* skip malformed */ }
        }
      }
      setPullStatus('Done!')
    } catch (err) {
      if ((err as Error).name !== 'AbortError') setPullStatus('Error: ' + (err as Error).message)
    }
  }

  const allModelOptions = [
    ...ollamaModels.map(m => m.name),
    ...COMMON_MODELS.filter(m => !ollamaModels.some(om => om.name === m)),
  ]

  return (
    <div className="container settings-page">
      <Link to={bookId ? `/books/${bookId}` : '/'} className="back-link">← Back</Link>
      <h1>Settings {bookId ? `— Book #${bookId}` : '(Global)'}</h1>

      {/* LLM Config */}
      <section className="settings-section">
        <h2>LLM Configuration</h2>
        <form className="card settings-form" onSubmit={handleSave}>
          <label>
            Provider
            <select value={config.provider} onChange={e => setConfig(c => ({ ...c, provider: e.target.value }))}>
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
                {allModelOptions.map(m => (
                  <option key={m} value={m}>{m}{ollamaModels.some(om => om.name === m) ? ' ✓' : ''}</option>
                ))}
                <option value="__custom__">— Custom model name —</option>
              </select>
              {useCustomModel && (
                <input
                  placeholder="e.g. llama3.2:3b"
                  value={customModel}
                  onChange={e => setCustomModel(e.target.value)}
                />
              )}
            </div>
          </label>
          <label>
            Endpoint
            <input value={config.endpoint} onChange={e => setConfig(c => ({ ...c, endpoint: e.target.value }))} required />
          </label>
          <label>
            Embedding Model
            <input value={config.embeddingModelName ?? ''} onChange={e => setConfig(c => ({ ...c, embeddingModelName: e.target.value }))} />
          </label>
          <label>
            API Key (optional)
            <input type="password" value={config.apiKey ?? ''} onChange={e => setConfig(c => ({ ...c, apiKey: e.target.value }))} />
          </label>
          <button type="submit">Save LLM Config</button>
          {saved && <span className="saved-msg">✓ Saved</span>}
        </form>
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
                <code>{'{LANGUAGE}'}</code> <code>{'{CHAPTER_COUNT}'}</code>
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
                    plannerSystemPrompt: f.plannerSystemPrompt || defaultPrompts.plannerSystemPrompt,
                    writerSystemPrompt: f.writerSystemPrompt || defaultPrompts.writerSystemPrompt,
                    editorSystemPrompt: f.editorSystemPrompt || defaultPrompts.editorSystemPrompt,
                    continuityCheckerSystemPrompt: f.continuityCheckerSystemPrompt || defaultPrompts.continuityCheckerSystemPrompt,
                  }))}
                >
                  Load Defaults
                </button>
              )}
              {(['planner', 'writer', 'editor', 'continuityChecker'] as const).map(role => {
                const key = `${role}SystemPrompt` as keyof typeof bookForm
                const defaultText = defaultPrompts?.[key as keyof DefaultPrompts]
                return (
                  <label key={role}>
                    {role.charAt(0).toUpperCase() + role.slice(1)} prompt
                    <textarea
                      rows={6}
                      value={bookForm[key]}
                      onChange={e => setBookForm(f => ({ ...f, [key]: e.target.value }))}
                      placeholder={defaultText ?? `Custom system prompt for ${role} agent…`}
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
