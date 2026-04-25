import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import type { LlmPreset } from '../api'
import { getPresets, createPreset, updatePreset, deletePreset } from '../api'

const PROVIDERS = ['Ollama', 'LMStudio', 'OpenAI', 'AzureOpenAI', 'Anthropic'] as const

const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  LMStudio: 'http://host.docker.internal:1234',
  Anthropic: 'http://localhost:4000',
}

const emptyForm = {
  name: '', provider: 'Ollama', modelName: '', endpoint: '', apiKey: '', embeddingModelName: '',
} as const

export default function Presets() {
  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [form, setForm] = useState<Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>>({ ...emptyForm })
  const [editingId, setEditingId] = useState<number | null>(null)
  const [saved, setSaved] = useState(false)

  useEffect(() => {
    getPresets().then(r => setPresets(r.data)).catch(() => {})
  }, [])

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (editingId !== null) {
      const updated = await updatePreset(editingId, form)
      setPresets(ps => ps.map(p => p.id === editingId ? updated.data : p))
    } else {
      const created = await createPreset(form)
      setPresets(ps => [...ps, created.data])
    }
    setForm({ ...emptyForm })
    setEditingId(null)
    setSaved(true)
    setTimeout(() => setSaved(false), 2000)
  }

  const handleEdit = (preset: LlmPreset) => {
    setEditingId(preset.id)
    setForm({
      name: preset.name,
      provider: preset.provider,
      modelName: preset.modelName,
      endpoint: preset.endpoint,
      apiKey: preset.apiKey ?? '',
      embeddingModelName: preset.embeddingModelName ?? '',
    })
  }

  const handleDelete = async (id: number) => {
    if (!confirm('Delete this preset?')) return
    await deletePreset(id)
    setPresets(ps => ps.filter(p => p.id !== id))
    if (editingId === id) { setEditingId(null); setForm({ ...emptyForm }) }
  }

  const handleCancel = () => {
    setEditingId(null)
    setForm({ ...emptyForm })
  }

  return (
    <div className="container settings-page">
      <Link to="/" className="back-link">← Back to Dashboard</Link>
      <h1>Credential Presets</h1>
      <p className="hint">
        Save named LLM configurations for quick reuse. Presets are per-user and can be applied from any
        book's LLM settings page.
      </p>

      {presets.length > 0 ? (
        <div className="card presets-list">
          {presets.map(p => (
            <div key={p.id} className="preset-row">
              <div className="preset-info">
                <strong>{p.name}</strong>
                <span className="preset-meta">{p.provider} · {p.modelName}{p.endpoint ? ` · ${p.endpoint}` : ''}</span>
                {p.embeddingModelName && <span className="preset-meta">embed: {p.embeddingModelName}</span>}
                {!p.userId && <span className="preset-badge">global</span>}
              </div>
              <div className="preset-actions">
                <button type="button" className="btn-secondary btn-sm" onClick={() => handleEdit(p)}>Edit</button>
                <button
                  type="button"
                  className="btn-danger btn-sm"
                  onClick={() => handleDelete(p.id)}
                  disabled={!p.userId}
                  title={!p.userId ? 'Global presets cannot be deleted' : undefined}
                >
                  Delete
                </button>
              </div>
            </div>
          ))}
        </div>
      ) : (
        <p className="empty" style={{ marginBottom: '1.5rem' }}>No presets yet. Create one below.</p>
      )}

      <section className="settings-section">
        <form className="card settings-form" onSubmit={handleSubmit}>
          <h3 style={{ margin: '0 0 0.75rem' }}>{editingId !== null ? 'Edit Preset' : 'New Preset'}</h3>
          <label>
            Preset Name
            <input
              required
              placeholder="e.g. Local Ollama"
              value={form.name}
              onChange={e => setForm(f => ({ ...f, name: e.target.value }))}
            />
          </label>
          <label>
            Provider
            <select value={form.provider} onChange={e => setForm(f => ({ ...f, provider: e.target.value }))}>
              {PROVIDERS.map(p => <option key={p}>{p}</option>)}
            </select>
          </label>
          <label>
            Model Name
            <input
              required
              placeholder="e.g. llama3"
              value={form.modelName}
              onChange={e => setForm(f => ({ ...f, modelName: e.target.value }))}
            />
          </label>
          <label>
            Endpoint
            <input
              placeholder={DEFAULT_ENDPOINTS[form.provider] ?? ''}
              value={form.endpoint}
              onChange={e => setForm(f => ({ ...f, endpoint: e.target.value }))}
            />
          </label>
          <label>
            Embedding Model (optional)
            <input
              placeholder="e.g. nomic-embed-text"
              value={form.embeddingModelName ?? ''}
              onChange={e => setForm(f => ({ ...f, embeddingModelName: e.target.value }))}
            />
          </label>
          <label>
            API Key (optional)
            <input
              type="password"
              value={form.apiKey ?? ''}
              onChange={e => setForm(f => ({ ...f, apiKey: e.target.value }))}
            />
          </label>
          <div style={{ display: 'flex', gap: '0.5rem', alignItems: 'center', marginTop: '0.25rem' }}>
            <button type="submit">{editingId !== null ? 'Update Preset' : 'Save Preset'}</button>
            {editingId !== null && (
              <button type="button" className="btn-secondary" onClick={handleCancel}>Cancel</button>
            )}
            {saved && <span className="saved-msg">✓ Saved</span>}
          </div>
        </form>
      </section>
    </div>
  )
}
