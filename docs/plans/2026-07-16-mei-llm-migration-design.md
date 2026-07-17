# MEAI LLM Migration Design

**Date:** 2026-07-16  
**Status:** Draft  
**Goal:** Replace Semantic Kernel with Microsoft.Extensions.AI (`IChatClient`) for full control over HTTP clients, timeouts, and reasoning content extraction.

---

## Architecture Overview

### Current State (SK)
```
[Agents] → SK `Kernel` → `IChatCompletionService` (provider connector) → SDK HttpClient (100s default timeout)
```

### Target State (MEAI)
```
[Agents] → `IChatClient.CompleteStreamingAsync()` → Provider SDK HttpClient (config.TimeoutMs applied)
```

**Key Changes:**
- Remove all SK dependencies from agent code
- Use MEAI `IChatClient` for streaming with automatic reasoning content extraction
- Custom HttpClients per provider with configurable timeouts
- Per-provider JSON format handling via `ChatOptions.Metadata`

---

## Components to Create/Modify

### 1. New Interface: `ILlmStreamingService` (in `ABook.Core`)

```csharp
public interface ILlmStreamingService
{
    Task<string> StreamAsync(
        int bookId,
        ChatHistory history,
        LlmConfiguration config,
        AgentRole role,
        CancellationToken ct);
}
```

**Responsibilities:**
- Execute streaming call with provider-specific options
- Extract reasoning content via `update.ReasoningContent` (automatic per-provider)
- Handle JSON format differences per provider
- Apply custom HttpClient timeout from config

### 2. Provider Implementations (in `ABook.Infrastructure`)

#### `OllamaLlmStreamingService`
```csharp
var options = new ChatOptions
{
    Temperature = config.Temperature,
    MaxOutputTokens = config.MaxTokens,
    Metadata = new Dictionary<string, object?> { ["format"] = "json" }
};

using var client = new OllamaClient(config.Endpoint, httpClient);
await foreach (var update in client.CompleteStreamingAsync(messages, options))
{
    if (!string.IsNullOrEmpty(update.ReasoningContent))
        thinkingSb.Append(update.ReasoningContent);
    
    if (update.Content is { Length: > 0 } token)
        sb.Append(token);
}
```

#### `OpenAiLlmStreamingService`
```csharp
var options = new ChatOptions
{
    Temperature = config.Temperature,
    MaxOutputTokens = config.MaxTokens,
    ResponseFormat = chatResponseFormat // for JSON schema
};

using var client = new OpenAIClient(config.ApiKey, httpClient);
await foreach (var update in client.CompleteStreamingAsync(messages, options))
{
    if (!string.IsNullOrEmpty(update.ReasoningContent))
        thinkingSb.Append(update.ReasoningContent);
    
    if (update.Content is { Length: > 0 } token)
        sb.Append(token);
}
```

#### `GoogleAiStudioLlmStreamingService`
```csharp
var options = new ChatOptions
{
    Temperature = config.Temperature,
    MaxOutputTokens = config.MaxTokens,
    Metadata = new Dictionary<string, object?> { ["response_mime_type"] = "application/json" }
};

using var client = new GoogleAIClient(config.ApiKey, httpClient);
await foreach (var update in client.CompleteStreamingAsync(messages, options))
{
    if (!string.IsNullOrEmpty(update.ReasoningContent))
        thinkingSb.Append(update.ReasoningContent);
    
    if (update.Content is { Length: > 0 } token)
        sb.Append(token);
}
```

### 3. Custom Chat History (`ABook.Core`)

Replace SK `ChatHistory` with MEAI `ChatMessage`:
```csharp
public record ChatMessage(string Role, string Content);
// or use MEAI's built-in message abstraction if preferred
```

**Migration path:** Convert existing `ChatHistory` to `IEnumerable<ChatMessage>` in agent code.

### 4. Strategy Pattern Update (in `ABook.Infrastructure.Llm`)

Replace `ILlmProviderStrategy` with provider-specific streaming service factories:
```csharp
public interface ILlmProviderFactory
{
    ILlmStreamingService CreateStreamingService(LlmConfiguration config);
}
```

---

## Data Flow Changes

### Before (SK)
1. Agent calls `kernel.GetRequiredService<IChatCompletionService>()`
2. Calls `GetStreamingChatMessageContentsAsync(history, settings, kernel, ct)`
3. Manual metadata key guessing for reasoning: `"ReasoningContent"`
4. Tag extraction fallback: `ExtractThinkingTags(result)`

### After (MEAI)
1. Agent calls `_streamingService.StreamAsync(...)` 
2. MEAI handles streaming natively via provider SDK
3. Automatic reasoning extraction: `update.ReasoningContent` (per-provider)
4. Tag extraction still catches Ollama/DeepSeek-R1/Qwen3 inline thinking

**Net result:** Cleaner code, better reasoning support for OpenAI/Gemini, same fallback for tag-based models.

---

## Error Handling

### Timeout Configuration
- Each provider creates custom `HttpClient` with `Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 120000)`
- Applied at service construction, not per-call
- Ollama already fixed in v0.1.18; OpenAI/Gemini will get same treatment

### Streaming Failures
- MEAI throws standard exceptions (`HttpRequestException`, `OperationCanceledException`)
- AgentBase catches and persists partial responses via existing error handling
- No SK-specific exception types to handle

---

## Testing Strategy

### Unit Tests (New)
1. **Ollama streaming service** — verify timeout applied, JSON format set, reasoning extraction works
2. **OpenAI streaming service** — verify response format, reasoning content capture
3. **Google streaming service** — verify mime type metadata, reasoning extraction

### Integration Tests
1. Full workflow with Ollama (timeout + reasoning)
2. Full workflow with OpenAI (reasoning extraction)
3. Full workflow with Google (JSON output + reasoning)

### Migration Validation
- All existing agent tests pass without SK dependencies
- Reasoning content surfaces for all three providers
- Timeout config applies consistently across providers

---

## Implementation Phases

### Phase 1: Core Infrastructure (2-3 days)
- [ ] Create `ILlmStreamingService` interface in `ABook.Core`
- [ ] Implement provider-specific streaming services (Ollama, OpenAI, Google)
- [ ] Update `LlmProviderFactory` to return streaming service instead of kernel/services
- [ ] Replace SK `ChatHistory` with MEAI-compatible message type

### Phase 2: Agent Refactor (3-4 days)
- [ ] Update `AgentBase.StreamResponseAsync` to use new interface
- [ ] Remove SK-specific reasoning extraction code (`chunk.Metadata.TryGetValue`)
- [ ] Verify tag extraction still works for Ollama/DeepSeek-R1/Qwen3
- [ ] Test all existing agents (Writer, Editor, Checker, Planner, Question)

### Phase 3: UI & Configuration (1-2 days)
- [ ] Update settings pages if any provider-specific config needed
- [ ] Verify timeout/temperature/max-tokens controls work with new services
- [ ] Test reasoning content display in chat panel

### Phase 4: Cleanup & Documentation (1 day)
- [ ] Remove SK NuGet packages from projects
- [ ] Update README with MEAI references
- [ ] Update AGENTS.md architecture diagrams
- [ ] Commit migration as single logical change

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| MEAI streaming API changes | Breaking changes in alpha/beta packages | Pin to stable version, monitor NuGet updates |
| Provider-specific quirks not covered | Some features missing vs SK | Use `ChatOptions.Metadata` for extras; fall back to raw SDK if needed |
| Reasoning content format varies | Extraction might miss some formats | MEAI handles this automatically; tag extraction as fallback |

---

## Success Criteria

- [ ] All three providers work with custom HttpClient timeouts
- [ ] Reasoning content surfaces for OpenAI and Gemini (automatic via MEAI)
- [ ] Tag extraction still works for Ollama/DeepSeek-R1/Qwen3
- [ ] No SK dependencies in agent code or NuGet packages
- [ ] All existing tests pass without modification (except provider setup)

---

**Next Step:** Validate this design with you, then proceed to implementation planning.
