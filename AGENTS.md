# Plan: Agentic Book-Writing ASP.NET Core App

## TL;DR

Build a Docker-packaged ASP.NET Core 8 web app with a React (TypeScript/Vite) UI **served as static files from wwwroot** (NOT a separate Docker service) that uses Semantic Kernel to orchestrate four AI agents (Planner, Writer, Editor, Continuity Checker) for collaborative book writing. Agents stream progress via SignalR and can pause to ask the user plot-clarifying questions. PostgreSQL stores relational data; **Qdrant vector database** (separate Docker service) stores chapter embeddings for RAG-based context retrieval. LLM provider is pluggable (Ollama by default, swappable to OpenAI/Azure/Anthropic via configuration).

---

## Architecture Overview

```
[React SPA (static files in ASP.NET wwwroot — single container)] 
    ↕ REST API + SignalR
[ASP.NET Core 8 API]
    ↕ Semantic Kernel (pluggable LLM connector)
    ↕ EF Core                    ↕ Qdrant .NET client
[PostgreSQL (Docker)]    [Qdrant (Docker)]    [Ollama (host machine)]
```

Docker Compose runs: **ASP.NET app (with React static files baked in) + PostgreSQL + Qdrant**. Ollama is external on the host. React is NOT a separate service — it's built in a Dockerfile stage and copied into `wwwroot/`.

---

## Data Model

- **Book**: Id, Title, Premise, Genre, TargetChapterCount, Status (Draft/InProgress/Complete), CreatedAt, UpdatedAt
- **Chapter**: Id, BookId, Number, Title, Outline, Content (markdown), Status (Outlined/Writing/Review/Editing/Done), CreatedAt, UpdatedAt
- **AgentMessage**: Id, BookId, ChapterId (nullable), AgentRole, MessageType (Content/Question/Answer/SystemNote/Feedback), Content, IsResolved, CreatedAt
- **LlmConfiguration**: Id, BookId (nullable, null = global default), Provider (Ollama/OpenAI/Azure/Anthropic), ModelName, Endpoint, ApiKey (nullable)

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
1. Agent encounters ambiguity → creates AgentMessage with type=Question
2. SignalR pushes notification to React UI
3. Agent run pauses (status=WaitingForInput)
4. User sees question in chat panel, types answer → creates AgentMessage with type=Answer
5. Backend resumes the agent run with the user's answer injected into context

---

## Phases

### Phase 1: Project Scaffolding & Infrastructure
1. Create solution structure:
   - `ABook.sln`
   - `src/ABook.Api/` — ASP.NET Core Web API project (.NET 8)
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
    - Stores state in DB so runs survive app restarts
    - Background service (`IHostedService`) processes queued agent tasks
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
    - **Book Detail**: Overview, chapter list with statuses, settings
    - **Chapter View**: Rendered markdown content, edit capability
    - **Agent Chat Panel**: Sidebar/panel showing agent messages, questions, answer input
    - **Settings**: LLM provider configuration (provider, model, endpoint, API key)
    - **Real-time indicators**: Streaming text as agents write, status badges per agent
16. SignalR integration:
    - `useSignalR` hook connecting to `BookHub`
    - Live token streaming rendered in chapter view
    - Toast/notification when agent asks a question
17. Build React for production → output to `src/ABook.Api/wwwroot/`

### Phase 5: Docker & Integration — *depends on Phases 3 & 4*
18. Finalize multi-stage Dockerfile:
    - Stage 1: Build React (`node:20-alpine`, `npm run build`)
    - Stage 2: Build .NET (`mcr.microsoft.com/dotnet/sdk:8.0`, `dotnet publish`)
    - Stage 3: Runtime (`mcr.microsoft.com/dotnet/aspnet:8.0`, copy published + wwwroot)
19. Finalize `docker-compose.yml`:
    - `abook-api` service with environment variables (DB connection, Ollama URL, Qdrant URL)
    - `postgres` service with volume for data persistence
    - `qdrant` service (`qdrant/qdrant:latest`) with volume for vector data persistence
    - `extra_hosts: ["host.docker.internal:host-gateway"]` for Ollama access
20. ASP.NET SPA fallback middleware to serve React static files for all non-API routes

---

## Relevant Files (to be created)

- `ABook.sln` — Solution root
- `src/ABook.Core/Models/` — `Book.cs`, `Chapter.cs`, `AgentMessage.cs`, `LlmConfiguration.cs`, enums
- `src/ABook.Core/Interfaces/` — `IBookRepository.cs`, `IAgentOrchestrator.cs`, `ILlmProviderFactory.cs`, `IVectorStoreService.cs`
- `src/ABook.Infrastructure/Data/AppDbContext.cs` — EF Core context
- `src/ABook.Infrastructure/Repositories/BookRepository.cs` — Data access
- `src/ABook.Infrastructure/Llm/LlmProviderFactory.cs` — Pluggable LLM factory
- `src/ABook.Infrastructure/VectorStore/QdrantVectorStoreService.cs` — Qdrant integration, chunking, embedding
- `src/ABook.Agents/PlannerAgent.cs`, `WriterAgent.cs`, `EditorAgent.cs`, `ContinuityCheckerAgent.cs`
- `src/ABook.Agents/AgentOrchestrator.cs` — Run lifecycle management
- `src/ABook.Api/Controllers/` — REST endpoints
- `src/ABook.Api/Hubs/BookHub.cs` — SignalR hub
- `src/ABook.Api/Program.cs` — App configuration
- `src/abook-ui/src/` — React app source
- `Dockerfile` — Multi-stage build
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

- **.NET 8** (LTS, broad Semantic Kernel support)
- **Ollama accessed via host.docker.internal** — not containerized, user manages it externally
- **LLM provider is pluggable** — abstracted behind `ILlmProviderFactory`, configured per-book or globally
- **Agents use Semantic Kernel function calling** for the "ask question" tool — agent invokes a `AskUser` function which triggers the pause mechanism
- **No authentication** — single-user local app
- **Markdown only** for book output — no DOCX/PDF export (can be added later)
- **PostgreSQL + Qdrant in compose** with persistent volumes
- **React UI is static files** — built in Dockerfile, served from `wwwroot/` by ASP.NET Core, NOT a separate Docker service
- **Qdrant for vector storage** — separate Docker service, used for RAG-based context retrieval so agents can handle large books without exceeding context windows

---

## Further Considerations

1. **Concurrent agent runs** — Should multiple agents run in parallel on different chapters (e.g., Writer on Ch3 while Editor reviews Ch2)? Recommend yes, with a configurable concurrency limit.
2. **Chapter editing** — Should users be able to manually edit chapter content in the UI (rich markdown editor), or only through agents? Recommend allowing manual edits alongside agent work.
