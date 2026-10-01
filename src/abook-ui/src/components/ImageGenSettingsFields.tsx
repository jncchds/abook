import { useState } from 'react'
import { getImageHealth } from '../api'
import { IMAGE_DEFAULTS, IMAGE_PROVIDERS, IMAGE_PROVIDER_LABELS, type ImageSettings } from '../config/providers'
import ToggleField from './ToggleField'

interface Props {
  value: ImageSettings
  onChange: (patch: Partial<ImageSettings>) => void
  /** The text model's provider — unloading before a render only works for Ollama. */
  llmProvider?: string
}

/** What Unsloth Studio's image API uses when steps and CFG are left out (its Z-Image Turbo recipe). */
const UNSLOTH_DEFAULTS = { steps: 9, cfg: 0 }

const ENDPOINT_PLACEHOLDERS: Record<string, string> = {
  ZImage: 'e.g. http://gpu-box:8000',
  UnslothStudio: 'e.g. http://gpu-box:8888',
}

const numberOrUndefined = (v: string, parse: (s: string) => number) => (v === '' ? undefined : parse(v))

/**
 * Image-generation block shared by the global settings, book settings and preset forms. These fields are
 * part of the LLM configuration and presets, so applying a preset switches the text and image models together.
 */
export default function ImageGenSettingsFields({ value, onChange, llmProvider }: Props) {
  const [health, setHealth] = useState<{ ok: boolean; text: string } | null>(null)
  const [testing, setTesting] = useState(false)

  const testConnection = async () => {
    if (!value.imageEndpoint) return
    setTesting(true)
    setHealth(null)
    try {
      const r = await getImageHealth(value.imageEndpoint, value.imageProvider, value.imageApiKey, value.imageModel)
      setHealth({
        ok: r.data.ready,
        text: r.data.ready
          ? `✓ Ready${r.data.detail ? ` — ${r.data.detail}` : ''}`
          : `✗ ${r.data.status}${r.data.detail ? ` — ${r.data.detail}` : ''}`,
      })
    } catch {
      setHealth({ ok: false, text: '✗ Could not reach the server' })
    } finally {
      setTesting(false)
    }
  }

  const num = (key: keyof ImageSettings, placeholder: number, parse: (s: string) => number = s => parseInt(s, 10)) => (
    <input
      type="text"
      inputMode="decimal"
      placeholder={String(placeholder)}
      value={(value[key] as number | null | undefined) ?? ''}
      onChange={e => onChange({ [key]: numberOrUndefined(e.target.value, parse) })}
    />
  )

  const provider = value.imageProvider ?? 'ZImage'
  const isUnsloth = provider === 'UnslothStudio'

  return (
    <details style={{ marginTop: '0.75rem' }} open={!!value.imageEndpoint}>
      <summary style={{ cursor: 'pointer', fontWeight: 600, userSelect: 'none' }}>🎨 Image Generation</summary>
      <p className="hint" style={{ margin: '0.5rem 0' }}>
        Renders book illustrations. The text model plans every prompt; rendering runs separately so the text model
        can be unloaded first. Leave the endpoint blank to disable.
      </p>
      <label>
        Image provider
        <select value={provider} onChange={e => { setHealth(null); onChange({ imageProvider: e.target.value }) }}>
          {IMAGE_PROVIDERS.map(p => <option key={p} value={p}>{IMAGE_PROVIDER_LABELS[p]}</option>)}
        </select>
      </label>
      <label>
        Image endpoint
        <div className="endpoint-row">
          <input
            value={value.imageEndpoint ?? ''}
            placeholder={ENDPOINT_PLACEHOLDERS[provider]}
            onChange={e => { setHealth(null); onChange({ imageEndpoint: e.target.value || undefined }) }}
          />
          <button type="button" className="btn-secondary btn-sm" onClick={testConnection} disabled={!value.imageEndpoint || testing}>
            {testing ? '…' : 'Test connection'}
          </button>
        </div>
        {health && <span className="hint" style={{ color: health.ok ? 'var(--success, green)' : 'var(--danger, red)' }}>{health.text}</span>}
        {isUnsloth && (
          <span className="hint">
            The Studio address, with or without <code>/v1</code> — the same server can also serve the text model as an
            OpenAI Compatible provider.
          </span>
        )}
      </label>
      {isUnsloth && (
        <>
          <label>
            Image API key
            <input
              type="password"
              value={value.imageApiKey ?? ''}
              placeholder="sk-unsloth-…"
              onChange={e => { setHealth(null); onChange({ imageApiKey: e.target.value || undefined }) }}
            />
            <span className="hint">Create one in Unsloth Studio under Settings → API.</span>
          </label>
          <label>
            Image model
            <input
              value={value.imageModel ?? ''}
              placeholder="e.g. unsloth/Z-Image-Turbo-GGUF:z-image-turbo-Q8_0.gguf"
              onChange={e => { setHealth(null); onChange({ imageModel: e.target.value || undefined }) }}
            />
            <span className="hint">
              Loaded before rendering when Studio has no image model loaded — Studio unloads its text model to make room.
              A Hub repo id, optionally followed by <code>:</code> and a checkpoint file in it. Leave blank to use
              whatever is loaded on Studio’s Images page.
            </span>
          </label>
        </>
      )}
      <label>
        Visual style
        <textarea
          rows={2}
          value={value.imageVisualStyle ?? ''}
          placeholder="e.g. painterly ink-and-watercolour illustration, muted earthy palette, soft light"
          onChange={e => onChange({ imageVisualStyle: e.target.value || undefined })}
        />
        <span className="hint">The illustrator writes this style into every prompt. Leave blank to let it choose one that suits the genre.</span>
      </label>
      <label>
        Illustrations per chapter{num('imagesPerChapter', IMAGE_DEFAULTS.imagesPerChapter)}
        <span className="hint">The number the illustrator aims for in each chapter. It plans more when a chapter genuinely needs them, fewer when there is little worth illustrating.</span>
      </label>
      <ToggleField
        checked={!!value.unloadLlmBeforeRender}
        onChange={v => onChange({ unloadLlmBeforeRender: v })}
        label="Unload the LLM before rendering"
        hint={isUnsloth
          ? 'Only works with Ollama. Unsloth Studio frees its own text model when the image model loads.'
          : llmProvider && llmProvider !== 'Ollama'
            ? 'Only works with Ollama — unload other providers yourself before rendering.'
            : 'Asks Ollama to free the text model’s VRAM before the image model renders.'}
      />
      <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: '0.75rem', marginTop: '0.5rem' }}>
        <label>
          Steps{num('imageSteps', isUnsloth ? UNSLOTH_DEFAULTS.steps : IMAGE_DEFAULTS.steps)}
          {isUnsloth && <span className="hint">Blank uses Studio’s Z-Image Turbo defaults; set both for other models.</span>}
        </label>
        <label>
          CFG scale{num('imageCfg', isUnsloth ? UNSLOTH_DEFAULTS.cfg : IMAGE_DEFAULTS.cfg, parseFloat)}
          <span className="hint">Negative prompts only take effect above 1.0.</span>
        </label>
        <label>Portrait width{num('imagePortraitWidth', IMAGE_DEFAULTS.portraitWidth)}</label>
        <label>Portrait height{num('imagePortraitHeight', IMAGE_DEFAULTS.portraitHeight)}</label>
        <label>Landscape width{num('imageLandscapeWidth', IMAGE_DEFAULTS.landscapeWidth)}</label>
        <label>Landscape height{num('imageLandscapeHeight', IMAGE_DEFAULTS.landscapeHeight)}</label>
        <label>
          Timeout (ms){num('imageTimeoutMs', IMAGE_DEFAULTS.timeoutMs)}
          <span className="hint">Per image, including the first render’s warm-up.</span>
        </label>
      </div>
    </details>
  )
}
