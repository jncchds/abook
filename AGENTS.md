# Plan: Agentic Book-Writing ASP.NET Core App

## TL;DR

Build a Docker-packaged ASP.NET Core 10 web app with a React (TypeScript/Vite) UI **served as static files from wwwroot** (NOT a separate Docker service) that uses MEAI (Microsoft.Extensions.AI) to orchestrate four AI agents (Planner, Writer, Editor, Continuity Checker) for collaborative book writing. Agents stream progress via SignalR and can pause to ask the user plot-clarifying questions. PostgreSQL stores relational data; **pgvector** (PostgreSQL extension) stores chapter embeddings for RAG-based context retrieval. LLM provider is pluggable (Ollama by default, swappable to OpenAI/Azure via configuration). Multi-user with cookie-based authentication.

---

## Architecture Overview

```
[React SPA (static files in ASP.NET wwwroot — single container)] 
    ↕ REST API + SignalR
[ASP.NET Core 10 API]
    ↕ MEAI (pluggable LLM connector)
    ↕ EF Core + pgvector
[PostgreSQL (Docker, pgvector/pgvector:pg16)]    [Ollama (host machine)]
```

Docker Compose runs: **ASP.NET app (with React static files baked in) + PostgreSQL**. Ollama is external on the host. React is NOT a separate service — it's built in a Dockerfile stage and copied into `wwwroot/`.

---

## Data Model

- **Book**: Id, Title, Premise, Genre, TargetChapterCount, Status (Draft/InProgress/Complete), Language, StoryBibleSystemPrompt, CharactersSystemPrompt, PlotThreadsSystemPrompt, ChapterOutlinesSystemPrompt, WriterSystemPrompt, EditorSystemPrompt, ContinuityCheckerSystemPrompt, **HumanAssisted** (bool, default false), **BaseBookId** (nullable self-FK → Book), **SettingsCopiedAt** (nullable UTC timestamp), UserId (FK → AppUser), CreatedAt, UpdatedAt
- **Chapter**: Id, BookId, Number, Title, Outline, Content (markdown), Status (Outlined/Writing/Review/Editing/Done), CreatedAt, UpdatedAt
- **AgentMessage**: Id, BookId, ChapterId (nullable), AgentRole, MessageType (Content/Question/Answer/SystemNote/Feedback), Content, IsResolved, **IsOptional** (bool, default false), CreatedAt
- **LlmConfiguration**: Id, BookId (nullable, FK → Book), **UserId (nullable, FK → AppUser)**, Provider (Ollama/OpenAI/GoogleAIStudio), ModelName, Endpoint, ApiKey (nullable), EmbeddingModelName (nullable)
  - Lookup chain: book-specific (BookId) → user-default (UserId, no BookId) → global (neither)
- **LlmPreset**: Id, UserId (nullable, FK → AppUser), Name, Provider, ModelName, Endpoint, ApiKey (nullable), EmbeddingModelName (nullable), CreatedAt, UpdatedAt
  - Visible to: own presets (UserId = currentUser) + global presets (UserId = null)
  - Managed via `GET/POST /api/presets`, `PUT/DELETE /api/presets/{id}`
- **LLM Presets page**: `Presets.tsx` at `/presets` route. Full CRUD for user-owned presets. Global presets (userId=null) are read-only. Dashboard header has a "🔑 Presets" link.
- **"Save as Preset" in Settings**: LLM config form has a "Save as Preset…" button that opens an inline name input. Checks for duplicate name among user-owned presets (case-insensitive); prompts to overwrite if found, then calls `updatePreset`; otherwise calls `createPreset`. Settings page retains the "Apply Preset" dropdown for filling settings from an existing preset.
- **Credential Presets CRUD removed from Settings page**: the full preset management form has been moved to the dedicated `/presets` page. Settings only retains the dropdown for applying presets and the save-as-preset flow.
- **Two settings pages**: `GlobalSettings.tsx` at `/settings` (MCP Access + LLM config global scope + Ollama pull); `BookSettings.tsx` at `/books/:bookId/settings` (LLM config book-scoped + language + agent system prompt overrides). Old unified `Settings.tsx` deleted.
- **AppUser**: Id, Username, **DisplayName (nullable)**, PasswordHash, IsAdmin, **ApiToken (nullable GUID string)**, CreatedAt

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

### Editor Agent
- Input: Written chapter
- Output: Edited chapter + list of suggested changes

### Continuity Checker Agent
- Input: All chapters written so far
- Output: List of inconsistencies + suggested fixes

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
   - `src/ABook.Agents/` — Class library (MEAI-based agent definitions)
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
   - `ModelsController` — `GET /api/models` (proxy to Ollama `/api/tags` + OpenAI/Google model lists), `POST /api/ollama/pull` (SSE stream). Replaced the old separate `OllamaController`.
8. Set up SignalR hub (`BookHub`):
   - Methods: `JoinBook(bookId)`, `LeaveBook(bookId)`
   - Events: `AgentStreaming(bookId, chapterId, token)`, `AgentQuestion(bookId, message)`, `AgentStatusChanged(bookId, agentRole, status)`, `ChapterUpdated(bookId, chapterId)`

### Phase 3: Agent Engine (MEAI) — *depends on Phase 2*
9. Configure MEAI with pluggable LLM connector:
   - `ILlmProviderFactory` that creates MEAI `IChatClient` based on `LlmConfiguration`
   - Custom chat clients per provider (`OllamaApiClient`, `OpenAiChatClient`, `GoogleAiStudioChatClient`)
   - Ollama uses OpenAI-compatible `/v1` endpoint; Google AI Studio uses streaming REST API
   - All providers implement the same `IChatClient` interface behind a strategy pattern
10. Implement vector store integration for RAG:
    - On chapter save/update → chunk text → generate embeddings → upsert to `ChapterEmbeddings` table via pgvector
    - `RetrieveContext(bookId, query, topK)` method for agents to pull relevant passages
    - Agents use retrieved context instead of full chapter text to stay within context window
11. Implement agent orchestrator (`AgentOrchestrator`):
    - Manages agent run lifecycle (start, pause, resume, complete)
    - Fire-and-forget execution via `RunInBackground` helper using `IServiceScopeFactory`
    - `AgentRunStateService` singleton tracks active runs cross-request; duplicate run returns 409
    - Returns HTTP 202 immediately; progress streamed via SignalR
12. Implement each agent using MEAI `IChatClient`: (system prompt + user message → streaming response via `GetStreamingResponseAsync`)
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
- `VERSION` — Plain-text `MAJOR.MINOR.PATCH` version; single source of truth for app version
- `RELEASE_NOTES.md` — Per-version change log; one `## v{version} — {date}` heading per commit
- `src/ABook.Core/Models/` — `Book.cs`, `Chapter.cs`, `AgentMessage.cs`, `LlmConfiguration.cs`, `AppUser.cs`, `TokenUsageRecord.cs`, `Enums.cs`
- `src/ABook.Core/Interfaces/` — `IBookRepository.cs`, `IAgentOrchestrator.cs`, `ILlmProviderFactory.cs`, `IVectorStoreService.cs`, `IBookNotifier.cs`, `IUserRepository.cs`
- `src/ABook.Infrastructure/Data/AppDbContext.cs` — EF Core context
- `src/ABook.Infrastructure/Repositories/` — Data access repositories
- `src/ABook.Infrastructure/Llm/LlmProviderFactory.cs` — Pluggable LLM factory
- `src/ABook.Infrastructure/VectorStore/PgvectorVectorStoreService.cs` — pgvector implementation of IVectorStoreService; scoped service using AppDbContext
- `src/ABook.Infrastructure/VectorStore/ChapterEmbedding.cs` — EF Core entity for pgvector embeddings storage
- `src/ABook.Infrastructure/Migrations/` — EF Core migrations (`InitialCreate`, `AddLanguageAndUsers`, `AddUserLlmConfig`, `AddTokenUsageRecord`, `AddPlanningArtifacts`, `AddPlanningPhaseStatus`, `AddPlanningPhasePrompts`, `AddChapterEmbeddings`, `AddAgentRuns`, `AddLlmPresets`, `AddApiToken`, `AddAssistedGeneration`)
- `src/ABook.Infrastructure/Migrations/20260626111642_AddBookBaseLineage.cs` — adds `Books.BaseBookId` (self-FK, `SetNull`) + `Books.SettingsCopiedAt`
- `src/ABook.Infrastructure/Migrations/20260626235543_AddDisplayName.cs` — adds nullable `Users.DisplayName` column
- `src/ABook.Agents/AgentBase.cs` — Base class for all agents
- `src/ABook.Agents/CheckerResult.cs` — Structured checker output (continuity issues, style issues, summary)
- `src/ABook.Agents/AgentPrompts.cs` — `PromptPlaceholders` constants + `DefaultPrompts` static class (all 7 agent default prompts)
- `src/ABook.Agents/QuestionAgent.cs` — upfront Q&A: `GatherQuestionsAsync`, `AskQuestionsAsync`, `LoadExistingContextAsync`
- `src/ABook.Agents/StoryBibleAgent.cs`, `CharactersAgent.cs`, `PlotThreadsAgent.cs`, `PlannerAgent.cs`, `WriterAgent.cs`, `EditorAgent.cs`, `ContinuityCheckerAgent.cs`
- `src/ABook.Agents/AgentOrchestrator.cs` — Run lifecycle management
- `src/ABook.Agents/AgentRunStateService.cs` — Singleton run state tracker
- `src/ABook.Core/Models/LlmPreset.cs` — Preset entity (Id, UserId, Name, Provider, ModelName, Endpoint, ApiKey, EmbeddingModelName, timestamps)
- `src/ABook.Api/Auth/ApiTokenAuthenticationHandler.cs` — Bearer token auth scheme used by MCP endpoint; reads `Authorization: Bearer {token}` header, looks up user by `ApiToken` column
- `src/ABook.Api/Mcp/UserMcpTools.cs` — User-level MCP tools: `get_current_user`, `get_llm_config`, `set_llm_config`, `list_presets`, `apply_preset`, `generate_book` (creates book + immediately starts full workflow)
- `src/ABook.Api/Mcp/BookMcpTools.cs` — MCP tools: `list_books`, `get_book`, `create_book`, `update_book`, `delete_book`, `get_agent_messages`, `get_agent_status`, `get_token_usage`
- `src/ABook.Api/Mcp/ContentMcpTools.cs` — MCP tools: `get_story_bible`, `update_story_bible`, `list_characters`, `create/update/delete_character`, `list_plot_threads`, `create/update/delete_plot_thread`, `list_chapters`, `get_chapter`, `create/update_chapter`
- `src/ABook.Api/Mcp/AgentMcpTools.cs` — MCP tools: `start_planning`, `continue_planning`, `start_workflow`, `continue_workflow`, `stop_workflow`, `write_chapter`, `edit_chapter`, `run_continuity_check`, `answer_agent_question`
- `src/ABook.Api/Controllers/` — `BooksController`, `ChaptersController`, `MessagesController`, `ConfigurationController`, `AgentController`, `AuthController`, `UsersController`, `ModelsController`, `PresetsController`, `ExportController` (thin wrapper over `BookExportService`)
- `src/ABook.Api/Controllers/ControllerExtensions.cs` — Shared static `RequireBookOwnershipAsync(this, bookId, repo)` helper; replaces the duplicated `CheckOwnershipAsync` that was defined identically on StoryBibleController, CharactersController, ChaptersController, and PlotThreadsController
- `src/ABook.Api/Controllers/PublicController.cs` — `[AllowAnonymous]` controller at `api/public`; config/genres/books-list/book-detail/export endpoints for the public library, gated by `PublicModeOptions`
- `src/ABook.Api/Services/PublicModeOptions.cs` — `record PublicModeOptions(bool IsEnabled)`, singleton read from `"PublicMode"` config key
- `src/ABook.Api/Services/BookExportService.cs` — Scoped service containing all HTML/FB2/EPUB/Metadata export generation logic (`ExportAsync`, `SafeFilename`, markdown converters, transliteration map); used by both `ExportController` and `PublicController`
- `src/ABook.Api/Hubs/BookHub.cs` — SignalR hub
- `src/ABook.Core/Models/AgentRun.cs` — Persisted agent run entity (Id GUID, BookId, RunType, Status, CurrentRole, ChapterId, PendingMessageId, WorkflowContext, timestamps)
- `src/ABook.Api/HostedServices/RunRecoveryService.cs` — Startup `BackgroundService`: rehydrates `WaitingForInput` runs (creates fresh TCS) and orphans stale `Running` runs
- `src/ABook.Api/Program.cs` — App configuration (cookie auth, EF Core, SignalR, pgvector, SK)
- `src/ABook.Api/appsettings.json` / `src/ABook.Api/appsettings.Local.example.json` — Includes `AgentSettings.MaxConcurrentRuns` (global concurrent run cap)
- `src/abook-ui/src/layouts/BookListLayout.tsx` — Shared layout for Dashboard and global pages (Settings, Presets, AdminUsers); fetches books for sidebar; passes `{ books, setBooks }` via Outlet context; "New Book" → `/?new=1`
- `src/abook-ui/src/layouts/BookLayout.tsx` — Shared layout for all book sub-pages at `/books/:bookId`; wraps `BookContextProvider`; renders `BookSidebar` (with chapter list) + `<Outlet />`; redirects index → `overview`
- `src/abook-ui/src/contexts/BookContext.tsx` — Centralized book state provider (all book/chapter/planning/agent state); single SignalR registration via `useBookHub`; navigates to `/books/:bookId/chat` on question/error events; exported via `useBookContext()` hook
- `src/abook-ui/src/pages/book/Overview.tsx` — Book overview + inline edit (title, genre, target chapters, premise)
- `src/abook-ui/src/pages/book/StoryBible.tsx` — Story Bible fields + phase action bar + live streaming preview
- `src/abook-ui/src/pages/book/Characters.tsx` — Character card CRUD + phase action bar + live streaming preview
- `src/abook-ui/src/pages/book/PlotThreads.tsx` — Plot Thread card CRUD + phase action bar + live streaming preview
- `src/abook-ui/src/pages/book/ChapterView.tsx` — Chapter content view (markdown) + POV/foreshadowing/payoff meta + inline chapter edit
- `src/abook-ui/src/pages/book/ChatPage.tsx` — Agent chat panel (messages, pending question/answer, clear)
- `src/abook-ui/src/pages/book/StatePage.tsx` — Current workflow state / steps log
- `src/abook-ui/src/pages/book/TokenStatsPage.tsx` — Token usage statistics panel
- `src/abook-ui/src/pages/Presets.tsx` — Dedicated page for credential preset CRUD (`/presets` route). Lists all visible presets (user-owned + global), with edit/delete actions. Global presets show a "global" badge and cannot be deleted.
- `src/abook-ui/src/pages/GlobalSettings.tsx` — Global settings page (Profile/display name, MCP Access, LLM config user-default scope, Ollama model pull). Route: `/settings`.
- `src/abook-ui/src/pages/book/BookSettings.tsx` — Book-level settings page (LLM config book-scoped, language, agent system prompt overrides). Route: `/books/:bookId/settings`.
- `src/abook-ui/src/layouts/LibraryLayout.tsx` — Layout wrapper for all `/library/*` routes; renders the standard `Sidebar` (with "My Books" if logged in, "Library" nav, chapter list with circular number badges, download buttons); loads book data when a `bookId` param is present; passes `{ book, bookLoading }` via outlet context to child pages
- `src/abook-ui/src/pages/Library.tsx` — Public library listing page (`/library` index route inside `LibraryLayout`). Paginated/filterable list; login prompt when non-public mode and unauthenticated.
- `src/abook-ui/src/pages/PublicBookReader.tsx` — Two exports: `PublicBookIndex` (redirects `/library/:bookId` to first chapter); default `PublicBookReader` (chapter content at `/library/:bookId/chapters/:chapterId`). Both use outlet context from `LibraryLayout` for book data.
- `src/abook-ui/src/utils/streamParsers.ts` — Pure stream parser functions: `parsePlanningStream`, `parseStoryBibleStream`, `parseCharactersStream`, `parsePlotThreadsStream`
- `src/abook-ui/src/utils/chapterStatus.ts` — `CHAPTER_STATUS_COLOR` constant and `chapterStatusColor(status)` function; shared by `BookLayout`, `ChapterView`, `Chapters`
- `src/abook-ui/src/config/providers.ts` — Shared LLM provider constants: `PROVIDERS`, `DEFAULT_ENDPOINTS`, `MODEL_LIST_PROVIDERS`, `API_KEY_REQUIRED_PROVIDERS`, `PROXY_REQUIRED_PROVIDERS`, `INITIAL_LLM_CONFIG`; used by `GlobalSettings`, `BookSettings`, `Presets`
- `src/abook-ui/src/components/PhaseActionBar.tsx` — Reusable phase action bar (Complete/Reopen/Clear buttons); calls `useBookContext()` internally; used by `StoryBible`, `Characters`, `PlotThreads`, `Chapters`
- `src/abook-ui/src/hooks/useRestoreStream.ts` — `useRestoreStream(bookId, isRunning, currentBuffer, agentRole, chapterId, onRestore)` hook; fetches stream buffer from server on hard-refresh if agent is running; used by all planning pages and `ChapterView`
- `src/abook-ui/src/hooks/useBookHub.ts`, `useAuth.tsx`
- `src/abook-ui/src/hooks/useNotifications.ts` — browser Notification API wrapper; opt-in preference in localStorage; `notify()` fires only when tab is hidden
- `src/abook-ui/src/App.tsx` — Two-layout routing: `BookListLayout` for `/`, `/settings`, `/presets`, `/admin/users`; `BookLayout` for `/books/:bookId/*` sub-routes (param is `bookId`); `LibraryLayout` for `/library/*` routes (index=Library list, `:bookId`=redirect to first chapter, `:bookId/chapters/:chapterId`=chapter view); `/login` outside auth guard; unauthenticated users redirected to `/library` instead of `/login`; mounts `<PwaUpdatePrompt />`
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

## Current Stage

**Alpha.** The app is functional end-to-end (planning, writing, editing, continuity checking) but still in active development. UI polish, edge cases, and documentation consistency are ongoing work. The sidebar badge displays `ALPHA` to match the current stage.

## Decisions

- **.NET 10** (latest; Docker images `mcr.microsoft.com/dotnet/sdk:10.0` + `aspnet:10.0`)
- **`.slnx` solution format** — requires .NET 9+ SDK (XML-based, lighter than classic `.sln`)
- **Ollama accessed via host.docker.internal** — not containerized, user manages it externally
- **LLM provider is pluggable** — abstracted behind `ILlmProviderFactory`, configured per-book or globally; supported: Ollama, OpenAI, GoogleAIStudio.
- **MEAI 10.*** (`Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`) is the AI abstraction layer; custom chat clients (`OllamaChatClient`, `OpenAiChatClient`, `GoogleAiStudioChatClient`) wrap provider-specific APIs behind the MEAI `IChatClient` interface
- **Agents use function calling** for the "ask question" tool — agent invokes a `AskUser` function which triggers the pause mechanism (implemented via MEAI tool definitions, not Semantic Kernel)
- **Agent runs are fire-and-forget** — `AgentController` returns 202 immediately; `AgentRunStateService` singleton tracks state; duplicate run returns 409
- **Global agent run concurrency cap (env-based)** — `AgentRunStateService` enforces a process-wide max for simultaneous runs (`Running` + `WaitingForInput`) across all books/users. Configure via `AgentSettings__MaxConcurrentRuns` (`AgentSettings:MaxConcurrentRuns`), default `3`. Value is read at startup in `Program.cs`; changing it requires restart.
- **Cookie authentication** — multi-user support with `IPasswordHasher<AppUser>`; admin role for user management
- **Per-book customization** — `Language` field and per-agent system prompt overrides stored on `Book` entity
- **Ollama model management** — `ModelsController` proxies Ollama's `/api/tags` and streams pull progress via SSE (replaced the old separate `OllamaController` file)
- **Markdown only** for book output — no DOCX/PDF export (can be added later)
- **pgvector for vector storage** — embeddings stored directly in PostgreSQL via the `vector` column type; no separate Docker service needed; `Pgvector.EntityFrameworkCore 0.*` package; dimension-free `vector` type to support any embedding model
- **Book continuation lineage**: `Books.BaseBookId` stores direct parent; parent-of-parent traversal is resolved at query time (cycle-guarded) so chains like A → B → C are supported without a closure table
- **Create-from-base behavior** (`POST /api/books` with `baseBookId`): copies language, human-assisted flag, target chapter count, all 7 system prompts, and book-scoped LLM config from base book to the new book; source ownership enforced
- **Ancestry-aware RAG retrieval**: `IVectorStoreService.SearchAsync` accepts `scopeBookIds`; `PgvectorVectorStoreService` filters `ChapterEmbeddings` via `BookId = ANY(array)` so descendant books can retrieve chunks from all ancestors
- **Ancestor planning context**: StoryBible/Characters/PlotThreads/ChapterOutlines generation injects a compact reference block built from ancestor books' Story Bible + active Characters (with Personality + Arc fields) + active Plot Threads + chapter synopses (all chapters for direct parent, last 5 for older ancestors) via `IBookRepository.BuildAncestorPlanningReferenceAsync`
- **Continuation-aware QuestionAgent**: `GatherQuestionsAsync` system prompt instructs the LLM to skip world-building questions already established by the ancestor context; focuses on continuation-specific decisions only
- **Checker → mechanical patch apply**: `ContinuityCheckerAgent.CheckAsync` accepts optional `humanNotes` param appended to the user message; checker prompts categorize issues as `continuity`, `grammar`, `repetition`, or `style` with verbatim `originalText` (context-rich, min ~20 chars), `replacementText`, and an optional 1-indexed `position` line-number hint. `EditorAgent.EditAsync` dispatches to `ApplyPatchesAsync` when checker issues exist: patches located via IndexOf-first matching with whitespace normalization (`CRLF→LF`, trailing-trim), applied end-first to preserve offsets, position used as a disambiguator only for duplicate matches. `EditWithLlmAsync` handles manual edits / MCP tools without checker input. Feedback message grouped by issue type shows original → replacement + description per fix; unapplied patches reported factually with skip reason.
- **`stopStreamingAt` in `StreamResponseAsync`**: optional `Regex? stopStreamingAt = null` parameter; when set, streaming to SignalR halts once the accumulated buffer matches the regex (but the LLM call continues to completion for the full text); used by `EditorAgent` creative path to stop streaming before `## Editorial Notes` heading
- **All LLM calls are streaming**: `GetCompletionAsync` deleted from AgentBase; every agent (Question, ContinuityChecker PreWriteCheck, ContinuityChecker Check, Editor, Writer, Planner) now uses `StreamResponseAsync`. JSON outputs for checker/planner are parsed from the fully-accumulated response after streaming completes — no tokens leak to SignalR that shouldn't be streamed
- **Q&A preview message**: QuestionAgent posts a single overview AgentMessage (Planner role, Question type) listing ALL upcoming clarifying questions numbered before iterating through them one-by-one via `AskUserAndWaitAsync`. User sees the full question set upfront in the chat panel; individual items still appear for answer/skip
- **Non-streaming PreWriteCheck removed**: previously used `GetCompletionAsync` to avoid leaking tokens to chapter view — now uses streaming like everything else (checker output is structured JSON, not prose)
- **Duplicate-rendering fix in planning pages**: Characters.tsx wraps `<div className="book-list">` in `{!charactersStream && ...}`; PlotThreads.tsx does the same with `{!plotThreadsStream && ...}`; Chapters.tsx hides the chapter list when `plannerBuffer && runStatus?.role === 'ChaptersAgent'` using `&&` instead of ternary else
- **Stream buffer restore fix in ChapterView**: `ChapterView.tsx` computes `activeRole = runStatus?.chapterId === chapter?.id ? runStatus?.role : undefined` and passes it to `useRestoreStream`; `AgentRunStateService.GetStreamBufferContent` with `agentRole=null` falls back to scanning all buffer entries for the given book+chapter to find the first non-empty one
- **`IVectorStoreService` registered as Scoped** — `PgvectorVectorStoreService` injects `AppDbContext` directly; simpler than singleton+factory pattern used with Qdrant
- **`UseVector()` on EF options builder** — `options.UseNpgsql(cs, o => o.UseVector())` in `Program.cs` with `using Pgvector.EntityFrameworkCore;`; requires `Pgvector.EntityFrameworkCore` referenced directly in both `ABook.Infrastructure` and `ABook.Api`
- **`pgvector/pgvector:pg16` Docker image** — replaces `postgres:16-alpine`; identical behaviour but with the pgvector extension pre-installed; only one service in compose (Qdrant removed)
- **React UI is static files** — built in Dockerfile, served from `wwwroot/` by ASP.NET Core, NOT a separate Docker service
- **`IBookNotifier` interface** (`SignalRBookNotifier` implementation) decouples agent/orchestrator code from direct SignalR hub dependency: `LlmConfiguration` has nullable `UserId`; lookup chain is book-specific → user-default → global. `ConfigurationController` automatically sets `UserId` from cookie claims when saving a global (non-book) config. Agents resolve config via `GetKernelAsync` which passes `book.UserId`.
- **Default system prompts API**: `GET /api/books/{id}/default-prompts` returns pre-interpolated default prompts. Returns 7 keys: `storyBibleSystemPrompt`, `charactersSystemPrompt`, `plotThreadsSystemPrompt`, `chapterOutlinesSystemPrompt`, `writerSystemPrompt`, `editorSystemPrompt`, `continuityCheckerSystemPrompt`. Settings page shows these as placeholders and has a "Load Defaults" button that pre-fills empty textarea fields.
- **Migration**: `AddPlanningPhaseStatus` adds `StoryBibleStatus`, `CharactersStatus`, `PlotThreadsStatus`, `ChaptersStatus` columns. `AddPlanningPhasePrompts` renames `PlannerSystemPrompt` → `StoryBibleSystemPrompt` and adds `CharactersSystemPrompt`, `PlotThreadsSystemPrompt`, `ChapterOutlinesSystemPrompt`.
- **pgvector cleanup**: `IVectorStoreService.DeleteCollectionAsync` deletes all embeddings for the book; called by `BooksController.Delete` (non-fatal). `ChaptersController.Update` calls `DeleteChapterChunksAsync` when content is cleared to empty.
- **`LlmProvider.LMStudio removed entirely`:** enum value deleted from `Enums.cs`. The factory and all strategies no longer reference it. (Note: this means EF Core will fail on old DB rows with provider=4; migration path requires updating those rows to OpenAI before upgrading.) Users should switch to OpenAI provider with endpoint `http://host.docker.internal:1234/v1`.
- **LlmProvider.GoogleAIStudio (int=5)**: uses a custom `GoogleAiStudioChatClient` that calls Gemini's streaming REST API (`/v1beta/models/{model}:streamGenerateContent?key={apiKey}`) directly, mapping SSE chunks to MEAI `ChatResponseUpdate`. Reasoning content from the `thinking` field is surfaced via `AdditionalProperties["ReasoningContent"]`. Embeddings use Google's OpenAI-compatible endpoint (`https://generativelanguage.googleapis.com/v1beta/openai`) via the same `OpenAIClient` pattern. API key is required and mandatory. Default model suggestions: `gemini-2.0-flash`, `gemini-2.5-pro` etc. Default embedding model: `text-embedding-004`.
- **LlmProvider.OpenAI endpoint support**: when `Endpoint` is non-empty, uses the URI-based `OpenAIChatCompletionService` overload so any OpenAI-compatible API (Groq, Together, etc.) works. When `Endpoint` is empty, uses the standard apiKey-only overload (real OpenAI API). Same logic applies to `CreateEmbeddingGeneration` and `CreateKernel`.
- **LlmProvider.Anthropic removed**: The SK Anthropic connector is not available for .NET 10. Users who need Claude-style models can use OpenAI provider with an OpenAI-compatible proxy endpoint (e.g. LiteLLM at `http://localhost:4000`). Strategy class deleted from source.
- **Azure OpenAI removed**: Not supported in the codebase (`LlmProvider` enum has only Ollama, OpenAI, GoogleAIStudio). Azure endpoints are functionally identical to the OpenAI provider — users should use OpenAI with a custom endpoint. Removed from README docs.
- **LLM debug logging**: set env var `LLM_DEBUG_LOGGING=true` to print full chat history (all messages with roles) before each LLM call and the complete response after to the application log at `Information` level. Implemented as a static flag in `AgentBase` checked in `StreamResponseAsync`.
- **`GET /api/models`** (renamed from `/api/ollama/models`): accepts `?provider=`, `?endpoint=`, `?apiKey=` query params. Routes: `GoogleAIStudio` → calls `https://generativelanguage.googleapis.com/v1beta/models?key={apiKey}` (returns only `gemini-*` models); `OpenAI` → calls `{endpoint}/models` or `https://api.openai.com/v1/models` with `Authorization: Bearer {apiKey}`; default (Ollama) → calls `/api/tags`. Pull endpoint stays at `/api/ollama/pull` (Ollama-specific).
- **Model list in Settings**: fetched dynamically for Ollama, OpenAI, and GoogleAIStudio. For OpenAI and GoogleAIStudio the API key is sent to the backend which calls the provider. Model/embedding fields use a datalist combobox (free-form input + suggestions from fetched list) instead of a select dropdown — custom model names no longer need a separate toggle. Switching provider resets endpoint to the provider's default (only if the current endpoint matches the previous provider's default).
- **ContinuityCheckerAgent uses RAG**: runs three targeted queries (character descriptions, timeline, locations) against pgvector before checking continuity; appends retrieved passages to the LLM prompt alongside the chapter synopsis.
- **`StripLeadingChapterHeading` moved to `AgentBase`** (protected): both `WriterAgent` and `EditorAgent` strip LLM-added chapter headings from prose before saving. Now handles consecutive heading lines, bold-formatted headings, and ordinal word variants ("Chapter One", "Chapter Two" etc.).
- **`InterpolateSystemPrompt` in `AgentBase`** (protected static): substitutes `PromptPlaceholders` tokens in user-supplied or default system prompts. Signature: `InterpolateSystemPrompt(string prompt, Book book, StoryBible? bible = null)`. Book-level tokens always resolved; Story Bible tokens (`{SETTING}`, `{THEMES}`, `{TONE}`, `{WORLD_RULES}`) resolved only when `bible` is passed.
- **`GetPreviousChapterEndingAsync` in `AgentBase`** (protected): returns the last 3 paragraphs of the immediately preceding chapter. `WriterAgent` includes this in the system prompt so prose is narratively continuous even without RAG context.
- **`EditorAgent` notes split**: uses a regex to find any `## Editorial Notes` / `## Editor's Notes` / `## Feedback` etc. heading (case-insensitive) instead of a hard string compare; more resilient to LLM phrasing variation.
- **Settings UI placeholder hint**: the "Custom Agent System Prompts" section now shows a reference block listing all supported template tokens and reminds users that the Editor prompt must end with `## Editorial Notes`.
- **Library sidebar layout**: Library and book reader pages now use the same `app-layout` + `Sidebar` as the rest of the app. Sidebar shows "← My Books" (logged in), "Library" nav, book title + chapter list (circular number badges when viewing a book), download buttons at the bottom. Unauthenticated users get a "Log In" button at the bottom instead of Sign Out.
- **Library chapter routes**: Each chapter is its own URL (`/library/:bookId/chapters/:chapterId`). Navigating to `/library/:bookId` auto-redirects to the first chapter via `PublicBookIndex`. Unauthenticated redirect is now `/library` (not `/login`).
- **`lib-ch-circle` CSS**: circular badge in `.s-icon` for library sidebar chapter buttons; accent-colored, 1.5rem diameter. the content area shows 4 tabs — Overview (book premise/genre/edit), Story Bible (world-building fields), Characters (role-colored cards with full CRUD), Plot Threads (status-colored cards with type/chapter-number info + CRUD). Chapter view includes a meta bar showing POV character, foreshadowing notes, and payoff notes from the enriched outline. When provided (per-chapter workflow), separates chapters into "preceding facts" vs "chapter under review" and instructs the LLM to report only issues *introduced by* that chapter, ignoring pre-existing issues between earlier chapters. No-id calls (final check, standalone button) retain full cross-manuscript review behaviour. `AgentOrchestrator` per-chapter call sites now pass `chapter.Id`; final checks pass null.
- **Token statistics**: `AgentBase.StreamResponseAsync` now accepts `AgentRole role` (required param before `CancellationToken`). After each LLM streaming call, emits approximate token counts (chars/4) via `IBookNotifier.NotifyTokenStatsAsync` → SignalR `TokenStats` event. Also **persists** a `TokenUsageRecord` row (BookId, ChapterId, AgentRole, PromptTokens, CompletionTokens) via `IBookRepository.AddTokenUsageAsync`. `GET /api/books/{id}/token-usage` returns all persisted records. UI loads historical stats on page load and refreshes them on each `TokenStats` SignalR event; shown as a collapsible `<details>` panel at the bottom of the chat sidebar. Total accumulated tokens shown.
- **Orchestrator simplified with design patterns**: `ExecuteAgentRunAsync` private helper centralises TryStartRun guard + cancellation/error handling + status transitions (Completed/Failed/Cancelled) for all 7 public run methods. `ProcessChapterAsync` private helper deduplicates the PreCheck→Write→Check→Edit→Check sequence used by both `StartWorkflowAsync` and `ContinueWorkflowAsync`. `AgentRunStateService` now requires `IServiceScopeFactory` + `ILogger` (DI still registers as singleton — framework resolves via constructor).
- **Durable run persistence (restart resilience)**: `AgentRun` entity persists run lifecycle to DB (`AgentRuns` table). `AgentRunPersistStatus` enum: `Running | WaitingForInput | Completed | Failed | Cancelled | Orphaned`. `ExecuteAgentRunAsync` calls `PersistRunStartAsync` on entry and `PersistRunFinishedAsync` on exit. `AskUserAndWaitAsync` calls `PersistRunPausedAsync` (saves `WaitingForInput` + `PendingMessageId`) before blocking and `PersistRunResumedAsync` after the answer arrives. `ResumeWithAnswerAsync` is idempotent (`IsResolved` guard). `RunRecoveryService` (hosted service, 3 s startup delay): scans DB for non-terminal runs; `WaitingForInput` → `RehydrateWaitingRun` creates fresh TCS + re-notifies UI; `Running` → mark `Orphaned`, emit SystemNote AgentMessage, reset in-memory status so user can restart. EF migration: `AddAgentRuns`.
- **Chapter inline edit**: "✎ Edit" button appears on chapter header when not running. Opens an inline form to edit title and outline; saves via `PUT /api/books/{id}/chapters/{chapterId}`.
- **Book inline edit**: "✎ Edit" button on book overview. Opens an inline form to edit title, genre, target chapters, premise/plot; saves via `PUT /api/books/{id}`.
- **Add chapter manually**: "+ Chapter" button at the bottom of the sidebar chapter list. Inline form collects title and outline; auto-assigns the next chapter number; saves via `POST /api/books/{id}/chapters` and immediately selects the new chapter.
- **Default LLM config from env vars**: on startup, `Program.cs` reads `LlmDefaults` config section and upserts the global `LlmConfiguration` record (BookId=null, UserId=null). Env var names follow .NET double-underscore convention: `LlmDefaults__Provider`, `LlmDefaults__ModelName`, `LlmDefaults__Endpoint`, `LlmDefaults__ApiKey`, `LlmDefaults__EmbeddingModelName`. Defaults are pre-populated in `appsettings.json` (Provider=Ollama, ModelName=llama3, Endpoint=http://host.docker.internal:11434). Commented examples in `docker-compose.yml`.
the- **Agent error logging & UI notifications**: `IBookNotifier` gains `NotifyAgentErrorAsync(bookId, agentRole, errorMessage)` → `AgentError` SignalR event. `AgentBase` gains `ReportErrorAsync(bookId, chapterId, role, message)` (protected) and `AgentOrchestrator` gains `ReportAgentErrorAsync(bookId, role, chapterId, message)` (private): both persist a `SystemNote` `AgentMessage` to the DB **and** fire the SignalR event, so errors appear in the chat panel alongside all other agent messages (rendered with `msg-systemnote` CSS class, `❌` prefix). `AgentError` event on the frontend refreshes the message list and switches to the chat panel — the top-of-screen error banner has been removed. All `Exception` catch blocks in `AgentOrchestrator` use this helper. `AgentBase.StreamResponseAsync` still logs errors and warns on empty/short responses.
- **Unexpected cancellation detection**: In `StartWorkflowAsync` and `ContinueWorkflowAsync`, `OperationCanceledException` is split into two catches using an exception filter: `when (ct.IsCancellationRequested)` = user clicked Stop → "Workflow stopped." with no error message; `else` = LLM timeout / connection reset → treated as a real error with `ReportAgentErrorAsync` and "Workflow failed (request cancelled)." progress message.
- **`RunInBackground` crash recovery**: In `AgentController.RunInBackground`, unexpected exceptions now log at Error level and force terminal in-memory state (`SetStatus(Failed)`, `ClearStreamBuffers`, `TryRemoveRunId`) so the book is never left stuck on "Running." `AgentRunStateService.TryRemoveRunId(bookId)` was added to expose cleanup of the persisted run-id cache. Nested silent catches in `ReportErrorAsync` (AgentBase) and `ReportAgentErrorAsync` (AgentOrchestrator) now log at Error level instead, so error-surfacing persistence failures are visible.
- **ProcessChapterAsync null-chapter fallback**: When `_repo.GetChapterAsync(bookId, chapter.Id)` returns null (should not happen normally), the orchestrator now emits a Warning log before falling back to the cached chapter parameter, making stale-state bugs detectable in production.
- **ResumeWithAnswerAsync silence diagnostics**: Submitting an answer for a null or already-resolved message now logs at Warning level with context (`messageId`, `bookId`), so race conditions where answers silently drop are debuggable.
- **Strategy pattern for LlmProviderFactory**: `ILlmProviderStrategy` interface; each provider has its own class in `src/ABook.Infrastructure/Llm/Strategies/`. `LlmProviderFactory` dispatches via a `static readonly Dictionary<LlmProvider, ILlmProviderStrategy>`. `OpenAIProviderHelpers.CreateOpenAIClient` is a shared static helper used by OpenAI and GoogleAIStudio strategies. Adding a future provider only requires a new strategy class + registration in the dictionary — no changes to the factory or other providers.
- **Two idempotent agent flows**: `StartPlanningAsync` and `StartWorkflowAsync` both read book phase statuses and skip completed phases (same logic as former `ContinuePlanningAsync`). `StartWorkflowAsync` additionally skips `Done` chapters and passes `resumeFromStatus` to `ProcessChapterAsync`. `ContinuePlanningAsync` and `ContinueWorkflowAsync` remain as MCP-tool aliases. The UI exposes only two buttons: **Write Book** and **Plan Book** — both are always idempotent continuations.
- **`[Authorize]` on all API controllers**: All 9 write-facing controllers now require authentication. Content controllers (`CharactersController`, `PlotThreadsController`, `StoryBibleController`, `ChaptersController`, `PlanningPhasesController`, `ConfigurationController`, `MessagesController`, `BooksController`, `AgentController`) require `[Authorize]`. Book ownership is verified via shared static helper `ControllerExtensions.RequireBookOwnershipAsync(this, bookId, repo)` — the identical private `CheckOwnershipAsync` method that was duplicated on 4 controllers has been replaced by this single extension method.
- **`GetAllAsync` null-userId guard**: `BookRepository.GetAllAsync(null)` now returns `Enumerable.Empty<Book>()` to prevent unauthenticated callers from listing all books.
- **`PostAnswer` message ownership check**: `MessagesController.PostAnswer` verifies the message's `BookId` matches the route `bookId` before calling `ResumeWithAnswerAsync`.
- **`EditorAgent` re-indexes after edit**: After `AddChapterVersionAsync`, `EditorAgent` calls `IndexChapterAsync` so subsequent RAG queries see the edited prose (not the Writer's version).
- **Write/Edit/Continuity CTS**: `AgentController.Write`, `Edit`, and `Continuity` now call `_runState.CreateRunCts(bookId)` before the background task, so the Stop button works for per-chapter operations.
- **Atomic `ActivateChapterVersionAsync`**: Wrapped in an explicit EF Core transaction (`BeginTransactionAsync`/`CommitAsync`/`RollbackAsync`) to prevent partial state if a mid-operation failure occurs.
- **`ReplaceChaptersAsync` in `BookRepository`**: atomically deletes all existing chapters for a book and inserts the new set inside a single PostgreSQL transaction. Called by `PlannerAgent.RunAsync` instead of separate `DeleteChaptersAsync` + per-chapter `AddChapterAsync` loop — prevents partial-save state if a chapter insert fails mid-loop. `IBookRepository` interface gains the method.
- **`plannerBuffer` cleared after `refreshBook` resolves**: in `BookContext.tsx`, the `WorkflowProgress(isComplete=true)` handler no longer calls `clearStreams()` before `refreshBook()`. Instead, it clears the other live streams immediately, then calls `refreshBook()` and only clears `plannerBuffer` + sets `setRunStatus(null)` inside `.then()` (and `.catch()` for error resilience). This ensures the chapter streaming preview stays visible while chapters load from the DB, preventing a flash of the "No chapters yet" empty state between `clearStreams()` and data arriving.
- **`BookContext` AbortController cleanup**: Initial `useEffect` creates an `AbortController` and passes `signal` to all initial fetch calls (`getMessages`, `getTokenUsage`, `getStoryBible`, `getCharacters`, `getPlotThreads`). Cleanup function calls `controller.abort()`.
- **`getMessages`, `getTokenUsage`, `getStoryBible`, `getCharacters`, `getPlotThreads`** in `api.ts` now accept an optional `AbortSignal` parameter.
- **Settings save error display**: `GlobalSettings` and `BookSettings` now catch save failures, log them with `console.error`, and show an inline error message to the user near the save button.
- **`useRestoreStream` dependency array fixed**: Changed from `[bookId, chapterId]` to `[bookId, chapterId, isRunning, agentRole]`. `onRestore` is stored in a ref to avoid stale closure without adding to deps.
- **Characters/PlotThreads save loading state**: Both Add and Edit forms in `Characters.tsx` and `PlotThreads.tsx` now use a `saving` boolean state to disable the Save button while the request is in flight.
- **EF migration `AddMissingIndexes`**: Adds non-unique indexes on `AgentMessages.BookId`, `AgentMessages.ChapterId`, and `TokenUsageRecords.BookId` to improve query performance for the most frequent read patterns.
- **N+1 fix in `StartWorkflowAsync` / `ContinueWorkflowAsync`**: Both methods now call `GetChaptersAsync(bookId)` once before the loop (building a `Dictionary<int, Chapter>` or `List<Chapter>`) instead of a separate `GetChapterAsync` call per chapter.
- **Stream buffer cleanup on orphaned runs**: `RunRecoveryService` now calls `_runState.ClearStreamBuffers(run.BookId)` after orphaning a stale `Running` run to prevent memory leaks.
- **Ollama embedding fix**: `OllamaTextEmbeddingGenerationService` ctor throws `InvalidCastException` in SK 1.74.0-alpha because its internal `EmbeddingGeneratorEmbeddingGenerationService<string, float>` no longer implements `ITextEmbeddingGenerationService`. Fix: `LlmProviderFactory.CreateEmbeddingGeneration` now uses `OpenAITextEmbeddingGenerationService` with an `OpenAIClient` pointed at Ollama's OpenAI-compatible `/v1` endpoint (supported since Ollama 0.4.x), bypassing the broken Ollama-specific service entirely.
- **`AgentController.Plan`** now calls `CreateRunCts` before starting the planner in background, so the Stop button works during a Plan Only run (same as `workflow/start`).
- **"Plan Only" mode**: `POST /api/books/{id}/agent/plan` runs the full 4-phase Planner (Story Bible → Characters → Plot Threads → Chapter Outlines) and stops, leaving the book in a state where the user can edit outlines/characters/plot threads and then click "Continue" to write chapters. Button appears as 🗗 Plan Only alongside Write Book.
- **Pending question restored on page refresh**: `ChatPage.tsx` loads messages and agent status together via `BookContext`; if status is `WaitingForInput`, the latest unresolved `Question` message is restored as `pendingQuestion` so the user can answer after refreshing the page.
- **Token stats panel scrollable**: `.token-stats-list` CSS now has `max-height: 220px; overflow-y: auto` alongside the existing `overflow-x: auto`, making it scrollable in both directions when there are many entries.
- **Export filename transliteration**: `SafeFilename` in `BookExportService.cs` (moved from `ExportController`) uses a `TranslitMap` dictionary for Cyrillic + Greek → Latin + NFD normalization to strip Latin diacritics before stripping non-ASCII. Ensures filenames like `vojna-i-mir.html` instead of `.html` for non-ASCII book titles.
- **App versioning**: `VERSION` file at repo root (plain text `MAJOR.MINOR.PATCH`) is the single source of truth. `vite.config.ts` reads it at build time and injects it as `__APP_VERSION__` global via Vite `define`. `src/abook-ui/src/vite-env.d.ts` declares the global type. GitHub Actions reads it via `echo "value=$(cat VERSION)" >> $GITHUB_OUTPUT` and uses it to tag Docker images (`type=raw,value=${{ steps.version.outputs.value }}`) and set `org.opencontainers.image.version` label. `RELEASE_NOTES.md` at repo root tracks per-version changes. Copilot instructions enforce patch-bump-per-commit discipline; minor/major bumps only when explicitly instructed.

- **PWA support**: `vite-plugin-pwa 1.x` generates `sw.js` (Workbox `generateSW` mode) and `manifest.webmanifest` into `wwwroot/` at build time; ASP.NET serves them as static files automatically. Manifest: name "ABook", `display: standalone`, icons `pwa-192x192.png` (192 px) + `pwa-512x512.png` (512 px, `any maskable`). `registerType: 'prompt'` — user-triggered update via `PwaUpdatePrompt` banner (avoids surprise mid-session reloads). Workbox precaches all `*.{js,css,html,ico,png,svg,woff,woff2}` assets; `navigateFallback: index.html` so React Router works offline when shell is cached. `NetworkOnly` runtime rule + `navigateFallbackDenylist` for `/api/*`, `/hubs/*`, `/mcp` — SW never intercepts live API or WebSocket traffic. `tsconfig.app.json` includes `"vite-plugin-pwa/client"` in `types` for `virtual:pwa-register/react` type support.
- **Browser notifications (Option A — in-tab)**: `useNotifications` hook (`src/abook-ui/src/hooks/useNotifications.ts`) wraps the browser `Notification` API; persists opt-in preference in `localStorage` (`abook:notifications:enabled`); `notify()` short-circuits unless: supported + enabled + `Notification.permission === 'granted'` + `document.visibilityState === 'hidden'`. `BookContext` uses a `notifyRef` (always-current ref) to call `notify` inside SignalR handlers for `AgentQuestion`, `AgentError`, and `WorkflowProgress(isComplete=true)` without adding it to effect deps. Opt-in toggle in Global Settings with live permission-state feedback. No backend changes needed.
- **HTML export no max-width**: `main` element in the generated HTML (book) and metadata HTML has no `max-width` or `margin: 0 auto` — content fills the full viewport width.
- **Chapter list page no inline content**: `Chapters.tsx` never renders chapter prose (streaming or persisted) inside cards. Cards show only status badge, POV character, outline, and foreshadowing/payoff notes. `ReactMarkdown` import removed.
- **All export logic is server-side, centralized in `BookExportService`**: HTML, FB2, EPUB, and metadata are all generated by the scoped `BookExportService` (`ExportAsync(bookId, format)`). `ExportController` (authenticated, `/api/books/{id}/export`) and `PublicController` (anonymous, `/api/public/books/{id}/export`, html/fb2/epub only) both delegate to it — no duplicated generation logic. UI uses `window.location.href = /api/books/{id}/export?format=...` (or the `/api/public/...` equivalent in the reader) for all formats.
- **Per-item versioning for CharacterCard and PlotThread**: `CharacterCardVersion` and `PlotThreadVersion` entities (in `ABook.Core/Models/`) store per-item version history. Each agent save (`CharactersAgent`, `PlotThreadsAgent`) and each UI edit (`CharactersController.Update`, `PlotThreadsController.Update`) creates a version row. `VersionNumber` auto-increments via `MAX(VersionNumber)+1` query per parent item. EF migration: `AddPerItemVersioning`.
- **`IsArchived bool` on `CharacterCard` and `PlotThread`**: soft-archive pattern. `GetCharacterCardsAsync`/`GetPlotThreadsAsync` filter `!IsArchived`; `GetAllCharacterCardsAsync`/`GetAllPlotThreadsAsync` return everything. `BookContext` loads with `includeArchived=true` so archived items are available in UI state.
- **Chapter archive/restore UI**: `Chapter` TypeScript interface gains `isArchived?: boolean`. `Chapters.tsx` splits chapters into active/archived, shows a collapsible "Show archived" section (identical pattern to Characters/PlotThreads) with ♻ Restore and 📜 History buttons per archived chapter. A 🗄 button on each active chapter card archives it. `ChapterView.tsx` shows an "archived" badge and a ♻ Restore button when the current chapter is archived; the 🗄 archive button appears in the header for non-archived chapters. `BookLayout.tsx` sidebar and download-button condition both filter `!isArchived`. `api.ts` gains `archiveChapter` and `restoreChapter` functions. **This also fixes the Chapters "Clear All" phase button** — previously archived chapters came back via `refreshBook()` because `GetByIdWithDetailsAsync` includes all chapters; now that the TS interface exposes `isArchived`, the filter hides them.
- **History on archived characters/plot threads**: the "Show archived" sections in `Characters.tsx` and `PlotThreads.tsx` now include a 📜 per-item version history button (same inline panel as active items). The shared `itemHistoryCardId` / `itemHistoryId` state is reused.
- **Chapter outline/title versioning**: `ChaptersController.Update` saves a `ChapterVersion` row before applying edits. Chapter view shows 📜 History button listing versions with activate/preview capability (existing behaviour, unchanged).
- **Metadata HTML export**: `GET /api/books/{id}/export?format=metadata` — server-side generation via `BookExportService.GenerateMetadataHtml`. Fetches characters, plot threads, messages, and token usage from the repository. Produces a self-contained themed HTML document (same 6 colour presets, font-size controls) with: Book Information, Chapter Outlines (with POV/foreshadowing/payoff), Story Bible, Characters, Plot Threads, Agent Messages (grouped by chapter), Token Statistics. The "⬇ Download Metadata" sidebar button (`BookLayout.tsx`) uses `window.location.href = /api/books/{id}/export?format=metadata`. Filename: `{transliterated-title}-metadata.html`.
- **Planning phase statuses**: `PlanningPhaseStatus { NotStarted, Complete }` enum on `Book` entity for four phases: `StoryBibleStatus`, `CharactersStatus`, `PlotThreadsStatus`, `ChaptersStatus`. EF migration: `AddPlanningPhaseStatus`.
- **Split Planner LLM calls**: Replaced. Each planning phase now uses **one single upfront Q&A round** before any generation. `GatherInitialQuestionsAsync` asks the LLM for clarifying questions about the book premise (returns JSON array); `AskQuestionsAsync` presents each question one at a time via `AskUserAndWaitAsync`. Continuation runs (`isContinuation=true`) skip Q&A and reload existing Q&A context via `LoadExistingQaContextAsync`.
- **Auto-mark phases complete**: Each planning phase agent (`StoryBibleAgent`, `CharactersAgent`, `PlotThreadsAgent`, `PlannerAgent`) marks its own phase `Complete` on success via `book.*Status = Complete; await Repo.UpdateAsync(book)`. Both "Plan Only" (`StartPlanningAsync`) and "Write Book" (`StartWorkflowAsync`) flows benefit automatically.
- **`PromptPlaceholders` + `DefaultPrompts`** in `AgentPrompts.cs`: all placeholder name constants and all 7 agent default prompt templates live here. Agents resolve the effective prompt as `InterpolateSystemPrompt(book.XxxSystemPrompt.HasValue ? book.XxxSystemPrompt : DefaultPrompts.Xxx, book, bible)`. Supported placeholders: `{TITLE}`, `{GENRE}`, `{PREMISE}`, `{LANGUAGE}`, `{CHAPTER_COUNT}` (book-level); `{SETTING}`, `{THEMES}`, `{TONE}`, `{WORLD_RULES}` (Story Bible level — passed through when bible is available); `{CHAPTER_SYNOPSES}` (cross-chapter spine — passed through when the calling agent supplies the pre-computed synopses string). Settings UI shows all tokens in the placeholder reference block.
- **Anti-repetition system**: Writer, Editor, and Checker all receive targeted cross-chapter context to prevent re-introductions, recycled scene beats, and echoed phrasing. Three mechanisms work together: (1) **Full chapter synopsis spine** — `AgentBase.BuildChapterSynopsesAsync` returns every prior chapter as `N. **Title** — Outline`; injected into the Writer and Editor *user messages* (not system prompt) so it doesn't pollute the system-prompt cache. Never truncated. (2) **3 targeted Writer RAG queries** — replaces the former single generic query: characters in this chapter (topK=4), locations/setting (topK=3), plot threads (topK=3); results labelled and injected into the system context block. (3) **4 targeted Editor RAG queries** — same 3 as Writer plus a 4th for `"repeated descriptions recurring phrases re-introduction"` (topK=4); injected into the user message alongside the synopsis spine. Default Writer/Editor/Checker prompts include explicit anti-repetition rules.
- **`{CHAPTER_SYNOPSES}` placeholder**: resolves to the full synopsis spine when the calling agent passes a pre-computed string to `InterpolateSystemPrompt(prompt, book, bible, chapterSynopses)`.
- **`QuestionAgent`**: dedicated agent class for the upfront Q&A round. `GatherQuestionsAsync` asks the LLM for clarifying questions; `AskQuestionsAsync` presents each one via `AskUserAndWaitAsync`; `LoadExistingContextAsync` rebuilds Q&A context for continuation runs. `AgentOrchestrator.RunPlanningPipelineAsync` uses `_questions.*` methods (not `_planner.*`).
- **`ContinuePlanningAsync`**: `AgentOrchestrator` method reads 4 phase statuses, computes skip flags, calls `RunPlanningPipelineAsync` skipping completed phases. If all 4 complete, emits "All planning phases are already complete." message.
- **`PlanningPhasesController`**: New controller at `api/books/{bookId}/planning-phases`. Three verbs × four phases: `POST /{phase}/complete` (sets status=Complete), `POST /{phase}/reopen` (sets status=NotStarted), `DELETE /{phase}` (deletes data + sets NotStarted). Valid phases: `storybible | characters | plotthreads | chapters`.
- **`AgentController.ContinuePlanning`**: `POST /api/books/{id}/agent/plan/continue` — creates run CTS, launches `ContinuePlanningAsync` in background, returns 202.
- **Book UI routing architecture**: Two-layout system — `BookListLayout` (Dashboard + global pages) and `BookLayout` (book sub-pages at `/books/:bookId/*`). `BookContext` centralizes all book state and single SignalR registration. Sub-pages: `overview`, `story-bible`, `characters`, `plot-threads`, `chapters/:chapterId`, `chat`, `state`, `token-stats`, `settings`. Stream parsers extracted to `src/abook-ui/src/utils/streamParsers.ts`.
- **Per-phase AgentRole values**: `AgentRole` enum has `StoryBibleAgent`, `CharactersAgent`, `PlotThreadsAgent` (in addition to `Planner`, `Writer`, `Editor`, `ContinuityChecker`, `Embedder`). Each planning phase streams tokens with its own role so the UI can route them to the correct tab.
- **`AgentStreaming` SignalR event** carries a 4th argument `agentRole: string` (role of the streaming agent). `SignalRBookNotifier.StreamTokenAsync` sends `(bookId, chapterId, agentRole, token)`. `useBookHub.ts` `StreamHandler` type updated: `(bookId, chapterId, agentRole, token) => void`.
- **Planning stream previews**: Each planning page (Story Bible, Characters, Plot Threads) shows a live `⏳ Generating…` preview while the corresponding agent is streaming. Three progressive parsers in `streamParsers.ts`: `parseStoryBibleStream` (regex-extracts JSON fields), `parseCharactersStream` (extracts complete `{...}` objects), `parsePlotThreadsStream` (same). All parsers are fault-tolerant — they skip malformed items and keep already-parsed ones.
- **Clear Chat button**: appears in the chat panel header when messages exist and agent is not running. Calls `DELETE /api/books/{id}/messages` → `MessagesController.DeleteAll` → `IBookRepository.DeleteMessagesAsync`.
- **Clear Token Stats button**: appears inside the token stats `<summary>` when stats exist and agent is not running. Calls `DELETE /api/books/{id}/token-usage` → `BooksController.DeleteTokenUsage` → `IBookRepository.DeleteTokenUsageAsync`.

- **Agent status correctness**: Fixed a bug where individual agent "Done" signals (e.g. `WriterAgent` completing mid-workflow) caused the UI to incorrectly clear `runStatus` and show buttons as clickable while the orchestrator was still running subsequent steps (Editor, ContinuityChecker, etc.). Fix: `setOnStatus` in `BookContext` no longer clears `runStatus` on "Done"; instead it calls `refreshBook()` to update phase-status dots. The authoritative "run finished" signal is now exclusively `WorkflowProgress(isComplete=true)`. `ExecuteAgentRunAsync` always emits this on success (with a `runType`-based message like "Writing complete." / "Planning complete." / "Workflow complete!"). The duplicate `WorkflowProgress("Workflow complete!", true)` calls at the end of `StartWorkflowAsync` and `ContinueWorkflowAsync` bodies were removed (to avoid double-emission). On page reload, `BookContext` only restores `runStatus` from the API when state is `Running` or `WaitingForInput`; terminal states (Done/Failed/Cancelled) are silently ignored. When a `Running`/`WaitingForInput` event arrives from SignalR, the previous `chapterId` is preserved via `prev?.chapterId`.
- **Fixed check-edit-check sequence**: `ProcessChapterAsync` in `AgentOrchestrator` runs PreCheck → Write → Check → optional human pause (if `book.HumanAssisted`) → Editor (only if issues found) → Done. Single edit pass only — no looping. `EditorAgent.EditAsync` called with `finalizeStatus: false`; status is forced to Done at the end. No final informational check — patches are deterministic, any failures reported in chat panel.
- **Human-assisted pauses during planning**: In `RunPlanningPipelineAsync`, after each non-skipped phase (StoryBible, Characters, PlotThreads, ChapterOutlines), if `book.HumanAssisted`, calls `_questions.AskSingleOptionalAsync(...)`. Non-empty responses are appended to `qaContext` and passed to subsequent phases.
- **`IsOptional` on `AgentMessage`**: controls UI behavior — shows "Skip" button and allows empty answer submit. Set by `AskUserAndWaitAsync(..., isOptional: true)`. Used for human-assisted pauses; regular upfront Q&A remains mandatory.
- **Ctrl+Enter shortcut**: `ChatPage.tsx` textarea `onKeyDown` handler submits the answer form on Ctrl+Enter.
- **"Continuity Checker" renamed to "Checker" in UI only**: `BookLayout.tsx` status label changed; `BookSettings.tsx` agent label changed. `ContinuityChecker` enum value, DB column name `ContinuityCheckerSystemPrompt`, and `AgentRole.ContinuityChecker` are all unchanged.
- **`CheckerResult` record**: in `ABook.Agents/CheckerResult.cs` — `record CheckerResult(bool HasIssues, CheckerIssue[] Issues, string Summary)` where each `CheckerIssue(Type, Description, ProposedFix, OriginalText, ReplacementText, Position)` carries a 1-indexed line-number hint for disambiguation. Issue types: `continuity`, `grammar`, `repetition`, `style`. JSON output parsed via `CheckerResultDto` with optional `position` field; Editor receives the issues array and applies patches mechanically (no LLM call). JSON is extracted via `ExtractJson(responseJson, '{', '}')` before parsing to handle LLM preamble text.
- **Planning agents snapshot before/after generation**: `StoryBibleAgent`, `CharactersAgent`, and `PlotThreadsAgent` each create a snapshot BEFORE deleting/overwriting existing data (reason `"agent-overwrite"`) and AFTER saving the newly generated content (reason `"agent-generated"`). This ensures the initial agent-generated state is visible in history immediately, and prior states are preserved on regeneration.
- **EF migration `AddAssistedGeneration`**: adds `HumanAssisted bool NOT NULL DEFAULT false` to `Books` and `IsOptional bool NOT NULL DEFAULT false` to `AgentMessages`.
- **EF migration `AddSnapshotSourceAndSoftDelete`**: adds `Source varchar(100) DEFAULT 'phase-reset'` to `CharactersSnapshots` and `PlotThreadsSnapshots`; adds `IsDeleted bool DEFAULT false` to `AgentMessages` and `TokenUsageRecords`. Enables soft-delete for both and per-edit snapshots for characters/plot threads.
- **Soft-delete for AgentMessages and TokenUsageRecords**: `DeleteMessagesAsync` and `DeleteTokenUsageAsync` now set `IsDeleted=true` via `ExecuteUpdateAsync` instead of hard-deleting. All reads filter `WHERE IsDeleted = FALSE`. UI "🗑 Clear" buttons renamed to "🗄 Archive".
- **Per-edit snapshots for Characters and PlotThreads**: `CharactersController.Update` and `PlotThreadsController.Update` save a `CharactersSnapshot`/`PlotThreadsSnapshot` row (with `Source="edit"`, `Reason="edit:{name}"`) before applying the update. Phase-level clears continue to use `Source="phase-reset"`. The `Source` field distinguishes edit-time from clear-time snapshots in the history list.
- **Restore endpoints**: `POST /books/{id}/characters/history/{snapshotId}/restore` replaces all current characters with the snapshot's `DataJson`; `POST /books/{id}/plot-threads/history/{snapshotId}/restore` does the same for plot threads; `POST /books/{id}/story-bible/history/{snapshotId}/restore` upserts story bible fields from the snapshot. All three existing `GET history` list + `GET history/{id}` detail endpoints were already implemented.
- **Version history UI**: `ChapterView.tsx` — "📜 History" button in the chapter header opens a list of `ChapterVersionMeta` (versionNumber, createdBy, wordCount, isActive). Clicking a row loads full content via `GET .../versions/{id}` and shows a 800-char preview. "Activate" calls `POST .../versions/{versionId}/activate` and refreshes the chapter in context.
- **Planning phase history UI**: `StoryBible.tsx`, `Characters.tsx`, `PlotThreads.tsx` — "📜 History" button in `PhaseActionBar` (optional `onHistory` prop) opens an inline history panel listing snapshots by date/source. Preview loads full content on demand; "Restore" calls the restore endpoint and refreshes context state. Phase history panels replace the page content while open (back via "✕ Close").
- **`PhaseActionBar` `onHistory` prop**: optional `() => void`; when provided, renders a "📜 History" ghost button between the Complete/Reopen button and the Archive button. Default `clearLabel` changed from `'🗑 Clear'` to `'🗄 Archive'`.
- **`QuestionAgent.AskSingleOptionalAsync`**: thin wrapper around `AskUserAndWaitAsync(..., isOptional: true)` for a single optional question.
- **MCP tools are organized in two tiers**: `UserMcpTools` for user-level operations (profile, LLM config, presets, `generate_book`); `BookMcpTools` / `ContentMcpTools` / `AgentMcpTools` for per-book operations. `generate_book` creates a book and immediately fires `StartWorkflowAsync` in background — single tool for end-to-end book generation.
- **MCP server embedded in `ABook.Api`**: `ModelContextProtocol.AspNetCore 1.2.0` package; `AddMcpServer().WithHttpTransport().WithTools<T>()` registration; mounted at `/mcp` via `app.MapMcp("/mcp").RequireAuthorization(...)`
- **ApiToken auth for MCP**: `ApiTokenAuthenticationHandler` implements `Authorization: Bearer {guid}` scheme registered as `"ApiToken"` alongside cookie scheme. MCP endpoint accepts either scheme. `AppUser.ApiToken` column (nullable string) added via `AddApiToken` migration. Plaintext GUID — acceptable for local dev tool; threat model excludes DB-level attackers.
- **One token per user**: simple UX; regenerate to rotate via `POST /api/auth/api-token/regenerate`. `GET /api/auth/api-token` returns current token for display.
- **MCP tool DI pattern**: tool classes (Scoped) inject `IBookRepository`, `AgentRunStateService`, `IServiceScopeFactory`, `IHttpContextAccessor`. User ID extracted from `ClaimTypes.NameIdentifier` on `IHttpContextAccessor.HttpContext.User`.
- **`AddHttpContextAccessor()`** registered in `Program.cs` — required so `IHttpContextAccessor` resolves correctly in MCP tool classes.
- **MCP tools use fire-and-forget pattern** for long-running agent operations: same `RunInBackground` / `IServiceScopeFactory` pattern as `AgentController`. Agent errors logged via `ILogger<AgentMcpTools>`; agent status polled via `get_agent_status` tool.
- **Settings UI `MCP Access` section**: loads token on mount via `getApiToken()`; show/hide toggle + copy-to-clipboard button; regenerate with inline confirmation; collapsible `<details>` with Claude Desktop and VS Code connection config snippets.
- **Public mode**: `PublicModeOptions` record `(bool IsEnabled)` registered as a singleton in `Program.cs`, read from the `"PublicMode"` config key (`appsettings.json` default `false`, override via `PublicMode` env var). Controls whether the `/library` page and `/api/public/*` endpoints allow fully anonymous access (public mode) or restrict to "your own books only, must be logged in" (non-public mode, the default).
- **`PublicController`**: `[AllowAnonymous]` controller at `api/public`. `GET /config` → `{ isPublicMode }` (always accessible, used by the frontend to decide whether to show a login prompt). `GET /genres` → deduplicated, case-insensitive sorted list of all individual genre tokens across visible books. `GET /books` → paginated/filterable list (`author`, `genre`, `chapterCount` exact-match query params); returns 401 if non-public mode and the caller is unauthenticated. `GET /books/{id}` → book detail with only non-archived, non-empty chapters included, plus `writtenChapterCount`. `GET /books/{id}/export?format=html|fb2|epub` → delegates to `BookExportService` (metadata format is intentionally not exposed publicly). In non-public mode, results are scoped to the caller's own books (`UserId` match) via `IBookRepository.GetPublicBooksAsync(PublicBookFilter)`; a book is only listed if it has at least one written (non-archived, non-empty-content) chapter.
- **Display name**: `AppUser.DisplayName` (nullable) lets a user set a public author name distinct from their login `Username`. `AuthController.Me()` is now async (loads the user from the DB) and returns `displayName: user.DisplayName ?? user.Username`. `PATCH /api/auth/profile` (`UpdateProfileRequest(string? DisplayName)`) trims and caps at 100 chars, stores `null` if empty. Surfaced in the UI via a new "Profile" section at the top of `GlobalSettings.tsx`; the public Library/Reader pages show `authorDisplayName` (falls back to `Username` server-side, so the frontend never needs its own fallback logic for other users' books).
- **`useAuth()` `setUser` is a full `Dispatch<SetStateAction<AppUser | null>>`** (not a plain setter) so callers can use the functional-updater form (`setUser(prev => ...)`) after partial updates like saving the profile.
- **Genre as comma-separated list**: `BooksController.NormalizeGenre(string)` splits on `,`, trims each token, drops empties, and rejoins with `", "` — applied on both `Create` and `Update`. The Dashboard book form's genre input keeps a single free-text field with placeholder `"Fantasy, Adventure"` and a hint below it; no multi-select UI. The Library page's genre filter is a `<select>` populated from `GET /api/public/genres`.
- **Public Library UI (`Library.tsx`, route `/library`)**: standalone page (no `BookListLayout`/`BookLayout` sidebar chrome — it's reachable while logged out). Filter bar (author text, genre select, exact chapter-count number input) + paginated `.book-list-card` results (reuses the same card CSS as the Dashboard) + Prev/Next pagination. Shows a centered login prompt instead of results when non-public mode and no authenticated user. Logged-in users see a "← My Books" link back to the Dashboard. Linked from the sidebar (`BookListLayout.tsx`, "📚 Library" nav button) and mounted as a top-level route in `App.tsx` (outside the `ProtectedRoutes` auth guard, alongside `/login`).
- **Public Book Reader UI (`PublicBookReader.tsx`, route `/library/:bookId`)**: two-column layout — left nav panel lists all written chapters (number + title, click to switch), right content area renders the selected chapter's markdown via `react-markdown`, with a download bar (HTML/FB2/EPUB via `window.location.href = /api/public/books/{id}/export?format=...`) and Prev/Next chapter buttons at the bottom. Same login-prompt gating as `Library.tsx`.
- **Private `ChapterView.tsx` prev/next**: mirrors the reader page — computed from `book.chapters` filtered to non-archived and sorted by `number`; renders `.chapter-prev-next` buttons under the chapter content that navigate to `/books/{bookId}/chapters/{prevOrNext.id}`.
- **BETA badge**: small red (`#c0392b`), uppercase, `.beta-badge` span rendered immediately after the `.version-pill` in `Sidebar.tsx`; hidden when the sidebar is collapsed (same pattern as the version pill).

---

## Further Considerations

1. **Concurrent agent runs** — Should multiple agents run in parallel on different chapters (e.g., Writer on Ch3 while Editor reviews Ch2)? Recommend yes, with a configurable concurrency limit.
2. **Chapter editing** — Should users be able to manually edit chapter content in the UI (rich markdown editor), or only through agents? Recommend allowing manual edits alongside agent work.

---

## Implementation Notes (Technical Gotchas)

- **EF Core / Npgsql**: Use `10.0.*` versions; both stable as of April 2026
- **`ABook.Agents` pins `Microsoft.EntityFrameworkCore.Relational 10.0.*`** (no longer conflicts with any SK package — Semantic Kernel was removed in v0.1.19)
- **pgvector / Npgsql**: `Pgvector.EntityFrameworkCore 0.*` + `Npgsql.EntityFrameworkCore.PostgreSQL 10.0.*`; call `options.UseNpgsql(cs, o => o.UseVector())` + `using Pgvector.EntityFrameworkCore`; requires direct `PackageReference` to `Pgvector.EntityFrameworkCore` in both `ABook.Infrastructure` and `ABook.Api`
- **SK embedding API**: `ITextEmbeddingGenerationService.GenerateEmbeddingsAsync(list)` — not `GenerateEmbeddingAsync`
- **`OllamaPromptExecutionSettings.Temperature`** is `float?` not `double`
- **`Microsoft.AspNetCore.SignalR 1.*`** NuGet package removed — SignalR is built into the framework in .NET 3+
- **Enum JSON serialization**: Register `JsonStringEnumConverter` in `AddJsonOptions` so enum values serialize as strings for the React client
- **`parsePlanningStream`** and three sibling parsers: React helpers that progressively parse the Planner/planning agents' streaming JSON. `parsePlanningStream` → chapter cards; `parseStoryBibleStream` → key/value fields; `parseCharactersStream` + `parsePlotThreadsStream` → complete object extraction. All wrap item parsing in `try/catch` to skip malformed partial JSON.
- **`AskUserAndWaitAsync` in `AgentBase`**: creates a `TaskCompletionSource<string>`, registers it via `AgentRunStateService.SetPending`, sets status to `WaitingForInput`, then `await tcs.Task`. Unblocked by `ResumeWithAnswerAsync` (answer) or `CancelRun` (cancellation). `AgentBase` now takes `AgentRunStateService` as a constructor param.
- **Full autonomous workflow**: `POST /api/books/{id}/agent/workflow/start` runs Plan → Write+Edit each chapter → Continuity check in sequence. Uses a `CancellationTokenSource` from `AgentRunStateService.CreateRunCts`. Stop via `POST .../workflow/stop`.
- **`PlannerAgent`** is Phase 4 only: generates Chapter Outlines from the already-saved Bible/Characters/Threads passed in as parameters.
- **`WriterAgent`**: `WriteAsync` writes the full chapter in a single streaming call. No mid-generation pauses — all questions are asked upfront by `QuestionAgent` before planning begins.
- **`WorkflowProgress` SignalR event**: emitted by `AgentOrchestrator.StartWorkflowAsync` at each step with `(bookId, step, isComplete)`. UI accumulates steps in `workflowLog` state array shown in sidebar.
