# Plan: Agentic Book-Writing ASP.NET Core App

## TL;DR

Build a Docker-packaged ASP.NET Core 10 web app with a React (TypeScript/Vite) UI **served as static files from wwwroot** (NOT a separate Docker service) that uses Semantic Kernel to orchestrate four AI agents (Planner, Writer, Editor, Continuity Checker) for collaborative book writing. Agents stream progress via SignalR and can pause to ask the user plot-clarifying questions. PostgreSQL stores relational data; **pgvector** (PostgreSQL extension) stores chapter embeddings for RAG-based context retrieval. LLM provider is pluggable (Ollama by default, swappable to OpenAI/Azure/Anthropic via configuration). Multi-user with cookie-based authentication.

---

## Architecture Overview

```
[React SPA (static files in ASP.NET wwwroot — single container)] 
    ↕ REST API + SignalR
[ASP.NET Core 10 API]
    ↕ Semantic Kernel (pluggable LLM connector)
    ↕ EF Core + pgvector
[PostgreSQL (Docker, pgvector/pgvector:pg16)]    [Ollama (host machine)]
```

Docker Compose runs: **ASP.NET app (with React static files baked in) + PostgreSQL**. Ollama is external on the host. React is NOT a separate service — it's built in a Dockerfile stage and copied into `wwwroot/`.

---

## Data Model

- **Book**: Id, Title, Premise, Genre, TargetChapterCount, Status (Draft/InProgress/Complete), Language, StoryBibleSystemPrompt, CharactersSystemPrompt, PlotThreadsSystemPrompt, ChapterOutlinesSystemPrompt, WriterSystemPrompt, EditorSystemPrompt, ContinuityCheckerSystemPrompt, UserId (FK → AppUser), CreatedAt, UpdatedAt
- **Chapter**: Id, BookId, Number, Title, Outline, Content (markdown), Status (Outlined/Writing/Review/Editing/Done), CreatedAt, UpdatedAt
- **AgentMessage**: Id, BookId, ChapterId (nullable), AgentRole, MessageType (Content/Question/Answer/SystemNote/Feedback), Content, IsResolved, CreatedAt
- **LlmConfiguration**: Id, BookId (nullable, FK → Book), **UserId (nullable, FK → AppUser)**, Provider (Ollama/OpenAI/Azure/Anthropic), ModelName, Endpoint, ApiKey (nullable), EmbeddingModelName (nullable)
  - Lookup chain: book-specific (BookId) → user-default (UserId, no BookId) → global (neither)
- **LlmPreset**: Id, UserId (nullable, FK → AppUser), Name, Provider, ModelName, Endpoint, ApiKey (nullable), EmbeddingModelName (nullable), CreatedAt, UpdatedAt
  - Visible to: own presets (UserId = currentUser) + global presets (UserId = null)
  - Managed via `GET/POST /api/presets`, `PUT/DELETE /api/presets/{id}`
- **LLM Presets page**: `Presets.tsx` at `/presets` route. Full CRUD for user-owned presets. Global presets (userId=null) are read-only. Dashboard header has a "🔑 Presets" link.
- **"Save as Preset" in Settings**: LLM config form has a "Save as Preset…" button that opens an inline name input. Checks for duplicate name among user-owned presets (case-insensitive); prompts to overwrite if found, then calls `updatePreset`; otherwise calls `createPreset`. Settings page retains the "Apply Preset" dropdown for filling settings from an existing preset.
- **Credential Presets CRUD removed from Settings page**: the full preset management form has been moved to the dedicated `/presets` page. Settings only retains the dropdown for applying presets and the save-as-preset flow.
- **AppUser**: Id, Username, PasswordHash, IsAdmin, **ApiToken (nullable GUID string)**, CreatedAt

### Vector Store (PostgreSQL + pgvector)
- **ChapterEmbedding** — `ChapterEmbeddings` table in PostgreSQL, stores chunked chapter text with embeddings
  - Each chapter is split into ~500-token overlapping chunks
  - Metadata: BookId, ChapterId, ChapterNumber, ChunkIndex
  - Used by agents for RAG: query relevant passages across all chapters without exceeding context window
  - Embedding model: configurable (default: Ollama embedding model or OpenAI `text-embedding-3-small`)
  - `vector` column type (dimension-free, supports any embedding model)
  - Unique index on `(BookId, ChapterId, ChunkIndex)` + index on `BookId`

---

## Agent Roles & Workflow

### Planner Agent
- Input: Book premise + user guidance
- Output: Chapter outlines (title + synopsis per chapter)
- Can ask: "Should the story have a subplot about X?" etc.

### Writer Agent
- Input: Chapter outline + previous chapters' summaries (for context)
- Output: Full chapter content in markdown
- Can pause mid-generation via `[ASK: question]` marker to ask the user about pivotal plot/character decisions; answer is fed back and generation continues
- Can ask: "How should character Y react to event Z?"

### Editor Agent
- Input: Written chapter
- Output: Edited chapter + list of suggested changes
- Can ask: "This passage contradicts chapter N. Which version is canonical?"

### Continuity Checker Agent
- Input: All chapters written so far
- Output: List of inconsistencies + suggested fixes
- Can ask: "Character's eye color is brown in Ch1 but blue in Ch5. Which is correct?"

### Human-in-the-Loop Flow
1. Agent encounters ambiguity → calls `AskUserAndWaitAsync` in `AgentBase`
2. Persists `AgentMessage` (type=Question) + notifies UI via SignalR
3. Registers a `TaskCompletionSource<string>` in `AgentRunStateService.SetPending`
4. Sets run state to `WaitingForInput`; `await tcs.Task` blocks the agent coroutine
5. SignalR pushes `AgentQuestion` event to React UI; answer form appears in chat panel
6. User types answer → `POST /api/books/{id}/messages/answer` → `AgentOrchestrator.ResumeWithAnswerAsync`
7. `TryResolvePending` calls `tcs.SetResult(answer)` → agent resumes with answer injected into context
8. Cancellation (Stop button) calls `CancelRun` which cancels the CTS and calls `tcs.TrySetCanceled()`

---

## Phases

### Phase 1: Project Scaffolding & Infrastructure
1. Create solution structure:
   - `ABook.slnx` (VS Solution XML format, requires .NET 9+ SDK)
   - `src/ABook.Api/` — ASP.NET Core Web API project (.NET 10)
   - `src/ABook.Core/` — Class library (domain models, interfaces)
   - `src/ABook.Infrastructure/` — Class library (EF Core, LLM connectors)
   - `src/ABook.Agents/` — Class library (Semantic Kernel agent definitions)
   - `src/abook-ui/` — React + TypeScript + Vite app
2. Set up Docker infrastructure:
   - `Dockerfile` (multi-stage: build .NET + build React → final runtime image with static files in wwwroot)
   - `docker-compose.yml` (app + PostgreSQL with pgvector, with `extra_hosts` for host Ollama access)
   - `.dockerignore`
3. Configure PostgreSQL with EF Core (Npgsql):
   - `AppDbContext` with entities from data model
   - Initial migration
   - Connection string from environment variables / `appsettings.json`
4. Configure pgvector:
   - `IVectorStoreService` interface in `ABook.Core`
   - `PgvectorVectorStoreService` in `ABook.Infrastructure.VectorStore` using `Pgvector.EntityFrameworkCore`
   - `ChapterEmbedding` EF Core entity in `ABook.Infrastructure.VectorStore`
   - EF migration `AddChapterEmbeddings` creates `CREATE EXTENSION IF NOT EXISTS vector` + `ChapterEmbeddings` table

### Phase 2: Backend Core (API + SignalR) — *depends on Phase 1*
5. Define domain models in `ABook.Core`:
   - `Book`, `Chapter`, `AgentMessage`, `LlmConfiguration` entities
   - `BookStatus`, `ChapterStatus`, `AgentRole`, `MessageType` enums
   - `IBookRepository`, `IAgentOrchestrator`, `ILlmProviderFactory`, `IVectorStoreService` interfaces
6. Implement EF Core repositories in `ABook.Infrastructure`
7. Build REST API controllers in `ABook.Api`:
   - `BooksController` — CRUD for books
   - `ChaptersController` — CRUD for chapters within a book
   - `MessagesController` — post answers, get conversation history
   - `ConfigurationController` — manage LLM provider settings
   - `AgentController` — start/stop agent runs; returns 202 immediately (fire-and-forget)
   - `AuthController` — login, register, logout, current user (`/api/auth/me`)
   - `UsersController` — admin-only user CRUD (create, change password, toggle role)
   - `OllamaController` — `GET /api/ollama/models` (proxy to Ollama `/api/tags`), `POST /api/ollama/pull` (SSE stream)
8. Set up SignalR hub (`BookHub`):
   - Methods: `JoinBook(bookId)`, `LeaveBook(bookId)`
   - Events: `AgentStreaming(bookId, chapterId, token)`, `AgentQuestion(bookId, message)`, `AgentStatusChanged(bookId, agentRole, status)`, `ChapterUpdated(bookId, chapterId)`

### Phase 3: Agent Engine (Semantic Kernel) — *depends on Phase 2*
9. Configure Semantic Kernel with pluggable LLM connector:
   - `ILlmProviderFactory` that creates SK `IChatCompletionService` based on `LlmConfiguration`
   - Support Ollama via `OllamaApiClient` / OpenAI-compatible endpoint
   - Support OpenAI, Azure OpenAI, Anthropic connectors behind same interface
10. Implement vector store integration for RAG:
    - On chapter save/update → chunk text → generate embeddings → upsert to `ChapterEmbeddings` table via pgvector
    - `RetrieveContext(bookId, query, topK)` method for agents to pull relevant passages
    - Agents use retrieved context instead of full chapter text to stay within context window
11. Implement agent orchestrator (`AgentOrchestrator`):
    - Manages agent run lifecycle (start, pause, resume, complete)
    - Fire-and-forget execution via `RunInBackground` helper using `IServiceScopeFactory`
    - `AgentRunStateService` singleton tracks active runs cross-request; duplicate run returns 409
    - Returns HTTP 202 immediately; progress streamed via SignalR
12. Implement each agent as a Semantic Kernel function/plugin:
    - `StoryBibleAgent` — Phase 1: generates Story Bible JSON, streams with `AgentRole.StoryBibleAgent`
    - `CharactersAgent` — Phase 2: generates Character Cards JSON, streams with `AgentRole.CharactersAgent`
    - `PlotThreadsAgent` — Phase 3: generates Plot Threads JSON, streams with `AgentRole.PlotThreadsAgent`
    - `PlannerAgent` — Phase 4: generates Chapter Outlines JSON; also owns public Q&A helpers (`GatherInitialQuestionsAsync`, `AskQuestionsAsync`, `LoadExistingQaContextAsync`) called by `AgentOrchestrator`
    - `WriterAgent` — system prompt for creative writing, streams tokens via SignalR, uses RAG for prior chapter context
    - `EditorAgent` — system prompt for editing/improving prose
    - `ContinuityCheckerAgent` — system prompt for cross-chapter analysis, heavy RAG user
13. Implement question-pause mechanism:
    - Agent detects need for clarification (via tool call or special token in prompt)
    - Creates `AgentMessage` (Question), sets run to `WaitingForInput`
    - On user answer, run resumes with answer injected into chat history

### Phase 4: React UI — *parallel with Phase 3*
14. Scaffold React app with Vite + TypeScript:
    - Configure proxy to ASP.NET dev server
    - Install: `react-router`, `@microsoft/signalr`, `zustand` (state), `react-markdown`
15. Build pages/components:
    - **Dashboard**: List of book projects, create new book
    - **Book Detail**: Overview, chapter list with statuses, agent run buttons (disabled while running), spinner banner
    - **Chapter View**: Rendered markdown content, edit capability
    - **Agent Chat Panel**: Sidebar/panel showing agent messages, questions, answer input
    - **Settings**: LLM provider + Ollama model management (dropdown of installed/common models, pull with SSE progress), per-book language, per-agent system prompt overrides (collapsible)
    - **Login**: Cookie-based auth login form
    - **Admin → Users**: Admin-only page for user CRUD
    - **Real-time indicators**: Streaming text as agents write, status badges per agent, planning output progressively parsed into chapter cards (`parsePlanningStream`)
16. SignalR integration:
    - `useBookHub` hook connecting to `BookHub`
    - `useAuth` hook / `AuthProvider` context for current user state
    - Auth guard in `App.tsx` redirects unauthenticated users to `/login`
    - Live token streaming rendered in chapter view
    - Toast/notification when agent asks a question
17. Build React for production → output to `src/ABook.Api/wwwroot/`

### Phase 5: Docker & Integration — *depends on Phases 3 & 4*
18. Finalize multi-stage Dockerfile:
    - Stage 1: Build React (`node:20-alpine`, `npm run build`)
    - Stage 2: Build .NET (`mcr.microsoft.com/dotnet/sdk:10.0`, `dotnet publish`)
    - Stage 3: Runtime (`mcr.microsoft.com/dotnet/aspnet:10.0`, copy published + wwwroot)
19. Finalize `docker-compose.yml`:
    - `abook-api` service with environment variables (DB connection, Ollama URL)
    - `postgres` service with volume for data persistence
    - only two services in compose (no Qdrant service)
    - `extra_hosts: ["host.docker.internal:host-gateway"]` for Ollama access
20. ASP.NET SPA fallback middleware to serve React static files for all non-API routes

---

## Relevant Files

- `ABook.slnx` — Solution root (XML format)
- `src/ABook.Core/Models/` — `Book.cs`, `Chapter.cs`, `AgentMessage.cs`, `LlmConfiguration.cs`, `AppUser.cs`, `TokenUsageRecord.cs`, `Enums.cs`
- `src/ABook.Core/Interfaces/` — `IBookRepository.cs`, `IAgentOrchestrator.cs`, `ILlmProviderFactory.cs`, `IVectorStoreService.cs`, `IBookNotifier.cs`, `IUserRepository.cs`
- `src/ABook.Infrastructure/Data/AppDbContext.cs` — EF Core context
- `src/ABook.Infrastructure/Repositories/` — Data access repositories
- `src/ABook.Infrastructure/Llm/LlmProviderFactory.cs` — Pluggable LLM factory
- `src/ABook.Infrastructure/VectorStore/PgvectorVectorStoreService.cs` — pgvector implementation of IVectorStoreService; scoped service using AppDbContext
- `src/ABook.Infrastructure/VectorStore/ChapterEmbedding.cs` — EF Core entity for pgvector embeddings storage
- `src/ABook.Infrastructure/Migrations/` — EF Core migrations (`InitialCreate`, `AddLanguageAndUsers`, `AddUserLlmConfig`, `AddTokenUsageRecord`, `AddPlanningArtifacts`, `AddPlanningPhaseStatus`, `AddPlanningPhasePrompts`, `AddChapterEmbeddings`, `AddAgentRuns`, `AddLlmPresets`, `AddApiToken`)
- `src/ABook.Agents/AgentBase.cs` — Base class for all agents
- `src/ABook.Agents/AgentPrompts.cs` — `PromptPlaceholders` constants + `DefaultPrompts` static class (all 7 agent default prompts)
- `src/ABook.Agents/QuestionAgent.cs` — upfront Q&A: `GatherQuestionsAsync`, `AskQuestionsAsync`, `LoadExistingContextAsync`
- `src/ABook.Agents/StoryBibleAgent.cs`, `CharactersAgent.cs`, `PlotThreadsAgent.cs`, `PlannerAgent.cs`, `WriterAgent.cs`, `EditorAgent.cs`, `ContinuityCheckerAgent.cs`
- `src/ABook.Agents/AgentOrchestrator.cs` — Run lifecycle management
- `src/ABook.Agents/AgentRunStateService.cs` — Singleton run state tracker
- `src/ABook.Core/Models/LlmPreset.cs` — Preset entity (Id, UserId, Name, Provider, ModelName, Endpoint, ApiKey, EmbeddingModelName, timestamps)
- `src/ABook.Api/Auth/ApiTokenAuthenticationHandler.cs` — Bearer token auth scheme used by MCP endpoint; reads `Authorization: Bearer {token}` header, looks up user by `ApiToken` column
- `src/ABook.Api/Mcp/BookMcpTools.cs` — MCP tools: `list_books`, `get_book`, `create_book`, `update_book`, `delete_book`, `get_agent_messages`, `get_agent_status`, `get_token_usage`
- `src/ABook.Api/Mcp/ContentMcpTools.cs` — MCP tools: `get_story_bible`, `update_story_bible`, `list_characters`, `create/update/delete_character`, `list_plot_threads`, `create/update/delete_plot_thread`, `list_chapters`, `get_chapter`, `create/update_chapter`
- `src/ABook.Api/Mcp/AgentMcpTools.cs` — MCP tools: `start_planning`, `continue_planning`, `start_workflow`, `continue_workflow`, `stop_workflow`, `write_chapter`, `edit_chapter`, `run_continuity_check`, `answer_agent_question`
- `src/ABook.Api/Controllers/` — `BooksController`, `ChaptersController`, `MessagesController`, `ConfigurationController`, `AgentController`, `AuthController`, `UsersController`, `OllamaController`, `PresetsController`
- `src/ABook.Api/Hubs/BookHub.cs` — SignalR hub
- `src/ABook.Core/Models/AgentRun.cs` — Persisted agent run entity (Id GUID, BookId, RunType, Status, CurrentRole, ChapterId, PendingMessageId, WorkflowContext, timestamps)
- `src/ABook.Api/HostedServices/RunRecoveryService.cs` — Startup `BackgroundService`: rehydrates `WaitingForInput` runs (creates fresh TCS) and orphans stale `Running` runs
- `src/ABook.Api/Program.cs` — App configuration (cookie auth, EF Core, SignalR, pgvector, SK)
- `src/abook-ui/src/pages/BookDetail.tsx` — Main book view: sidebar chapter list, chapter content, agent chat panel. Overview area now has 4 tabs: Overview (book details/edit), Story Bible (edit form), Characters (card list + CRUD), Plot Threads (card list + CRUD). Chapter view shows POV character, foreshadowing/payoff meta panel. Loads StoryBible/Characters/PlotThreads on mount and refreshes on `ChapterUpdated` SignalR events.
- `src/abook-ui/src/pages/Presets.tsx` — Dedicated page for credential preset CRUD (`/presets` route). Lists all visible presets (user-owned + global), with edit/delete actions. Global presets show a "global" badge and cannot be deleted.
- `src/abook-ui/src/hooks/` — `useBookHub.ts`, `useAuth.tsx`
- `src/abook-ui/src/utils/bookHtmlExport.ts` — Client-side HTML export: markdown→HTML converter, 6 color presets, font-size controls; `downloadBookAsHtml(book)` triggers browser download
- `src/abook-ui/src/api.ts` — Typed API client
- `src/abook-ui/src/App.tsx` — Router + auth guard
- `Dockerfile` — Multi-stage build (Node 20 + .NET 10 SDK + .NET 10 runtime)
- `docker-compose.yml` — App + PostgreSQL (pgvector/pgvector:pg16)
- `.dockerignore`

---

## Verification

1. `docker-compose up --build` starts app + PostgreSQL, React UI loads at `http://localhost:5000`
2. Create a book via UI → verify stored in PostgreSQL
3. Start planning → Planner agent streams chapter outlines via SignalR, visible in UI in real-time
4. Agent asks a question → notification appears in chat panel → answer submitted → agent resumes
5. Write a chapter → Writer streams content → Editor reviews → Continuity Checker analyzes
6. Switch LLM provider in settings → next agent run uses new provider
7. `docker-compose down && docker-compose up` → data persists (PostgreSQL volume)

---

## Decisions

- **.NET 10** (latest; Docker images `mcr.microsoft.com/dotnet/sdk:10.0` + `aspnet:10.0`)
- **`.slnx` solution format** — requires .NET 9+ SDK (XML-based, lighter than classic `.sln`)
- **Ollama accessed via host.docker.internal** — not containerized, user manages it externally
- **LLM provider is pluggable** — abstracted behind `ILlmProviderFactory`, configured per-book or globally; supported: Ollama, OpenAI, AzureOpenAI, Anthropic, GoogleAIStudio. `LMStudio` enum value kept for DB compatibility (int=4) but no longer has a factory strategy — use OpenAI provider with a custom endpoint instead.
- **SK Ollama connector** is alpha (`Microsoft.SemanticKernel.Connectors.Ollama` 1.x-alpha); suppress `SKEXP0070` pragma
- **Agents use Semantic Kernel function calling** for the "ask question" tool — agent invokes a `AskUser` function which triggers the pause mechanism
- **Agent runs are fire-and-forget** — `AgentController` returns 202 immediately; `AgentRunStateService` singleton tracks state; duplicate run returns 409
- **Cookie authentication** — multi-user support with `IPasswordHasher<AppUser>`; admin role for user management
- **Per-book customization** — `Language` field and per-agent system prompt overrides stored on `Book` entity
- **Ollama model management** — `OllamaController` proxies Ollama's `/api/tags` and streams pull progress via SSE
- **Markdown only** for book output — no DOCX/PDF export (can be added later)
- **pgvector for vector storage** — embeddings stored directly in PostgreSQL via the `vector` column type; no separate Docker service needed; `Pgvector.EntityFrameworkCore 0.*` package; dimension-free `vector` type to support any embedding model
- **`IVectorStoreService` registered as Scoped** — `PgvectorVectorStoreService` injects `AppDbContext` directly; simpler than singleton+factory pattern used with Qdrant
- **`UseVector()` on EF options builder** — `options.UseNpgsql(cs, o => o.UseVector())` in `Program.cs` with `using Pgvector.EntityFrameworkCore;`; requires `Pgvector.EntityFrameworkCore` referenced directly in both `ABook.Infrastructure` and `ABook.Api`
- **`pgvector/pgvector:pg16` Docker image** — replaces `postgres:16-alpine`; identical behaviour but with the pgvector extension pre-installed; only one service in compose (Qdrant removed)
- **React UI is static files** — built in Dockerfile, served from `wwwroot/` by ASP.NET Core, NOT a separate Docker service
- **`IBookNotifier` interface** (`SignalRBookNotifier` implementation) decouples agent/orchestrator code from direct SignalR hub dependency: `LlmConfiguration` has nullable `UserId`; lookup chain is book-specific → user-default → global. `ConfigurationController` automatically sets `UserId` from cookie claims when saving a global (non-book) config. Agents resolve config via `GetKernelAsync` which passes `book.UserId`.
- **Default system prompts API**: `GET /api/books/{id}/default-prompts` returns pre-interpolated default prompts. Returns 7 keys: `storyBibleSystemPrompt`, `charactersSystemPrompt`, `plotThreadsSystemPrompt`, `chapterOutlinesSystemPrompt`, `writerSystemPrompt`, `editorSystemPrompt`, `continuityCheckerSystemPrompt`. Settings page shows these as placeholders and has a "Load Defaults" button that pre-fills empty textarea fields.
- **Migration**: `AddPlanningPhaseStatus` adds `StoryBibleStatus`, `CharactersStatus`, `PlotThreadsStatus`, `ChaptersStatus` columns. `AddPlanningPhasePrompts` renames `PlannerSystemPrompt` → `StoryBibleSystemPrompt` and adds `CharactersSystemPrompt`, `PlotThreadsSystemPrompt`, `ChapterOutlinesSystemPrompt`.
- **pgvector cleanup**: `IVectorStoreService.DeleteCollectionAsync` deletes all embeddings for the book; called by `BooksController.Delete` (non-fatal). `ChaptersController.Update` calls `DeleteChapterChunksAsync` when content is cleared to empty.
- **LlmProvider.LMStudio deleted from factory**: enum value kept (int=4) to avoid breaking existing DB rows; the factory throws `NotSupportedException` if invoked. Users should switch to OpenAI provider with endpoint `http://host.docker.internal:1234/v1`.
- **LlmProvider.GoogleAIStudio (int=5)**: uses `GoogleAIGeminiChatCompletionService` from `Microsoft.SemanticKernel.Connectors.Google 1.74.0-alpha`. Embeddings use Google's OpenAI-compatible endpoint (`https://generativelanguage.googleapis.com/v1beta/openai`) via the same `OpenAIClient` pattern. API key is required and mandatory. Default model suggestions: `gemini-2.0-flash`, `gemini-2.5-pro` etc. Default embedding model: `text-embedding-004`.
- **LlmProvider.OpenAI endpoint support**: when `Endpoint` is non-empty, uses the URI-based `OpenAIChatCompletionService` overload so any OpenAI-compatible API (Groq, Together, etc.) works. When `Endpoint` is empty, uses the standard apiKey-only overload (real OpenAI API). Same logic applies to `CreateEmbeddingGeneration` and `CreateKernel`.
- **LlmProvider.Anthropic**: Anthropic's native API is not OpenAI-compatible and the SK Anthropic connector is not available for .NET 10. Uses SK's OpenAI connector with a custom endpoint — requires an OpenAI-compatible proxy (e.g. LiteLLM at `http://localhost:4000`). Endpoint is mandatory; throws `InvalidOperationException` if not set. Default endpoint hint in Settings UI: `http://localhost:4000`. Proxy note displayed in Settings when Anthropic is selected.
- **LLM debug logging**: set env var `LLM_DEBUG_LOGGING=true` to print full chat history (all messages with roles) before each LLM call and the complete response after to the application log at `Information` level. Implemented as a static flag in `AgentBase` checked in `StreamResponseAsync`.
- **`GET /api/models`** (renamed from `/api/ollama/models`): accepts `?provider=`, `?endpoint=`, `?apiKey=` query params. Routes: `GoogleAIStudio` → calls `https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}` (returns only `gemini-*` models); `OpenAI` → calls `{endpoint}/models` or `https://api.openai.com/v1/models` with `Authorization: Bearer {apiKey}`; default (Ollama) → calls `/api/tags`. Pull endpoint stays at `/api/ollama/pull` (Ollama-specific).
- **Model list in Settings**: fetched dynamically for Ollama, OpenAI, and GoogleAIStudio. For OpenAI and GoogleAIStudio the API key is sent to the backend which calls the provider. Model/embedding fields use a datalist combobox (free-form input + suggestions from fetched list) instead of a select dropdown — custom model names no longer need a separate toggle. Switching provider resets endpoint to the provider's default (only if the current endpoint matches the previous provider's default).
- **ContinuityCheckerAgent uses RAG**: runs three targeted queries (character descriptions, timeline, locations) against pgvector before checking continuity; appends retrieved passages to the LLM prompt alongside the chapter synopsis.
- **`StripLeadingChapterHeading` moved to `AgentBase`** (protected): both `WriterAgent` and `EditorAgent` strip LLM-added chapter headings from prose before saving. Now handles consecutive heading lines, bold-formatted headings, and ordinal word variants ("Chapter One", "Chapter Two" etc.).
- **`InterpolateSystemPrompt` in `AgentBase`** (protected static): substitutes `PromptPlaceholders` tokens in user-supplied or default system prompts. Signature: `InterpolateSystemPrompt(string prompt, Book book, StoryBible? bible = null)`. Book-level tokens always resolved; Story Bible tokens (`{SETTING}`, `{THEMES}`, `{TONE}`, `{WORLD_RULES}`) resolved only when `bible` is passed.
- **`GetPreviousChapterEndingAsync` in `AgentBase`** (protected): returns the last 3 paragraphs of the immediately preceding chapter. `WriterAgent` includes this in the system prompt so prose is narratively continuous even without RAG context.
- **`EditorAgent` notes split**: uses a regex to find any `## Editorial Notes` / `## Editor's Notes` / `## Feedback` etc. heading (case-insensitive) instead of a hard string compare; more resilient to LLM phrasing variation.
- **Settings UI placeholder hint**: the "Custom Agent System Prompts" section now shows a reference block listing all supported template tokens and reminds users that the Editor prompt must end with `## Editorial Notes`.
- **Book detail UI tabs**: When no chapter is selected, the content area shows 4 tabs — Overview (book premise/genre/edit), Story Bible (world-building fields), Characters (role-colored cards with full CRUD), Plot Threads (status-colored cards with type/chapter-number info + CRUD). Chapter view includes a meta bar showing POV character, foreshadowing notes, and payoff notes from the enriched outline. When provided (per-chapter workflow), separates chapters into "preceding facts" vs "chapter under review" and instructs the LLM to report only issues *introduced by* that chapter, ignoring pre-existing issues between earlier chapters. No-id calls (final check, standalone button) retain full cross-manuscript review behaviour. `AgentOrchestrator` per-chapter call sites now pass `chapter.Id`; final checks pass null.
- **Token statistics**: `AgentBase.StreamResponseAsync` now accepts `AgentRole role` (required param before `CancellationToken`). After each LLM streaming call, emits approximate token counts (chars/4) via `IBookNotifier.NotifyTokenStatsAsync` → SignalR `TokenStats` event. Also **persists** a `TokenUsageRecord` row (BookId, ChapterId, AgentRole, PromptTokens, CompletionTokens) via `IBookRepository.AddTokenUsageAsync`. `GET /api/books/{id}/token-usage` returns all persisted records. UI loads historical stats on page load and refreshes them on each `TokenStats` SignalR event; shown as a collapsible `<details>` panel at the bottom of the chat sidebar. Total accumulated tokens shown.
- **Orchestrator simplified with design patterns**: `ExecuteAgentRunAsync` private helper centralises TryStartRun guard + cancellation/error handling + status transitions (Completed/Failed/Cancelled) for all 7 public run methods. `ProcessChapterAsync` private helper deduplicates the PreCheck→Write→Continuity→Edit loop used by both `StartWorkflowAsync` and `ContinueWorkflowAsync`. `AgentRunStateService` now requires `IServiceScopeFactory` + `ILogger` (DI still registers as singleton — framework resolves via constructor).
- **Durable run persistence (restart resilience)**: `AgentRun` entity persists run lifecycle to DB (`AgentRuns` table). `AgentRunPersistStatus` enum: `Running | WaitingForInput | Completed | Failed | Cancelled | Orphaned`. `ExecuteAgentRunAsync` calls `PersistRunStartAsync` on entry and `PersistRunFinishedAsync` on exit. `AskUserAndWaitAsync` calls `PersistRunPausedAsync` (saves `WaitingForInput` + `PendingMessageId`) before blocking and `PersistRunResumedAsync` after the answer arrives. `ResumeWithAnswerAsync` is idempotent (`IsResolved` guard). `RunRecoveryService` (hosted service, 3 s startup delay): scans DB for non-terminal runs; `WaitingForInput` → `RehydrateWaitingRun` creates fresh TCS + re-notifies UI; `Running` → mark `Orphaned`, emit SystemNote AgentMessage, reset in-memory status so user can restart. EF migration: `AddAgentRuns`.
- **Chapter inline edit**: "✎ Edit" button appears on chapter header when not running. Opens an inline form to edit title and outline; saves via `PUT /api/books/{id}/chapters/{chapterId}`.
- **Book inline edit**: "✎ Edit" button on book overview. Opens an inline form to edit title, genre, target chapters, premise/plot; saves via `PUT /api/books/{id}`.
- **Add chapter manually**: "+ Chapter" button at the bottom of the sidebar chapter list. Inline form collects title and outline; auto-assigns the next chapter number; saves via `POST /api/books/{id}/chapters` and immediately selects the new chapter.
- **Default LLM config from env vars**: on startup, `Program.cs` reads `LlmDefaults` config section and upserts the global `LlmConfiguration` record (BookId=null, UserId=null). Env var names follow .NET double-underscore convention: `LlmDefaults__Provider`, `LlmDefaults__ModelName`, `LlmDefaults__Endpoint`, `LlmDefaults__ApiKey`, `LlmDefaults__EmbeddingModelName`. Defaults are pre-populated in `appsettings.json` (Provider=Ollama, ModelName=llama3, Endpoint=http://host.docker.internal:11434). Commented examples in `docker-compose.yml`.
the- **Agent error logging & UI notifications**: `IBookNotifier` gains `NotifyAgentErrorAsync(bookId, agentRole, errorMessage)` → `AgentError` SignalR event. `AgentBase` gains `ReportErrorAsync(bookId, chapterId, role, message)` (protected) and `AgentOrchestrator` gains `ReportAgentErrorAsync(bookId, role, chapterId, message)` (private): both persist a `SystemNote` `AgentMessage` to the DB **and** fire the SignalR event, so errors appear in the chat panel alongside all other agent messages (rendered with `msg-systemnote` CSS class, `❌` prefix). `AgentError` event on the frontend refreshes the message list and switches to the chat panel — the top-of-screen error banner has been removed. All `Exception` catch blocks in `AgentOrchestrator` use this helper. `AgentBase.StreamResponseAsync` still logs errors and warns on empty/short responses.
- **Unexpected cancellation detection**: In `StartWorkflowAsync` and `ContinueWorkflowAsync`, `OperationCanceledException` is split into two catches using an exception filter: `when (ct.IsCancellationRequested)` = user clicked Stop → "Workflow stopped." with no error message; `else` = LLM timeout / connection reset → treated as a real error with `ReportAgentErrorAsync` and "Workflow failed (request cancelled)." progress message.
- **Strategy pattern for LlmProviderFactory**: `ILlmProviderStrategy` interface; each provider has its own class in `src/ABook.Infrastructure/Llm/Strategies/`. `LlmProviderFactory` dispatches via a `static readonly Dictionary<LlmProvider, ILlmProviderStrategy>`. `OpenAIProviderHelpers.CreateOpenAIClient` is a shared static helper used by OpenAI, Anthropic, and GoogleAIStudio strategies. Adding a future provider only requires a new strategy class + registration in the dictionary — no changes to the factory or other providers.
- **`PlannerAgent` safe re-plan**: `DeleteChaptersAsync` is now called **after** the LLM response is successfully parsed, so a JSON parse failure no longer wipes existing chapters. `ParseChapterOutlines` now extracts the JSON array (first `[` … last `]`) before parsing, making it resilient to LLM preamble/postamble text around the array.
- **Ollama embedding fix**: `OllamaTextEmbeddingGenerationService` ctor throws `InvalidCastException` in SK 1.74.0-alpha because its internal `EmbeddingGeneratorEmbeddingGenerationService<string, float>` no longer implements `ITextEmbeddingGenerationService`. Fix: `LlmProviderFactory.CreateEmbeddingGeneration` now uses `OpenAITextEmbeddingGenerationService` with an `OpenAIClient` pointed at Ollama's OpenAI-compatible `/v1` endpoint (supported since Ollama 0.4.x), bypassing the broken Ollama-specific service entirely.
- **`AgentController.Plan`** now calls `CreateRunCts` before starting the planner in background, so the Stop button works during a Plan Only run (same as `workflow/start`).
- **"Plan Only" mode**: `POST /api/books/{id}/agent/plan` runs the full 4-phase Planner (Story Bible → Characters → Plot Threads → Chapter Outlines) and stops, leaving the book in a state where the user can edit outlines/characters/plot threads and then click "Continue" to write chapters. Button appears as 🗗 Plan Only alongside Write Book.
- **Pending question restored on page refresh**: `BookDetail.tsx` now loads messages and agent status together on mount; if status is `WaitingForInput`, the latest unresolved `Question` message is restored as `pendingQuestion` so the user can answer after refreshing the page.
- **Token stats panel scrollable**: `.token-stats-list` CSS now has `max-height: 220px; overflow-y: auto` alongside the existing `overflow-x: auto`, making it scrollable in both directions when there are many entries.
- **Metadata HTML export**: `bookHtmlExport.ts` now exports `generateBookMetadataHtml` + `downloadBookMetadataAsHtml`. Accepts book + storyBible + characters + plotThreads + messages + tokenStats (as `TokenStatEntry[]`). Produces a self-contained themed HTML document (same 6 colour presets, font-size controls) with: Book Information, Chapter Outlines (with POV/foreshadowing/payoff), Story Bible, Characters, Plot Threads, Agent Messages (grouped by chapter), Token Statistics. ToC with anchor links at the top. A "⬇ Download Metadata" button always shows in the sidebar (alongside the conditional "⬇ Download HTML").
- **Planning phase statuses**: `PlanningPhaseStatus { NotStarted, Complete }` enum on `Book` entity for four phases: `StoryBibleStatus`, `CharactersStatus`, `PlotThreadsStatus`, `ChaptersStatus`. EF migration: `AddPlanningPhaseStatus`.
- **Split Planner LLM calls**: Replaced. Each planning phase now uses **one single upfront Q&A round** before any generation. `GatherInitialQuestionsAsync` asks the LLM for clarifying questions about the book premise (returns JSON array); `AskQuestionsAsync` presents each question one at a time via `AskUserAndWaitAsync`. Continuation runs (`isContinuation=true`) skip Q&A and reload existing Q&A context via `LoadExistingQaContextAsync`.
- **Auto-mark phases complete**: Each planning phase agent (`StoryBibleAgent`, `CharactersAgent`, `PlotThreadsAgent`, `PlannerAgent`) marks its own phase `Complete` on success via `book.*Status = Complete; await Repo.UpdateAsync(book)`. Both "Plan Only" (`StartPlanningAsync`) and "Write Book" (`StartWorkflowAsync`) flows benefit automatically.
- **`PromptPlaceholders` + `DefaultPrompts`** in `AgentPrompts.cs`: all placeholder name constants and all 7 agent default prompt templates live here. Agents resolve the effective prompt as `InterpolateSystemPrompt(book.XxxSystemPrompt.HasValue ? book.XxxSystemPrompt : DefaultPrompts.Xxx, book, bible)`. Supported placeholders: `{TITLE}`, `{GENRE}`, `{PREMISE}`, `{LANGUAGE}`, `{CHAPTER_COUNT}` (book-level); `{SETTING}`, `{THEMES}`, `{TONE}`, `{WORLD_RULES}` (Story Bible level — passed through when bible is available). Settings UI shows all tokens in the placeholder reference block.
- **`QuestionAgent`**: dedicated agent class for the upfront Q&A round. `GatherQuestionsAsync` asks the LLM for clarifying questions; `AskQuestionsAsync` presents each one via `AskUserAndWaitAsync`; `LoadExistingContextAsync` rebuilds Q&A context for continuation runs. `AgentOrchestrator.RunPlanningPipelineAsync` uses `_questions.*` methods (not `_planner.*`).
- **`ContinuePlanningAsync`**: `AgentOrchestrator` method reads 4 phase statuses, computes skip flags, calls `RunPlanningPipelineAsync` skipping completed phases. If all 4 complete, emits "All planning phases are already complete." message.
- **`PlanningPhasesController`**: New controller at `api/books/{bookId}/planning-phases`. Three verbs × four phases: `POST /{phase}/complete` (sets status=Complete), `POST /{phase}/reopen` (sets status=NotStarted), `DELETE /{phase}` (deletes data + sets NotStarted). Valid phases: `storybible | characters | plotthreads | chapters`.
- **`AgentController.ContinuePlanning`**: `POST /api/books/{id}/agent/plan/continue` — creates run CTS, launches `ContinuePlanningAsync` in background, returns 202.
- **`BookDetail.tsx` phase UI**: `isPhaseComplete(phase)` helper; `handleContinuePlanning`, `handleCompletePhase`, `handleReopenPhase`, `handleClearPhase` handlers; amber "⏩ Continue Planning" button in agent-actions (visible when any phase is Complete); per-tab phase action bars (status badge + Complete/Reopen/Clear buttons) in Story Bible, Characters, Plot Threads tabs and Chapters sidebar section; green dot indicator (●) on tab labels when phase is Complete.
- **Per-phase AgentRole values**: `AgentRole` enum has `StoryBibleAgent`, `CharactersAgent`, `PlotThreadsAgent` (in addition to `Planner`, `Writer`, `Editor`, `ContinuityChecker`, `Embedder`). Each planning phase streams tokens with its own role so the UI can route them to the correct tab.
- **`AgentStreaming` SignalR event** carries a 4th argument `agentRole: string` (role of the streaming agent). `SignalRBookNotifier.StreamTokenAsync` sends `(bookId, chapterId, agentRole, token)`. `useBookHub.ts` `StreamHandler` type updated: `(bookId, chapterId, agentRole, token) => void`.
- **Planning stream previews**: Each planning tab (Story Bible, Characters, Plot Threads) shows a live `⏳ Generating…` preview while the corresponding agent is streaming. Three progressive parsers in `BookDetail.tsx`: `parseStoryBibleStream` (regex-extracts JSON fields), `parseCharactersStream` (extracts complete `{...}` objects), `parsePlotThreadsStream` (same). All parsers are fault-tolerant — they skip malformed items and keep already-parsed ones.
- **Clear Chat button**: appears in the chat panel header when messages exist and agent is not running. Calls `DELETE /api/books/{id}/messages` → `MessagesController.DeleteAll` → `IBookRepository.DeleteMessagesAsync`.
- **Clear Token Stats button**: appears inside the token stats `<summary>` when stats exist and agent is not running. Calls `DELETE /api/books/{id}/token-usage` → `BooksController.DeleteTokenUsage` → `IBookRepository.DeleteTokenUsageAsync`.

- **MCP server embedded in `ABook.Api`**: `ModelContextProtocol.AspNetCore 1.2.0` package; `AddMcpServer().WithHttpTransport().WithTools<T>()` registration; mounted at `/mcp` via `app.MapMcp("/mcp").RequireAuthorization(...)`
- **ApiToken auth for MCP**: `ApiTokenAuthenticationHandler` implements `Authorization: Bearer {guid}` scheme registered as `"ApiToken"` alongside cookie scheme. MCP endpoint accepts either scheme. `AppUser.ApiToken` column (nullable string) added via `AddApiToken` migration. Plaintext GUID — acceptable for local dev tool; threat model excludes DB-level attackers.
- **One token per user**: simple UX; regenerate to rotate via `POST /api/auth/api-token/regenerate`. `GET /api/auth/api-token` returns current token for display.
- **MCP tool DI pattern**: tool classes (Scoped) inject `IBookRepository`, `AgentRunStateService`, `IServiceScopeFactory`, `IHttpContextAccessor`. User ID extracted from `ClaimTypes.NameIdentifier` on `IHttpContextAccessor.HttpContext.User`.
- **`AddHttpContextAccessor()`** registered in `Program.cs` — required so `IHttpContextAccessor` resolves correctly in MCP tool classes.
- **MCP tools use fire-and-forget pattern** for long-running agent operations: same `RunInBackground` / `IServiceScopeFactory` pattern as `AgentController`. Agent errors logged via `ILogger<AgentMcpTools>`; agent status polled via `get_agent_status` tool.
- **Settings UI `MCP Access` section**: loads token on mount via `getApiToken()`; show/hide toggle + copy-to-clipboard button; regenerate with inline confirmation; collapsible `<details>` with Claude Desktop and VS Code connection config snippets.

---

## Further Considerations

1. **Concurrent agent runs** — Should multiple agents run in parallel on different chapters (e.g., Writer on Ch3 while Editor reviews Ch2)? Recommend yes, with a configurable concurrency limit.
2. **Chapter editing** — Should users be able to manually edit chapter content in the UI (rich markdown editor), or only through agents? Recommend allowing manual edits alongside agent work.

---

## Implementation Notes (Technical Gotchas)

- **EF Core / Npgsql**: Use `10.0.*` versions; both stable as of April 2026
- **`ABook.Agents` pins `Microsoft.EntityFrameworkCore.Relational 10.0.*`** to avoid version conflict with Semantic Kernel
- **pgvector / Npgsql**: `Pgvector.EntityFrameworkCore 0.*` + `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.*`; call `options.UseNpgsql(cs, o => o.UseVector())` + `using Pgvector.EntityFrameworkCore`; requires direct `PackageReference` to `Pgvector.EntityFrameworkCore` in both `ABook.Infrastructure` and `ABook.Api`
- **SK embedding API**: `ITextEmbeddingGenerationService.GenerateEmbeddingsAsync(list)` — not `GenerateEmbeddingAsync`
- **`OllamaPromptExecutionSettings.Temperature`** is `float?` not `double`
- **`Microsoft.AspNetCore.SignalR 1.*`** NuGet package removed — SignalR is built into the framework in .NET 3+
- **Enum JSON serialization**: Register `JsonStringEnumConverter` in `AddJsonOptions` so enum values serialize as strings for the React client
- **`parsePlanningStream`** and three sibling parsers: React helpers that progressively parse the Planner/planning agents' streaming JSON. `parsePlanningStream` → chapter cards; `parseStoryBibleStream` → key/value fields; `parseCharactersStream` + `parsePlotThreadsStream` → complete object extraction. All wrap item parsing in `try/catch` to skip malformed partial JSON.
- **`AskUserAndWaitAsync` in `AgentBase`**: creates a `TaskCompletionSource<string>`, registers it via `AgentRunStateService.SetPending`, sets status to `WaitingForInput`, then `await tcs.Task`. Unblocked by `ResumeWithAnswerAsync` (answer) or `CancelRun` (cancellation). `AgentBase` now takes `AgentRunStateService` as a constructor param.
- **Full autonomous workflow**: `POST /api/books/{id}/agent/workflow/start` runs Plan → Write+Edit each chapter → Continuity check in sequence. Uses a `CancellationTokenSource` from `AgentRunStateService.CreateRunCts`. Stop via `POST .../workflow/stop`.
- **`PlannerAgent`** is Phase 4 only: generates Chapter Outlines from the already-saved Bible/Characters/Threads passed in as parameters.
- **`WriterAgent` mid-generation questions**: the LLM can emit `[ASK: question]` at any point while writing to pause and request the author's input on a pivotal plot/character decision. `WriteWithQuestionsAsync` loops up to 6 rounds: it streams until the marker is detected, extracts the partial prose (before the marker), calls `AskUserAndWaitAsync`, then feeds the partial prose + answer back as a new turn and resumes streaming. Works with any model — no function-calling required.
- **`WorkflowProgress` SignalR event**: emitted by `AgentOrchestrator.StartWorkflowAsync` at each step with `(bookId, step, isComplete)`. UI accumulates steps in `workflowLog` state array shown in sidebar.
