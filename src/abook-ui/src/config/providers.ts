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

export const MODEL_LIST_PROVIDERS = new Set(['Ollama'])
export const API_KEY_REQUIRED_PROVIDERS = new Set(['OpenAI', 'GoogleAIStudio'])
// OpenAICompatible: endpoint always editable, API key optional
export const PROXY_REQUIRED_PROVIDERS = new Set()

export const INITIAL_LLM_CONFIG = {
  provider: 'Ollama',
  modelName: 'llama3',
  endpoint: 'http://host.docker.internal:11434',
  embeddingModelName: 'nomic-embed-text',
} as const
