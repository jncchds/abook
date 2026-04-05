# Plan: Agentic Book-Writing ASP.NET Core App

## TL;DR

Build a Docker-packaged ASP.NET Core 10 web app with a React (TypeScript/Vite) UI **served as static files from wwwroot** (NOT a separate Docker service) that uses Semantic Kernel to orchestrate four AI agents (Planner, Writer, Editor, Continuity Checker) for collaborative book writing. Agents stream progress via SignalR and can pause to ask the user plot-clarifying questions. PostgreSQL stores relational data; **Qdrant vector database** (separate Docker service) stores chapter embeddings for RAG-based context retrieval. LLM provider is pluggable (Ollama by default, swappable to OpenAI/Azure/Anthropic via configuration). Multi-user with cookie-based authentication.

---

## Architecture Overview

```
[React SPA (static files in ASP.NET wwwroot — single container)] 
    ↕ REST API + SignalR
[ASP.NET Core 10 API]
    ↕ Semantic Kernel (pluggable LLM connector)
    ↕ EF Core                    ↕ Qdrant .NET client
[PostgreSQL (Docker)]    [Qdrant (Docker)]    [Ollama (host machine)]
```

Docker Compose runs: **ASP.NET app (with React static files baked in) + PostgreSQL + Qdrant**. Ollama is external on the host. React is NOT a separate service — it's built in a Dockerfile stage and copied into `wwwroot/`.

---

## Data Model

- **Book**: Id, Title, Premise, Genre, TargetChapterCount, Status (Draft/InProgress/Complete), Language, PlannerSystemPrompt, WriterSystemPrompt, EditorSystemPrompt, ContinuityCheckerSystemPrompt, UserId (FK → AppUser), CreatedAt, UpdatedAt
- **Chapter**: Id, BookId, Number, Title, Outline, Content (markdown), Status (Outlined/Writing/Review/Editing/Done), CreatedAt, UpdatedAt
- **AgentMessage**: Id, BookId, ChapterId (nullable), AgentRole, MessageType (Content/Question/Answer/SystemNote/Feedback), Content, IsResolved, CreatedAt
- **LlmConfiguration**: Id, BookId (nullable, FK → Book), **UserId (nullable, FK → AppUser)**, Provider (Ollama/OpenAI/Azure/Anthropic), ModelName, Endpoint, ApiKey (nullable), EmbeddingModelName (nullable)
  - Lookup chain: book-specific (BookId) → user-default (UserId, no BookId) → global (neither)
- **AppUser**: Id, Username, PasswordHash, IsAdmin

### Vector Store (Qdrant)
- **ChapterEmbedding** — collection per book, stores chunked chapter text with embeddings
  - Each chapter is split into ~500-token overlapping chunks
  - Metadata: BookId, ChapterId, ChapterNumber, ChunkIndex
  - Used by agents for RAG: query relevant passages across all chapters without exceeding context window
  - Embedding model: configurable (default: Ollama embedding model or OpenAI `text-embedding-3-small`)

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
   - `docker-compose.yml` (app + PostgreSQL + Qdrant, with `extra_hosts` for host Ollama access)
   - `.dockerignore`
3. Configure PostgreSQL with EF Core (Npgsql):
   - `AppDbContext` with entities from data model
   - Initial migration
   - Connection string from environment variables / `appsettings.json`
4. Configure Qdrant client:
   - `IVectorStoreService` interface in `ABook.Core`
   - `QdrantVectorStoreService` in `ABook.Infrastructure` using `Qdrant.Client` NuGet
   - Embedding generation via SK `ITextEmbeddingGenerationService` (pluggable, same factory pattern as chat completion)

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
    - On chapter save/update → chunk text → generate embeddings → upsert to Qdrant
    - `RetrieveContext(bookId, query, topK)` method for agents to pull relevant passages
    - Agents use retrieved context instead of full chapter text to stay within context window
11. Implement agent orchestrator (`AgentOrchestrator`):
    - Manages agent run lifecycle (start, pause, resume, complete)
    - Fire-and-forget execution via `RunInBackground` helper using `IServiceScopeFactory`
    - `AgentRunStateService` singleton tracks active runs cross-request; duplicate run returns 409
    - Returns HTTP 202 immediately; progress streamed via SignalR
12. Implement each agent as a Semantic Kernel function/plugin:
    - `PlannerAgent` — system prompt for outlining, outputs structured chapter list
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
    - `abook-api` service with environment variables (DB connection, Ollama URL, Qdrant URL)
    - `postgres` service with volume for data persistence
    - `qdrant` service (`qdrant/qdrant:latest`) with volume for vector data persistence
    - `extra_hosts: ["host.docker.internal:host-gateway"]` for Ollama access
20. ASP.NET SPA fallback middleware to serve React static files for all non-API routes

---

## Relevant Files

- `ABook.slnx` — Solution root (XML format)
- `src/ABook.Core/Models/` — `Book.cs`, `Chapter.cs`, `AgentMessage.cs`, `LlmConfiguration.cs`, `AppUser.cs`, `Enums.cs`
- `src/ABook.Core/Interfaces/` — `IBookRepository.cs`, `IAgentOrchestrator.cs`, `ILlmProviderFactory.cs`, `IVectorStoreService.cs`, `IBookNotifier.cs`, `IUserRepository.cs`
- `src/ABook.Infrastructure/Data/AppDbContext.cs` — EF Core context
- `src/ABook.Infrastructure/Repositories/` — Data access repositories
- `src/ABook.Infrastructure/Llm/LlmProviderFactory.cs` — Pluggable LLM factory
- `src/ABook.Infrastructure/VectorStore/QdrantVectorStoreService.cs` — Qdrant integration, chunking, embedding
- `src/ABook.Infrastructure/Migrations/` — EF Core migrations (`InitialCreate`, `AddLanguageAndUsers`)
- `src/ABook.Agents/AgentBase.cs` — Base class for all agents
- `src/ABook.Agents/PlannerAgent.cs`, `WriterAgent.cs`, `EditorAgent.cs`, `ContinuityCheckerAgent.cs`
- `src/ABook.Agents/AgentOrchestrator.cs` — Run lifecycle management
- `src/ABook.Agents/AgentRunStateService.cs` — Singleton run state tracker
- `src/ABook.Api/Controllers/` — `BooksController`, `ChaptersController`, `MessagesController`, `ConfigurationController`, `AgentController`, `AuthController`, `UsersController`, `OllamaController`
- `src/ABook.Api/Hubs/BookHub.cs` — SignalR hub
- `src/ABook.Api/Services/SignalRBookNotifier.cs` — `IBookNotifier` implementation (decouples agents from SignalR)
- `src/ABook.Api/Program.cs` — App configuration (cookie auth, EF Core, SignalR, Qdrant, SK)
- `src/abook-ui/src/pages/` — `Dashboard.tsx`, `BookDetail.tsx`, `Settings.tsx`, `Login.tsx`, `AdminUsers.tsx`
- `src/abook-ui/src/hooks/` — `useBookHub.ts`, `useAuth.tsx`
- `src/abook-ui/src/utils/bookHtmlExport.ts` — Client-side HTML export: markdown→HTML converter, 6 color presets, font-size controls; `downloadBookAsHtml(book)` triggers browser download
- `src/abook-ui/src/api.ts` — Typed API client
- `src/abook-ui/src/App.tsx` — Router + auth guard
- `Dockerfile` — Multi-stage build (Node 20 + .NET 10 SDK + .NET 10 runtime)
- `docker-compose.yml` — App + PostgreSQL + Qdrant
- `.dockerignore`

---

## Verification

1. `docker-compose up --build` starts app + PostgreSQL + Qdrant, React UI loads at `http://localhost:5000`
2. Create a book via UI → verify stored in PostgreSQL
3. Start planning → Planner agent streams chapter outlines via SignalR, visible in UI in real-time
4. Agent asks a question → notification appears in chat panel → answer submitted → agent resumes
5. Write a chapter → Writer streams content → Editor reviews → Continuity Checker analyzes
6. Switch LLM provider in settings → next agent run uses new provider
7. `docker-compose down && docker-compose up` → data persists (PostgreSQL + Qdrant volumes)

---

## Decisions

- **.NET 10** (latest; Docker images `mcr.microsoft.com/dotnet/sdk:10.0` + `aspnet:10.0`)
- **`.slnx` solution format** — requires .NET 9+ SDK (XML-based, lighter than classic `.sln`)
- **Ollama accessed via host.docker.internal** — not containerized, user manages it externally
- **LLM provider is pluggable** — abstracted behind `ILlmProviderFactory`, configured per-book or globally; supported: Ollama, LMStudio, OpenAI, AzureOpenAI, Anthropic
- **SK Ollama connector** is alpha (`Microsoft.SemanticKernel.Connectors.Ollama` 1.x-alpha); suppress `SKEXP0070` pragma
- **Agents use Semantic Kernel function calling** for the "ask question" tool — agent invokes a `AskUser` function which triggers the pause mechanism
- **Agent runs are fire-and-forget** — `AgentController` returns 202 immediately; `AgentRunStateService` singleton tracks state; duplicate run returns 409
- **Cookie authentication** — multi-user support with `IPasswordHasher<AppUser>`; admin role for user management
- **Per-book customization** — `Language` field and per-agent system prompt overrides stored on `Book` entity
- **Ollama model management** — `OllamaController` proxies Ollama's `/api/tags` and streams pull progress via SSE
- **Markdown only** for book output — no DOCX/PDF export (can be added later)
- **PostgreSQL + Qdrant in compose** with persistent volumes
- **React UI is static files** — built in Dockerfile, served from `wwwroot/` by ASP.NET Core, NOT a separate Docker service
- **Qdrant for vector storage** — separate Docker service, used for RAG-based context retrieval so agents can handle large books without exceeding context windows
- **`IBookNotifier` interface** (`SignalRBookNotifier` implementation) decouples agent/orchestrator code from direct SignalR hub dependency
- **Per-user LLM settings**: `LlmConfiguration` has nullable `UserId`; lookup chain is book-specific → user-default → global. `ConfigurationController` automatically sets `UserId` from cookie claims when saving a global (non-book) config. Agents resolve config via `GetKernelAsync` which passes `book.UserId`.
- **Default system prompts API**: `GET /api/books/{id}/default-prompts` returns pre-interpolated default prompts (using book's title/genre/language). Settings page fetches these and shows a "Load Defaults" button that pre-fills empty textarea fields. Placeholders also show the default text.
- **Migration**: `AddUserLlmConfig` adds nullable `UserId` FK to `LlmConfigurations` table.
- **Qdrant cleanup**: `IVectorStoreService.DeleteCollectionAsync` drops the whole book collection; called by `BooksController.Delete` (non-fatal). `ChaptersController.Update` calls `DeleteChapterChunksAsync` when content is cleared to empty (e.g. the "Clear" button in the UI).
- **LlmProvider.LMStudio**: uses SK's OpenAI connector with a custom endpoint (`/v1`). API key defaults to `"lm-studio"` if omitted. Embedding support requires `EmbeddingModelName` to be set. Default endpoint: `http://host.docker.internal:1234`.
- **`GET /api/ollama/models`** now accepts `?provider=` query param; when `provider=LMStudio` it queries `{endpoint}/v1/models` (OpenAI-compatible format: `{"data":[{"id":"..."}]}`). For Ollama it still queries `/api/tags`.
- **Model list in Settings**: fetched dynamically from the configured endpoint; only Ollama and LMStudio fetch model lists. Switching provider resets endpoint to the provider's default (only if the current endpoint matches the previous provider's default).
- **ContinuityCheckerAgent uses RAG**: runs three targeted queries (character descriptions, timeline, locations) against Qdrant before checking continuity; appends retrieved passages to the LLM prompt alongside the chapter synopsis.
- **`StripLeadingChapterHeading` moved to `AgentBase`** (protected): both `WriterAgent` and `EditorAgent` strip LLM-added chapter headings from prose before saving. Now handles consecutive heading lines, bold-formatted headings, and ordinal word variants ("Chapter One", "Chapter Two" etc.).
- **`InterpolateSystemPrompt` in `AgentBase`** (protected static): replaces `{TITLE}`, `{GENRE}`, `{PREMISE}`, `{LANGUAGE}`, `{CHAPTER_COUNT}` tokens in user-supplied system prompts with book data. All four agents call this when using a custom prompt.
- **`GetPreviousChapterEndingAsync` in `AgentBase`** (protected): returns the last 3 paragraphs of the immediately preceding chapter. `WriterAgent` includes this in the system prompt so prose is narratively continuous even without RAG context.
- **`EditorAgent` notes split**: uses a regex to find any `## Editorial Notes` / `## Editor's Notes` / `## Feedback` etc. heading (case-insensitive) instead of a hard string compare; more resilient to LLM phrasing variation.
- **Settings UI placeholder hint**: the "Custom Agent System Prompts" section now shows a reference block listing all supported template tokens and reminds users that the Editor prompt must end with `## Editorial Notes`.
- **`ContinuityCheckerAgent.CheckAsync` focused mode**: accepts optional `int? chapterId`. When provided (per-chapter workflow), separates chapters into "preceding facts" vs "chapter under review" and instructs the LLM to report only issues *introduced by* that chapter, ignoring pre-existing issues between earlier chapters. No-id calls (final check, standalone button) retain full cross-manuscript review behaviour. `AgentOrchestrator` per-chapter call sites now pass `chapter.Id`; final checks pass null.
- **Token statistics**: `AgentBase.StreamResponseAsync` now accepts `AgentRole role` (required param before `CancellationToken`). After each LLM streaming call, emits approximate token counts (chars/4) via `IBookNotifier.NotifyTokenStatsAsync` → SignalR `TokenStats` event. UI receives stats via `useBookHub.setOnTokenStats` and shows them as a collapsible `<details>` panel at the bottom of the chat sidebar. Total accumulated tokens shown.
- **Chapter inline edit**: "✎ Edit" button appears on chapter header when not running. Opens an inline form to edit title and outline; saves via `PUT /api/books/{id}/chapters/{chapterId}`.
- **Book inline edit**: "✎ Edit" button on book overview. Opens an inline form to edit title, genre, target chapters, premise/plot; saves via `PUT /api/books/{id}`.
- **Add chapter manually**: "+ Chapter" button at the bottom of the sidebar chapter list. Inline form collects title and outline; auto-assigns the next chapter number; saves via `POST /api/books/{id}/chapters` and immediately selects the new chapter.

---

## Further Considerations

1. **Concurrent agent runs** — Should multiple agents run in parallel on different chapters (e.g., Writer on Ch3 while Editor reviews Ch2)? Recommend yes, with a configurable concurrency limit.
2. **Chapter editing** — Should users be able to manually edit chapter content in the UI (rich markdown editor), or only through agents? Recommend allowing manual edits alongside agent work.

---

## Implementation Notes (Technical Gotchas)

- **EF Core / Npgsql**: Use `10.0.*` versions; both stable as of April 2026
- **`ABook.Agents` pins `Microsoft.EntityFrameworkCore.Relational 10.0.*`** to avoid version conflict with Semantic Kernel
- **Qdrant.Client 1.x API**: `CreateCollectionAsync(name, VectorParams, ...)` — not `VectorsConfig`
- **SK embedding API**: `ITextEmbeddingGenerationService.GenerateEmbeddingsAsync(list)` — not `GenerateEmbeddingAsync`
- **`OllamaPromptExecutionSettings.Temperature`** is `float?` not `double`
- **`Microsoft.AspNetCore.SignalR 1.*`** NuGet package removed — SignalR is built into the framework in .NET 3+
- **Enum JSON serialization**: Register `JsonStringEnumConverter` in `AddJsonOptions` so enum values serialize as strings for the React client
- **`parsePlanningStream`**: React helper that progressively parses the Planner agent's streaming JSON into chapter cards as tokens arrive
- **`AskUserAndWaitAsync` in `AgentBase`**: creates a `TaskCompletionSource<string>`, registers it via `AgentRunStateService.SetPending`, sets status to `WaitingForInput`, then `await tcs.Task`. Unblocked by `ResumeWithAnswerAsync` (answer) or `CancelRun` (cancellation). `AgentBase` now takes `AgentRunStateService` as a constructor param.
- **Full autonomous workflow**: `POST /api/books/{id}/agent/workflow/start` runs Plan → Write+Edit each chapter → Continuity check in sequence. Uses a `CancellationTokenSource` from `AgentRunStateService.CreateRunCts`. Stop via `POST .../workflow/stop`.
- **`PlannerAgent`** generates outline directly from book metadata with no up-front question.
- **`WriterAgent` mid-generation questions**: the LLM can emit `[ASK: question]` at any point while writing to pause and request the author's input on a pivotal plot/character decision. `WriteWithQuestionsAsync` loops up to 6 rounds: it streams until the marker is detected, extracts the partial prose (before the marker), calls `AskUserAndWaitAsync`, then feeds the partial prose + answer back as a new turn and resumes streaming. Works with any model — no function-calling required.
- **`WorkflowProgress` SignalR event**: emitted by `AgentOrchestrator.StartWorkflowAsync` at each step with `(bookId, step, isComplete)`. UI accumulates steps in `workflowLog` state array shown in sidebar.
