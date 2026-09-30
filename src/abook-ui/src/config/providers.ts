export const PROVIDERS = ['Ollama', 'OpenAI', 'OpenAICompatible', 'GoogleAIStudio'] as const
export type LlmProviderName = typeof PROVIDERS[number]

export const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  OpenAICompatible: 'http://localhost:1234/v1',
  GoogleAIStudio: 'https://generativelanguage.googleapis.com/v1beta/openai',
}

export const PROVIDER_LABELS: Record<LlmProviderName, string> = {
  Ollama: 'Ollama',
  OpenAI: 'OpenAI',
  OpenAICompatible: 'OpenAI Compatible',
  GoogleAIStudio: 'Google AI Studio',
}

export const MODEL_LIST_PROVIDERS = new Set(['Ollama', 'OpenAI', 'OpenAICompatible', 'GoogleAIStudio'])
export const API_KEY_REQUIRED_PROVIDERS = new Set(['GoogleAIStudio'])
// OpenAICompatible: endpoint always editable, API key optional
export const PROXY_REQUIRED_PROVIDERS = new Set()

export const INITIAL_LLM_CONFIG = {
  provider: 'Ollama',
  modelName: 'llama3',
  endpoint: 'http://host.docker.internal:11434',
  embeddingModelName: 'nomic-embed-text',
} as const

// ── Image generation ─────────────────────────────────────────────────────────

export const IMAGE_PROVIDERS = ['ZImage'] as const
export const IMAGE_PROVIDER_LABELS: Record<string, string> = { ZImage: 'Z-Image' }

/** Z-Image Turbo is distilled for 8 steps at cfg 1.0; these match the server-side defaults. */
export const IMAGE_DEFAULTS = {
  steps: 8,
  cfg: 1.0,
  portraitWidth: 768,
  portraitHeight: 1152,
  landscapeWidth: 1152,
  landscapeHeight: 768,
  timeoutMs: 300000,
} as const

export type ImageSettings = Pick<import('../api').LlmSettings,
  'imageProvider' | 'imageEndpoint' | 'imageSteps' | 'imageCfg' | 'imagePortraitWidth' | 'imagePortraitHeight'
  | 'imageLandscapeWidth' | 'imageLandscapeHeight' | 'imageTimeoutMs' | 'imageVisualStyle' | 'unloadLlmBeforeRender'>

/** The image-generation fields of a configuration or preset, so applying/saving a preset carries them along. */
export function pickImageSettings(s: ImageSettings): ImageSettings {
  return {
    imageProvider: s.imageProvider ?? undefined,
    imageEndpoint: s.imageEndpoint ?? undefined,
    imageSteps: s.imageSteps ?? undefined,
    imageCfg: s.imageCfg ?? undefined,
    imagePortraitWidth: s.imagePortraitWidth ?? undefined,
    imagePortraitHeight: s.imagePortraitHeight ?? undefined,
    imageLandscapeWidth: s.imageLandscapeWidth ?? undefined,
    imageLandscapeHeight: s.imageLandscapeHeight ?? undefined,
    imageTimeoutMs: s.imageTimeoutMs ?? undefined,
    imageVisualStyle: s.imageVisualStyle ?? undefined,
    unloadLlmBeforeRender: s.unloadLlmBeforeRender ?? false,
  }
}
