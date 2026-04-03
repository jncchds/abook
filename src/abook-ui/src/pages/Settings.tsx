import { useEffect, useState } from 'react'
import { useParams, Link } from 'react-router-dom'
import type { LlmConfig } from '../api'
import { getLlmConfig, updateLlmConfig } from '../api'

const providers = ['Ollama', 'OpenAI', 'AzureOpenAI', 'Anthropic']

export default function Settings() {
  const { id } = useParams<{ id: string }>()
  const bookId = id ? Number(id) : undefined
  const [config, setConfig] = useState<LlmConfig>({
    provider: 'Ollama', modelName: 'llama3',
    endpoint: 'http://host.docker.internal:11434', embeddingModelName: 'nomic-embed-text'
  })
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    getLlmConfig(bookId).then(r => { if (r.data) setConfig(r.data) })
  }, [bookId])

  const handleSave = async (e: React.FormEvent) => {
    e.preventDefault()
    await updateLlmConfig({ ...config, bookId })
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  return (
    <div className="container settings-page">
      <Link to={bookId ? `/books/${bookId}` : '/'} className="back-link">← Back</Link>
      <h1>LLM Settings {bookId ? `(Book #${bookId})` : '(Global)'}</h1>
      <form className="card settings-form" onSubmit={handleSave}>
        <label>
          Provider
          <select value={config.provider} onChange={e => setConfig(c => ({ ...c, provider: e.target.value }))}>
            {providers.map(p => <option key={p}>{p}</option>)}
          </select>
        </label>
        <label>
          Model Name
          <input value={config.modelName} onChange={e => setConfig(c => ({ ...c, modelName: e.target.value }))} required />
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
        <button type="submit">Save</button>
        {saved && <span className="saved-msg">✓ Saved</span>}
      </form>
    </div>
  )
}
