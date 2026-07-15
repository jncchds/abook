import { useEffect, useState } from 'react'
import type { LlmPreset, ProviderModel } from '../api'
import { getPresets, createPreset, updatePreset, deletePreset, getModels } from '../api'
import { PROVIDERS, DEFAULT_ENDPOINTS, MODEL_LIST_PROVIDERS, API_KEY_REQUIRED_PROVIDERS } from '../config/providers'

const emptyForm = {
  name: '', provider: 'Ollama', modelName: '', endpoint: '', apiKey: '', embeddingModelName: '',
} as const

export default function Presets() {
  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [form, setForm] = useState<Omit<LlmPreset, 'id' | 'userId' | 'createdAt' | 'updatedAt'>>({ ...emptyForm })
  const [editingId, setEditingId] = useState<number | null>(null)
  const [showForm, setShowForm] = useState(false)
  const [saved, setSaved] = useState(false)

  // Model fetching — same pattern as BookSettings LLM config form
  const [models, setModels] = useState<ProviderModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)

  const fetchModels = () => {
    const prov = form.provider
    if (!MODEL_LIST_PROVIDERS.has(prov)) { setModels([]); return }
    setModelsLoading(true)
    getModels(form.endpoint || DEFAULT_ENDPOINTS[prov], prov, form.apiKey ?? undefined)
      .then(r => setModels(r.data)).catch(() => setModels([]))
      .finally(() => setModelsLoading(false))
  }

  useEffect(() => { fetchModels() }, [form.provider])

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
    setShowForm(false)
    setForm({ ...emptyForm })
  }

  const fetchedModelNames = models.map(m => m.name)

  return (
    <>
      <div className="page-header">
        <h2>Credential Presets</h2>
        <button className="btn-sm" onClick={() => {
          if (editingId !== null) { handleCancel(); setShowForm(false); return }
          setShowForm(v => !v)
        }}>{showForm ? '✕ Close' : '+ New Preset'}</button>
      </div>
      <p className="hint">
        Save named LLM configurations for quick reuse. Presets are per-user and can be applied from any
        book's LLM settings page.
      </p>

      {showForm && (
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
            <select value={form.provider} onChange={e => {
              const prov = e.target.value
              const defaultEndpoint = DEFAULT_ENDPOINTS[prov] ?? ''
              setForm(f => ({ ...f, provider: prov, endpoint: f.endpoint && (DEFAULT_ENDPOINTS[f.provider] === f.endpoint) ? defaultEndpoint : f.endpoint }))
              setModels([])
            }}>
              {PROVIDERS.map(p => <option key={p}>{p}</option>)}
            </select>
          </label>
          <label>
            Model Name
            <div className="model-selector">
              <input
                required
                placeholder="e.g. llama3"
                value={form.modelName}
                onChange={e => setForm(f => ({ ...f, modelName: e.target.value }))}
                list="preset-models-list"
              />
              <datalist id="preset-models-list">
                {fetchedModelNames.map(m => <option key={m} value={m} />)}
              </datalist>
              {modelsLoading && <span className="hint">Loading…</span>}
            </div>
          </label>
          <label>
            Endpoint
            <div className="endpoint-row">
              <input
                placeholder={DEFAULT_ENDPOINTS[form.provider] ?? ''}
                value={form.endpoint}
                onChange={e => setForm(f => ({ ...f, endpoint: e.target.value }))}
              />
              {MODEL_LIST_PROVIDERS.has(form.provider) && (
                <button
                  type="button"
                  className="btn-secondary btn-sm"
                  onClick={fetchModels}
                  disabled={modelsLoading}
                >{modelsLoading ? '…' : '↺ Refresh'}</button>
              )}
            </div>
            {API_KEY_REQUIRED_PROVIDERS.has(form.provider) && (
              <span className="hint">Enter your API key below, then click ↺ Refresh to load the model list.</span>
            )}
          </label>
          <label>
            Embedding Model (optional)
            <div className="model-selector">
              <input
                placeholder={form.provider === 'GoogleAIStudio' ? 'e.g. text-embedding-004' : 'e.g. nomic-embed-text'}
                value={form.embeddingModelName ?? ''}
                onChange={e => setForm(f => ({ ...f, embeddingModelName: e.target.value }))}
                list="preset-embed-list"
              />
              <datalist id="preset-embed-list">
                {fetchedModelNames.map(m => <option key={m} value={m} />)}
              </datalist>
            </div>
          </label>
          <label>
            API Key (optional)
            <input type="password" value={form.apiKey ?? ''} onChange={e => setForm(f => ({ ...f, apiKey: e.target.value }))} />
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
      )}

      {/* Presets list below form */}
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
        <p className="empty" style={{ marginBottom: '1.5rem' }}>No presets yet.</p>
      )}
    </>
  )
}
