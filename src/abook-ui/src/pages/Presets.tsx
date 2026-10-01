import { useEffect, useState } from 'react'
import type { LlmPreset, PresetInput, ProviderModel } from '../api'
import { getPresets, createPreset, updatePreset, deletePreset, getModels, getDefaultPreset, setDefaultPreset, modelsErrorMessage } from '../api'
import { PROVIDERS, PROVIDER_LABELS, DEFAULT_ENDPOINTS, MODEL_LIST_PROVIDERS, API_KEY_REQUIRED_PROVIDERS, pickImageSettings } from '../config/providers'
import ImageGenSettingsFields from '../components/ImageGenSettingsFields'
import { useAuth } from '../hooks/useAuth'

const emptyForm = {
  name: '', provider: 'Ollama', modelName: '', endpoint: '', apiKey: '', embeddingModelName: '',
  temperature: undefined as number | undefined,
  timeoutMs: undefined as number | undefined,
  reasoningEffort: undefined as string | undefined,
  maxTokens: undefined as number | undefined,
  ...pickImageSettings({}),
  shared: false,
}

export default function Presets() {
  const { user } = useAuth()
  const isAdmin = user?.isAdmin ?? false
  const [presets, setPresets] = useState<LlmPreset[]>([])
  const [defaultPresetId, setDefaultPresetId] = useState<number | null>(null)
  const [form, setForm] = useState<PresetInput>({ ...emptyForm })
  const [editingId, setEditingId] = useState<number | null>(null)
  const editingShared = presets.find(p => p.id === editingId)?.userId == null && editingId !== null
  /** Shared presets (no owner) are usable by everyone but only admins may change them. */
  const canModify = (p: LlmPreset) => p.userId != null || isAdmin
  const [showForm, setShowForm] = useState(false)
  const [saved, setSaved] = useState(false)
  const [showAdvanced, setShowAdvanced] = useState(false)

  // Model fetching — same pattern as BookSettings LLM config form
  const [models, setModels] = useState<ProviderModel[]>([])
  const [modelsLoading, setModelsLoading] = useState(false)
  const [modelsError, setModelsError] = useState<string | null>(null)

  const fetchModels = () => {
    const prov = form.provider
    setModelsError(null)
    if (!MODEL_LIST_PROVIDERS.has(prov)) { setModels([]); return }
    setModelsLoading(true)
    getModels(form.endpoint || DEFAULT_ENDPOINTS[prov], prov, form.apiKey ?? undefined)
      .then(r => setModels(r.data))
      .catch(err => { setModels([]); setModelsError(modelsErrorMessage(err)) })
      .finally(() => setModelsLoading(false))
  }

  useEffect(() => {
    getPresets().then(r => setPresets(r.data)).catch(err => console.error('Failed to load presets', err))
    getDefaultPreset().then(r => setDefaultPresetId(r.data.presetId)).catch(err => console.error('Failed to load default preset', err))
  }, [])

  const handleMakeDefault = async (id: number) => {
    await setDefaultPreset(id)
    setDefaultPresetId(id)
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
      temperature: preset.temperature ?? undefined,
      timeoutMs: preset.timeoutMs ?? undefined,
      reasoningEffort: preset.reasoningEffort ?? undefined,
      maxTokens: preset.maxTokens ?? undefined,
      ...pickImageSettings(preset),
      shared: preset.userId == null,
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
        Named LLM configurations. Your <strong>default</strong> preset is copied into every new book; any preset can
        also be applied from a book's Settings page. Books keep their own copy — editing a preset never changes
        existing books. <strong>Shared</strong> presets are available to everyone and can only be changed by admins.
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
              {PROVIDERS.map(p => <option key={p} value={p}>{PROVIDER_LABELS[p]}</option>)}
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
            {modelsError && <span className="error-msg" role="alert">{modelsError}</span>}
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
          <details open={showAdvanced} onToggle={e => setShowAdvanced((e.target as HTMLDetailsElement).open)} style={{ marginTop: '0.75rem' }}>
            <summary style={{ cursor: 'pointer', fontWeight: 600, userSelect: 'none' }}>⚙ Advanced LLM Parameters</summary>
            <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem', marginTop: '0.75rem' }}>
              <label>
                Temperature (0–1)
                <input
                  type="text"
                  inputMode="decimal"
                  placeholder="default (0.8)"
                  value={form.temperature ?? ''}
                  onChange={e => setForm(f => ({ ...f, temperature: e.target.value === '' ? undefined : parseFloat(e.target.value) }))}
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
                  value={form.timeoutMs ?? ''}
                  onChange={e => setForm(f => ({ ...f, timeoutMs: e.target.value === '' ? undefined : parseInt(e.target.value, 10) }))}
                />
                <span className="hint">Request timeout. Leave blank for no override.</span>
              </label>
              {form.provider !== 'OpenAICompatible' && (
              <label>
                Reasoning effort
                <select value={form.reasoningEffort ?? ''} onChange={e => setForm(f => ({ ...f, reasoningEffort: e.target.value || undefined }))}>
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
                  value={form.maxTokens ?? ''}
                  onChange={e => setForm(f => ({ ...f, maxTokens: e.target.value === '' ? undefined : parseInt(e.target.value, 10) }))}
                />
                <span className="hint">Maximum output tokens. Leave blank for provider default.</span>
              </label>
            </div>
          </details>
          <ImageGenSettingsFields value={form} llmProvider={form.provider} onChange={patch => setForm(f => ({ ...f, ...patch }))} />
          {isAdmin && (
            <label className="toggle-row">
              <span>Shared with everyone</span>
              <input
                type="checkbox"
                checked={form.shared ?? false}
                disabled={editingShared}
                onChange={e => setForm(f => ({ ...f, shared: e.target.checked }))}
              />
            </label>
          )}
          {isAdmin && form.shared && (
            <span className="hint">
              Every user can use a shared preset (including its API keys); only admins can edit or delete it. Sharing cannot be undone.
            </span>
          )}
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
                {p.userId == null && <span className="preset-badge">shared</span>}
                {p.id === defaultPresetId && <span className="preset-badge">★ default</span>}
              </div>
              <div className="preset-actions">
                {p.id !== defaultPresetId && (
                  <button type="button" className="btn-secondary btn-sm" onClick={() => handleMakeDefault(p.id)}
                    title="Copy this preset into every new book">Make default</button>
                )}
                {canModify(p) && (
                  <>
                    <button type="button" className="btn-secondary btn-sm" onClick={() => { handleEdit(p); setShowForm(true) }}>Edit</button>
                    <button type="button" className="btn-danger btn-sm" onClick={() => handleDelete(p.id)}>Delete</button>
                  </>
                )}
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
