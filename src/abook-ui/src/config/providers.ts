export const PROVIDERS = ['Ollama', 'OpenAI', 'OpenAICompatible', 'GoogleAIStudio'] as const
export type LlmProviderName = typeof PROVIDERS[number]

export const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  OpenAICompatible: '',
  GoogleAIStudio: 'https://generativelanguage.googleapis.com/v1beta/openai',
}

export const MODEL_LIST_PROVIDERS = new Set(['Ollama', 'OpenAI', 'OpenAICompatible', 'GoogleAIStudio'])
export const API_KEY_REQUIRED_PROVIDERS = new Set(['OpenAI', 'OpenAICompatible', 'GoogleAIStudio'])
export const PROXY_REQUIRED_PROVIDERS = new Set()

export const INITIAL_LLM_CONFIG = {
  provider: 'Ollama',
  modelName: 'llama3',
  endpoint: 'http://host.docker.internal:11434',
  embeddingModelName: 'nomic-embed-text',
} as const
