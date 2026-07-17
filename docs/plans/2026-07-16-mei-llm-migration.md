# MEAI LLM Migration — Implementation Plan

**Date:** 2026-07-16  
**Status:** Approved for execution  
**Goal:** Replace Semantic Kernel with Microsoft.Extensions.AI (`IChatClient`) for full control over HTTP clients, timeouts, and reasoning content extraction.

---

## Key Decisions (confirmed)

1. **No function calling involved.** QuestionAgent uses plain streaming + TCS pause — SK's function-calling infrastructure is not used anywhere in the codebase. This confirms a clean removal is possible.
2. **Embeddings stay on MEAI `IEmbeddingGenerator<string, Embedding<float>>`.** All 3 providers already expose OpenAI-compatible embedding endpoints; the interface abstraction requires zero agent-side changes. Strategies just construct the underlying client differently.
3. **Big-bang swap.** This is a feature release (no partial deployment), so we can remove SK entirely in one change without a parallel-run migration period.

---

## Architecture: Before → After

### Before (SK)
```
[Agents] → kernel.GetRequiredService<IChatCompletionService>()
         → GetStreamingChatMessageContentsAsync(history, settings, kernel, ct)
         → Manual metadata key guessing for reasoning ("ReasoningContent")
         → Tag extraction fallback for inline <think> blocks
```

### After (MEAI + Provider SDKs)
```
[Agents] → _chatClient.CompleteStreamingAsync(messages, chatOptions, ct)
         → update.ReasoningContent (automatic per-provider)
         → Tag extraction still catches Ollama/DeepSeek-R1/Qwen3 inline thinking
```

**No new `ILlmStreamingService` abstraction layer needed.** Providers expose `IChatClient` directly; agents call streaming methods on the interface. This eliminates an unnecessary indirection.

---

## NuGet Package Changes

### Remove (from all projects)
- `Microsoft.SemanticKernel` — no longer used
- `Microsoft.SemanticKernel.Connectors.Google 1.74.0-alpha` — replaced by MEAI Google connector
- `Microsoft.SemanticKernel.Connectors.Ollama 1.74.0-alpha` — replaced by MEAI Ollama client

### Keep (already referenced)
- `OpenAI 2.12.0` — used for OpenAI embeddings and OpenAI-compatible endpoints; also needed directly for OpenAI chat streaming with custom HttpClients

### Add (to ABook.Infrastructure only)
- `Microsoft.Extensions.AI 1.*` — core MEAI abstractions (`IChatClient`, `IEmbeddingGenerator`)
- `Microsoft.Extensions.AI.Ollama 1.*` — Ollama provider implementation of IChatClient + IEmbeddingGenerator
- `Microsoft.Extensions.AI.OpenAI 1.*` — OpenAI provider (wraps the existing OpenAI SDK)

Note: Google embeddings continue using the existing `OpenAI` package via its OpenAI-compatible endpoint (`https://generativelanguage.googleapis.com/v1beta/openai`). No new MEAI Google package needed for embeddings. For chat, we'll use the `GoogleAIClient` from `Microsoft.Extensions.AI.Google` or call Google's REST API directly — see Task 2.

---

## Files to Modify / Delete

### Delete
- `src/ABook.Infrastructure/Llm/ILlmProviderStrategy.cs` — replaced by direct MEAI client creation
- `src/ABook.Infrastructure/Llm/Strategies/OllamaProviderStrategy.cs` — rewritten without SK
- `src/ABook.Infrastructure/Llm/Strategies/OpenAIProviderStrategy.cs` — rewritten without SK
- `src/ABook.Infrastructure/Llm/Strategies/GoogleAIStudioProviderStrategy.cs` — rewritten without SK
- `src/ABook.Infrastructure/Llm/OpenAIProviderHelpers.cs` — may be simplified or removed (see Task 2)

### Modify
- `src/ABook.Core/ABook.Core.csproj` — remove `Microsoft.SemanticKernel` reference
- `src/ABook.Infrastructure/ABook.Infrastructure.csproj` — swap SK packages for MEAI packages
- `src/ABook.Agents/ABook.Agents.csproj` — remove `Microsoft.SemanticKernel` reference
- `src/ABook.Core/Interfaces/ILlmProviderFactory.cs` — replace SK return types with MEAI types
- `src/ABook.Infrastructure/Llm/LlmProviderFactory.cs` — rewrite to create MEAI clients directly
- `src/ABook.Agents/AgentBase.cs` — replace `Kernel`/`ChatHistory` usage with MEAI equivalents in `StreamResponseAsync`, `GetRagContextAsync`, `IndexChapterAsync`
- All 7 agent files (`QuestionAgent.cs`, `StoryBibleAgent.cs`, `CharactersAgent.cs`, `PlotThreadsAgent.cs`, `PlannerAgent.cs`, `WriterAgent.cs`, `EditorAgent.cs`, `ContinuityCheckerAgent.cs`) — remove SK using statements, adapt `GetKernelAsync` call
- `src/ABook.Api/Program.cs` — if any SK-specific DI registrations exist (unlikely; check first)

---

## New Types to Create

### In `ABook.Infrastructure.Llm`
- `OllamaChatClientFactory` — creates MEAI `IChatClient` for Ollama with custom HttpClient + timeout
- `OpenAiChatClientFactory` — creates MEAI `IChatClient` for OpenAI (with optional endpoint)
- `GoogleAiStudioChatClientFactory` — creates chat client for Google AI Studio

### In `ABook.Infrastructure.Llm.Strategies` (simplified)
- Keep the strategy pattern but slimmed down to just config mapping:
  ```csharp
  public interface IProviderConfigMapper {
      ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null);
  }
  ```

---

## Implementation Tasks

### Task 1: Update NuGet packages and project files

**ABook.Core.csproj:** Remove `Microsoft.SemanticKernel`.  
**ABook.Infrastructure.csproj:** Replace SK packages with MEAI packages (see table above). Add `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.Ollama`, `Microsoft.Extensions.AI.OpenAI`.  
**ABook.Agents.csproj:** Remove `Microsoft.SemanticKernel`.  

Verification: `dotnet restore` succeeds in all three projects.

---

### Task 2: Redesign `ILlmProviderFactory` interface

Replace SK-specific methods with MEAI equivalents:

```csharp
public interface ILlmProviderFactory
{
    /// <summary>Creates a chat client for streaming completions.</summary>
    IChatClient CreateChatClient(LlmConfiguration config);

    /// <summary>Creates an embedding generator (unchanged from current).</summary>
    IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(LlmConfiguration config);

    /// <summary>Buils ChatOptions from config + optional JSON schema.</summary>
    ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null);
}
```

Remove: `CreateChatCompletion`, `CreateKernel`, `CreateExecutionSettings`.  
Verification: Interface compiles; no consumers reference removed methods yet (they'll be updated in Task 3).

---

### Task 3: Rewrite provider strategies without SK

Replace each strategy with a MEAI-native implementation:

#### Ollama
```csharp
public IChatClient CreateChatClient(LlmConfiguration config) {
    var httpClient = new HttpClient {
        BaseAddress = new Uri(config.Endpoint),
        Timeout = TimeSpan.FromMilliseconds(config.TimeoutMs ?? 120_000)
    };
    return new OllamaClient(httpClient, config.ModelName);
}

public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null) {
    var options = new ChatOptions {
        Temperature = (float)(config.Temperature > 0 ? config.Temperature : 0.7f),
        MaxOutputTokens = config.MaxTokens,
    };
    if (!string.IsNullOrWhiteSpace(jsonSchema))
        options.Metadata ??= [];
    // Ollama: format "json" via metadata; reasoning_effort via additional options
    return options;
}
```

#### OpenAI
```csharp
public IChatClient CreateChatClient(LlmConfiguration config) {
    var uri = string.IsNullOrWhiteSpace(config.Endpoint)
        ? null : new Uri(config.Endpoint);
    // Use OpenAI SDK directly for custom HttpClient support
    var client = uri != null
        ? new OpenAIClient(uri, new ApiKeyCredential(config.ApiKey ?? ""))
        : new OpenAIClient(new ApiKeyCredential(config.ApiKey ?? ""));
    return new OpenAIChatClient(client.GetChatClient(config.ModelName));
}
```

#### Google AI Studio
- Embeddings: unchanged (uses `OpenAIProviderHelpers` with OpenAI-compatible endpoint)
- Chat: use `GoogleGeminiChatClient` from MEAI or direct REST calls — decide during implementation based on available MEAI version

Verification: Each strategy compiles in isolation. No SK using statements remain.

---

### Task 4: Rewrite `LlmProviderFactory`

Replace factory to dispatch via provider enum → create MEAI clients directly:

```csharp
public class LlmProviderFactory : ILlmProviderFactory {
    private static readonly Dictionary<LlmProvider, IProviderConfigMapper> Mappers = ...;
    
    public IChatClient CreateChatClient(LlmConfiguration config) =>
        GetStrategy(config.Provider).CreateChatClient(config);

    // Embedding stays same — strategies already use MEAI IEmbeddingGenerator via .AsIEmbeddingGenerator()
    public IEmbeddingGenerator<string, Embedding<float>> CreateEmbeddingGeneration(...) => ...;
    
    public ChatOptions BuildChatOptions(LlmConfiguration config, string? jsonSchema = null) =>
        GetStrategy(config.Provider).BuildChatOptions(config, jsonSchema);
}
```

Verification: `dotnet build` succeeds in ABook.Infrastructure.

---

### Task 5: Refactor `AgentBase.StreamResponseAsync`

Replace the SK-specific call chain:

**Before:**
```csharp
var chat = kernel.GetRequiredService<IChatCompletionService>();
var settings = LlmFactory.CreateExecutionSettings(config, jsonSchema);
await foreach (var chunk in chat.GetStreamingChatMessageContentsAsync(history, settings, kernel, ct)) {
    if (chunk.Metadata is not null && chunk.Metadata.TryGetValue(ReasoningMetaKey, ...)) ...
}
```

**After:**
```csharp
using var chatClient = LlmFactory.CreateChatClient(config);
var chatOptions = LlmFactory.BuildChatOptions(config, jsonSchema);
var messages = history.Select(m => new ChatMessage(ChatRole.System, m.Content)).ToList(); // or however current code builds messages

await foreach (var update in chatClient.CompleteStreamingAsync(messages, chatOptions, ct)) {
    if (!string.IsNullOrEmpty(update.ReasoningContent))
        thinkingSb.Append(update.ReasoningContent);  // automatic per-provider
    
    if (update.Content is { Length: > 0 } token)
        sb.Append(token);
}
```

Key changes:
- Remove `Kernel kernel` parameter from StreamResponseAsync signature
- Replace manual metadata key guessing with `update.ReasoningContent` property
- Tag extraction fallback stays unchanged (still catches inline `<think>` blocks)
- Remove `kernel.GetRequiredService<IChatCompletionService>()` — client is created fresh per call

Verification: AgentBase compiles. Reasoning content flows through `update.ReasoningContent`.

---

### Task 6: Refactor all agents to remove SK dependencies

For each agent file (QuestionAgent, StoryBibleAgent, CharactersAgent, PlotThreadsAgent, PlannerAgent, WriterAgent, EditorAgent, ContinuityCheckerAgent):
1. Remove `using Microsoft.SemanticKernel;` and `using Microsoft.SemanticKernel.ChatCompletion;`
2. Replace `GetKernelAsync(bookId)` → `(LlmConfiguration config)` (just return the config, no kernel)
3. Update call sites: pass `config` to `StreamResponseAsync` instead of `(kernel, config)` tuple

Also update `AgentBase.GetRagContextAsync` and `AgentBase.IndexChapterAsync`:
- These already use `factory.CreateEmbeddingGeneration(config)` — **no changes needed** since embeddings stay on MEAI IEmbeddingGenerator.

Verification: All agent files compile without SK imports.

---

### Task 7: Update message type conversion (ChatHistory → ChatMessage)

SK's `ChatHistory` uses string roles ("system", "user", "assistant"). MEAI uses typed `ChatMessage` with `ChatRole.System`, `ChatRole.User`, etc.

Create a small converter in AgentBase or inline:
```csharp
private static IEnumerable<ChatMessage> ToChatMessages(ChatHistory history) {
    foreach (var m in history) {
        var role = m.Role switch {
            "system" => ChatRole.System,
            "user" => ChatRole.User,
            _ => ChatRole.Assistant
        };
        yield return new ChatMessage(role, m.Content ?? "");
    }
}
```

Or better: stop using SK `ChatHistory` entirely in agent code. Replace with a simple list built inline per call:
```csharp
var messages = new List<ChatMessage>();
messages.Add(new ChatMessage(ChatRole.System, systemPrompt));
messages.Add(new ChatMessage(ChatRole.User, userMessage));
```

Verification: No `ChatHistory` references remain in agent code or core.

---

### Task 8: Verify build and run end-to-end

1. Run `dotnet build` across the entire solution — zero errors, zero SK warnings
2. Start Docker Compose (`docker-compose up --build`)
3. Login to the app
4. Create a book with Ollama configured
5. Run "Plan Book" — verify streaming output in chat panel works for all 4 phases (StoryBible, Characters, PlotThreads, Chapters)
6. Verify reasoning content extraction works (check if any agent uses thinking models; otherwise just confirm no errors)
7. Switch provider to OpenAI, create a book, run a short test
8. Verify timeout config: set `TimeoutMs` in LLM config and confirm it applies

---

## Risks & Mitigations

| Risk | Impact | Mitigation |
|------|--------|------------|
| MEAI Ollama client API differs from SK connector | Streaming/timeout behavior breaks | Test with Ollama explicitly; pin to stable MEAI version |
| Google/Ollama have no official MEAI connectors | Must write custom IChatClient adapters | Write adapters in-house using OllamaSharp (Ollama) and Gemini REST API (Google); tested before merge |
| Custom HttpClient disposal timing | Connection leaks or premature closure | Use `using` on the IChatClient per streaming call (already scoped); HttpClients owned by factories live for DI scope duration |
| OpenAI SDK version compatibility with MEAI wrapper | Type mismatches in ChatOptions | Pin OpenAI to current 2.12.0; verify MEAI.OpenAI wraps it correctly |

---

## Success Criteria

- [ ] `dotnet build` succeeds across all projects with zero SK package references
- [ ] All three providers (Ollama, OpenAI, Google AI Studio) stream completions via MEAI
- [ ] Custom HttpClient timeouts apply consistently (`config.TimeoutMs`)
- [ ] Reasoning content surfaces automatically for OpenAI via `update.ReasoningContent`; Ollama/Google use AgentBase tag fallback
- [ ] Tag extraction fallback still works for Ollama/DeepSeek-R1/Qwen3 inline `<think>` blocks
- [ ] Embedding pipeline (RAG + chapter indexing) continues working unchanged
- [ ] End-to-end workflow runs successfully in Docker Compose with at least one provider

---

## Execution Order

```
Task 1 → Task 2 → Task 3 → Task 4 → Task 5 → Task 6 → Task 7 → Task 8
```

Tasks 1-4 are infrastructure (compile-safe in isolation).  
Tasks 5-7 are agent refactor (must compile together).  
Task 8 is verification.
