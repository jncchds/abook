export const PROVIDERS = ['Ollama', 'OpenAI', 'AzureOpenAI', 'Anthropic', 'GoogleAIStudio'] as const
export type LlmProviderName = typeof PROVIDERS[number]

export const DEFAULT_ENDPOINTS: Record<string, string> = {
  Ollama: 'http://host.docker.internal:11434',
  Anthropic: 'http://localhost:4000',
  GoogleAIStudio: 'https://generativelanguage.googleapis.com/v1beta/openai',
}

export const MODEL_LIST_PROVIDERS = new Set(['Ollama', 'OpenAI', 'GoogleAIStudio'])
export const API_KEY_REQUIRED_PROVIDERS = new Set(['OpenAI', 'GoogleAIStudio'])
export const PROXY_REQUIRED_PROVIDERS = new Set(['Anthropic'])

export const INITIAL_LLM_CONFIG = {
  provider: 'Ollama',
  modelName: 'llama3',
  endpoint: 'http://host.docker.internal:11434',
  embeddingModelName: 'nomic-embed-text',
} as const
