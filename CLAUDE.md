# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Commands

### Backend (.NET 10 — run from repo root)

```bash
dotnet build                          # build all projects
dotnet run --project src/ABook.Api    # start API (listens on http://localhost:5193)
```

### Frontend (run from `src/abook-ui/`)

```bash
npm install
npm run dev      # Vite dev server at http://localhost:5173, proxies /api and /hubs to http://localhost:5000
npm run build    # TypeScript check + Vite build → outputs to src/ABook.Api/wwwroot/
npm run lint     # ESLint
npx tsc --noEmit -p tsconfig.app.json  # type-check only (plain `tsc --noEmit` checks nothing: tsconfig.json only holds references)
```

> **Note:** the Vite dev server proxies to port 5000, but `launchSettings.json` runs the API on 5193 by default. Either change the proxy target in `vite.config.ts` to match, or run the API on 5000 (e.g. via `--urls http://localhost:5000`).

### Database migrations (from repo root)

```bash
dotnet ef migrations add <MigrationName> --project src/ABook.Infrastructure --startup-project src/ABook.Api
dotnet ef database update --project src/ABook.Infrastructure --startup-project src/ABook.Api
```

Always use the `dotnet ef` CLI — never create migration files by hand.

### Local dev database

```bash
docker-compose up postgres -d   # start PostgreSQL with pgvector only
docker-compose up -d            # full stack (app + postgres)
docker build -t abook .         # build the production Docker image
```

Copy `src/ABook.Api/appsettings.Local.example.json` → `appsettings.Local.json` for local LLM overrides (not committed).

### LLM debug logging

Set env var `LLM_DEBUG_LOGGING=true` to print full chat history and LLM responses to the application log at `Information` level.

## Architecture

```
React SPA (static files in ASP.NET wwwroot — single container)
        ↕ REST API + SignalR
ASP.NET Core 10 API
        ↕ Direct provider clients  ↕ EF Core 10 + Npgsql
LLM (Ollama / OpenAI / OpenAICompatible / GoogleAIStudio)   PostgreSQL 16 (pgvector in-DB)
```

React is built at image-build time and served from `wwwroot/` — there is no separate frontend container at runtime.

### Project layout

| Project | Purpose |
|---|---|
| `src/ABook.Core` | Domain models (`Book`, `Chapter`, `AgentMessage`, …) and interfaces (`IBookRepository`, `IAgentOrchestrator`, `ILlmProviderFactory`, `IVectorStoreService`, …) |
| `src/ABook.Infrastructure` | EF Core `AppDbContext`, repositories, LLM provider factory + strategies, pgvector service, migrations |
| `src/ABook.Agents` | All agent classes (`WriterAgent`, `EditorAgent`, `ContinuityCheckerAgent`, `PlannerAgent`, `StoryBibleAgent`, `CharactersAgent`, `PlotThreadsAgent`, `QuestionAgent`, `IllustrationPlannerAgent`, `ChapterSummarizer`, and the book chat in `BookChat/`: `BookChatAgent`, `ProposalExecutor`, `BookContentService`), `AgentOrchestrator`, `AgentRunStateService`, `AgentPrompts` |
| `src/ABook.Api` | Controllers, SignalR `BookHub`, MCP tool classes, `Program.cs`, hosted services |
| `src/abook-ui` | React 19 + TypeScript + Vite SPA |

### Key architectural patterns

**LLM provider factory** — `LlmProviderFactory` dispatches to per-provider strategy classes (`src/ABook.Infrastructure/Llm/Strategies/`). Supported: `Ollama`, `OpenAI`, `OpenAICompatible` (LM Studio, OpenRouter, vLLM, …), `GoogleAIStudio`. Each book owns its LLM config, copied from the owner's default preset at creation — there is no user or global fallback.

**Agent orchestration** — `AgentOrchestrator` runs fire-and-forget via `IServiceScopeFactory`. `AgentRunStateService` (singleton) tracks in-memory state; `AgentRun` entity persists run lifecycle to the DB for restart resilience. All LLM calls use `StreamResponseAsync` — there is no non-streaming path.

**Human-in-the-loop** — `AgentBase.AskUserAndWaitAsync` creates a `TaskCompletionSource<string>`, sets run state to `WaitingForInput`, and awaits. `AgentOrchestrator.ResumeWithAnswerAsync` unblocks it. A paused run cannot survive a restart (its call stack is gone), so `RunRecoveryService` (hosted service) marks leftover `Running` and `WaitingForInput` runs `Orphaned` on startup and tells the author to use Continue.

**Starting runs** — always through `AgentRunnerService.TryStart`, which atomically claims the book + a concurrency slot and creates the run's cancellation token (`AgentRunStateService.TryStartRun`) before starting the orchestrator in the background. Never call orchestrator run methods directly.

**Prompts** — every piece of text sent to an LLM lives in `src/ABook.Agents/AgentPrompts.cs` (`DefaultPrompts`, `PromptTemplates`, `RagQueries`). Agents gather data and call the templates; do not build prompt text anywhere else.

**Streaming** — only book content (story bible, characters, plot threads, chapter outlines, chapter prose) is streamed to the UI. Reasoning/thinking output is never streamed; it is saved as a "💭 Thinking" chat message when each LLM call ends. Pass `forwardToUi: false` to `StreamResponseAsync` for any call whose output is not content.

**Checker → mechanical patch apply** — `ContinuityCheckerAgent` outputs structured JSON (`CheckerResult`) with `originalText`, `replacementText`, and optional `position` (1-indexed line hint). `EditorAgent.ApplyPatchesAsync` locates patches via `IndexOf` with whitespace normalization and applies them end-first — no LLM call for the edit step.

**Interactive books (book chat)** — `Book.Mode` is Autonomous (pipeline) or Interactive. Interactive books are written in a chat: `BookChatAgent` replies with JSON (message, conflicts, actions), `BookChatActions.Normalize` turns actions into `ChatProposal`s, and nothing changes until the author applies them via `ProposalExecutor` → `BookContentService` (which returns before/after renderings for undo). Written chapters never move (unwritten ones may be inserted, moved or removed anywhere). The premise and Story Bible lock when writing starts (`StoryBibleLockedAt`); only the author unlocks them. Keep the chat context bounded: digest + rolling `ChatMemory` + last few messages — never resend the whole history. See AGENTS.md "Interactive books".

**Telegram bot** — `src/ABook.Api/Telegram/`, off unless `TELEGRAM_BOT_TOKEN` is set (`ABOOK_HOSTNAME` = webhook, else long polling). Access is the bound chat/topic (`TelegramBinding.Level`), never an abook account; bindings come only from one-time codes made in Book Settings. `FanOutBookNotifier` (the registered `IBookNotifier`) feeds `TelegramEventSink` after SignalR; `TelegramDispatcher` runs one paced `BindingWorker` per binding; `TelegramUpdateHandler` handles updates. Plain messages are never treated as answers — only explicit replies to a question message, buttons and commands (Interactive books: plain text = book-chat message). See AGENTS.md "Telegram bot". Linked accounts (`AppUser.TelegramUserId`) manage their books in the private chat through **cards** (`Telegram/Cards/`: one message edited in place, state in `TelegramCard`, reply-to-edit, drafts saved through `BookContentService`) — see AGENTS.md "Telegram accounts and book management cards".

**Sections and chapter order** — every chapter belongs to a `Section` (a part of the plan; one chapter by default). Places are `SortOrder` (archived rows included); `Chapter.Number` / `Section.Number` are derived counts of active rows, kept by the repository through the pure `ChapterOrdering` — **never locate a chapter or section by number once it can move; use ids**, and change order only through the repository's structure methods (insert/move/archive/restore/split/breakdown). A section with one active chapter mirrors that chapter's plan (`MirrorSingleChapterPlansAsync` in `SaveChangesAsync`). Plot threads' and characters' chapter-number fields count **sections**. Planning outlines sections; `PlannerAgent.BreakDownSectionAsync` divides one into chapters. See AGENTS.md "Chapter order and sections".

**Rewind & change log** — `AppDbContext.SaveChangesAsync` writes a `ContentRevision` (full row state, dated) for every save of Book, Chapter, Section, StoryBible, CharacterCard, PlotThread, Clarification and ChatProposal. **Never use `ExecuteUpdate`/`ExecuteDelete` on those tables** — bulk writes bypass the log and a rewind would restore the wrong state. A rewind (`IBookRewinder`, run type `rewind`) writes the state at T as new versions and hides later book-chat messages (`HiddenAt`; `GetChatMessagesAsync` excludes them unless `includeHidden`). See AGENTS.md "Rewind".

**RAG** — `PgvectorVectorStoreService` stores chunked chapter embeddings. Writer uses 3 targeted queries; Editor uses 4 (adds repeated-phrase detection). Ancestry-aware: `SearchAsync` accepts `scopeBookIds` so sequel books include ancestor embeddings.

**Versioning** — `VERSION` at repo root is the single source of truth. Vite reads it at build time and injects it as `__APP_VERSION__`. Before bumping the version, check whether the current `VERSION` value has already been pushed to origin (`git log origin/main..HEAD -- VERSION`); if it hasn't, reuse the existing version rather than bumping again.

### React frontend structure

- `src/abook-ui/src/App.tsx` — routing: `BookListLayout` for `/`, `/settings`, `/presets`, `/admin/users`; `BookLayout` for `/books/:bookId/*`; `LibraryLayout` for `/library/*`
- `src/abook-ui/src/contexts/BookContext.tsx` — all book/chapter/planning/agent state; single SignalR registration
- `src/abook-ui/src/layouts/` — `BookListLayout`, `BookLayout`, `LibraryLayout`
- `src/abook-ui/src/pages/book/` — per-book sub-pages (`Overview`, `Clarifications`, `StoryBible`, `Characters`, `PlotThreads`, `Chapters`, `ChapterView`, `Illustrations`, `BookChat` at `/studio`, `ChatPage` = Agent Messages, `TokenStatsPage`, `Rewind`, `BookSettings`)
- `src/abook-ui/src/hooks/useBookHub.ts` — SignalR hook
- `src/abook-ui/src/api.ts` — all REST calls via axios
- `src/abook-ui/src/utils/streamParsers.ts` — streaming JSON parsers for planning agents

### MCP server

Built-in at `/mcp` using `ModelContextProtocol.AspNetCore 1.2.0`. Tool classes in `src/ABook.Api/Mcp/`. Authenticated via `Authorization: Bearer <api-token>` (the `ApiToken` scheme in `ApiTokenAuthenticationHandler`) or a browser JWT access token.

## Mandatory workflow rules

1. **After every code change** — check for compilation errors and warnings: `dotnet build` (backend), `npx tsc --noEmit -p tsconfig.app.json` in `src/abook-ui` (frontend). Fix all errors before finishing; fix warnings whenever it is safe to do so without changing behaviour.
2. **After every committed change** — version check-bump flow:
   a. Check if the current `VERSION` has been pushed to origin (`git log origin/main..HEAD -- VERSION`); if so, bump the patch number and create a new `## v{version} — {date}` heading in `RELEASE_NOTES.md`; otherwise reuse the current version.
   b. Verify the heading for the current version in `RELEASE_NOTES.md` carries today's date; update it if it doesn't.
   c. Append a bullet for the change under that heading.
3. **After every architectural or schema change** — update `AGENTS.md` (authoritative design doc) and `README.md` (user-facing docs).
4. **Never manually create migration files** — always use `dotnet ef migrations add`.
5. **Never run the project outside of Docker** — do not use `dotnet run` or `npm run dev` to start the application. Use `docker-compose up -d` for the full stack or `docker-compose up postgres -d` for the database only.

## Technical gotchas

- `ABook.Agents` pins `Microsoft.EntityFrameworkCore.Relational 10.0.*` (removing it gives an MSB3277 version-conflict warning).
- `Pgvector.EntityFrameworkCore 0.*` must be referenced directly in both `ABook.Infrastructure` and `ABook.Api`; call `options.UseNpgsql(cs, o => o.UseVector())`.
- Ollama embeddings go through an `OpenAIClient` pointed at Ollama's `/v1` endpoint (`OllamaProviderStrategy`).
- Enum JSON serialization: `JsonStringEnumConverter` is registered in both `AddJsonOptions` (controllers) and SignalR's `AddJsonProtocol`.
- All hub events go out as one `BookEvent { seq, name, args }` message, numbered per book by `BookEventLog` — send only through `IBookNotifier`, never `IHubContext<BookHub>` directly, or the sequence breaks. `AgentStreaming`'s args are `(bookId, chapterId, agentRole, token)`.
- `LlmProvider.LMStudio` and `LlmProvider.Anthropic` have been removed from the enum; old DB rows with those values will cause EF errors.
