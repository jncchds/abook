# Release Notes

## v0.1.15 — 2026-07-14

- refactor: eliminated all non-streaming LLM calls across the agent engine — `GetCompletionAsync` (AgentBase) removed entirely; QuestionAgent, ContinuityChecker.PreWriteCheckAndFixAsync, and ContinuityChecker.CheckAsync now use `StreamResponseAsync` exclusively. All 4 prior call sites replaced with streaming; JSON parsing still works on the fully-accumulated response after streaming completes
- feat: upfront Q&A preview — QuestionAgent posts a single overview message listing ALL upcoming clarifying questions before presenting them one-by-one, so the user is prepared to what's coming (planner role, question type); individual questions still appear via AskUserAndWaitAsync for answer/skip per item
- refactor: cleaned up reasoning/thinking content extraction in `AgentBase.StreamResponseAsync` — removed per-chunk array allocation (`new[] { ... }`) inside the streaming loop; consolidated metadata key check to single canonical key `ReasoningContent` with short-circuit evaluation; simplified merge comment for clarity. No functional change, same behavior as prior v0.1.15 release.

## v0.1.14 — 2026-07-14

- feat: structured JSON output via typed schemas — planning agents (Story Bible, Characters, Plot Threads, Chapter Outlines) and Continuity Checker now use `CreateJsonSchemaFormat` with precise JSON schemas instead of generic `jsonMode: true`; prevents LLM type mismatches (e.g. object vs array)
- feat: empty-response guard on planning agent parsers — StoryBibleAgent, CharactersAgent, PlotThreadsAgent, PlannerAgent (chapter outlines) now throw a clear `FormatException` when the LLM returns no JSON data at all
- break: removed Anthropic provider — `AnthropicProviderStrategy.cs` deleted; `LlmProvider.Anthropic` removed from enum. Users who previously relied on Anthropic can achieve similar functionality by using OpenAI provider with an OpenAI-compatible proxy endpoint (e.g. LiteLLM)
- fix: improved OpenAI API key validation in provider strategies — clearer error messages when no endpoint is set and no API key is provided

## v0.1.13 — 2026-07-13

- feat: Library redesigned with full sidebar layout (same style as rest of app); download buttons moved to sidebar; chapter list in sidebar uses circular number badges; each chapter is a separate route (`/library/:bookId/chapters/:chapterId`); navigating to `/library/:bookId` auto-redirects to the first chapter; unauthenticated users are now redirected to `/library` instead of `/login`

## v0.1.12 — 2026-07-12

- feat: LLM thinking/reasoning content is now captured and saved as an agent message — supports `<think>…</think>` / `<thinking>…</thinking>` tags (DeepSeek-R1, Qwen3, etc.) and vendor metadata keys (`ReasoningContent`, `Thinking`, etc.) in both streaming and non-streaming calls; saved as a `SystemNote` with a 💭 prefix; UI refreshes via a new `MessagesUpdated` SignalR event
- feat: agent message cards in the chat panel are now collapsible `<details>` elements — long messages (>200 chars) are collapsed by default showing a plain-text content preview; a chevron rotates to indicate open/closed state

- feat: public mode — new `PublicMode` config flag (`appsettings.json` / env var) toggles anonymous access to a public library; `PublicController` (`/api/public/*`) exposes config, genre list, paginated/filterable book list, single book detail, and HTML/FB2/EPUB export, all without cookie auth when enabled
- feat: public Library page (`/library`) — paginated list of books filterable by author display name, genre, and exact written-chapter count; shows a login prompt for unauthenticated users when public mode is off; shows the current user's own books when logged in and public mode is off
- feat: public Book Reader page (`/library/:bookId`) — left-hand chapter navigation panel, prev/next buttons, and HTML/FB2/EPUB download buttons backed by the public export endpoint
- feat: user display names — `AppUser.DisplayName` (nullable, migration `AddDisplayName`) lets a user set a public-facing author name via a new "Profile" section at the top of Global Settings; falls back to the login username everywhere it's shown (author byline in Library, export bylines)
- feat: prev/next chapter navigation added to the private `ChapterView` page (mirrors the reader page behaviour)
- feat: genre is now treated as a comma-separated list — `BooksController` normalizes (trims each item, rejoins with `, `) on create/update; Dashboard genre field shows a "Fantasy, Adventure" placeholder + hint; Library page offers a genre dropdown populated from `/api/public/genres`
- feat: BETA badge — small red uppercase badge next to the version pill in the sidebar
- refactor: extracted all HTML/FB2/EPUB/metadata export generation out of `ExportController` into a new scoped `BookExportService`; `ExportController` is now a thin authenticated wrapper and `PublicController` reuses the same service for anonymous exports
- fix: `UserDto` now returns `displayName` (falling back to `username`) so login/register responses are consistent with `GET /api/auth/me`; previously login/register returned no `displayName`, causing `user.displayName` to be undefined until the next page reload

## v0.1.11 — 2026-06-27

- fix: continuation books — planning agents now receive full prior-book context (chapter synopses + richer character personality/arc fields in `BuildAncestorPlanningReferenceAsync`); QuestionAgent upfront Q&A skips already-established world-building for continuations
- fix: checker → mechanical patch apply — `ContinuityCheckerAgent` prompts now produce verbatim `originalText`/`replacementText` per issue; `EditorAgent` applies patches mechanically via `string.IndexOf` (no LLM call when checker issues exist); human notes collected before checker and fed to it; no redundant Final Check pass
- fix: agent messages no longer bleed into chapter content — `PreWriteCheckAndFixAsync` uses `GetCompletionAsync` (non-streaming) to avoid emitting tokens to the chapter view; `StreamResponseAsync` gains optional `stopStreamingAt` regex to halt streaming before editorial-notes sections
- fix: duplicate content in Characters/PlotThreads/Chapters pages — persisted lists are hidden while the corresponding planning stream is active (`{!charactersStream && ...}`, `{!plotThreadsStream && ...}`, `!(plannerBuffer && runStatus?.role === 'ChaptersAgent')`)
- fix: stream buffer not restored after hard-refresh in `ChapterView` — `useRestoreStream` now receives the active `agentRole` computed from `runStatus`; `AgentRunStateService.GetStreamBufferContent` with `agentRole=null` scans all buffers to find a non-empty one for the given book+chapter

## v0.1.10 — 2026-06-26

- feat: add book continuation lineage (`Books.BaseBookId`, `Books.SettingsCopiedAt`) with self-FK migration `AddBookBaseLineage`
- feat: extend `POST /api/books` with optional `baseBookId`; creating from a base book now copies language, human-assisted mode, target chapter count, all 7 system prompts, and book-scoped LLM configuration
- feat: make RAG ancestry-aware — vector search now supports scoped book IDs, and descendant books query embeddings across full ancestor chains (parent, grandparent, etc.)
- feat: inject ancestor Story Bible / Characters / Plot Threads as reference context into planning generation (Story Bible, Characters, Plot Threads, Chapter Outlines)
- feat: add UI support for continuation creation and lineage display in Dashboard, Overview, and Book Settings

## v0.1.9 — 2026-06-07

- fix: add StoryBible include in GetByIdWithDetailsAsync (was returning null for StoryBible)
- fix: add silent-skip logging for already-Done chapters in ProcessChapterAsync
- fix: remove HasData seed from LlmConfiguration in AppDbContext (handled by Program.cs UpsertLlmConfigAsync)
- fix: add AsSplitQuery to GetByIdWithDetailsAsync to avoid Cartesian explosion
- fix: add appsettings.Local.example.json to .gitignore
- fix: remove deprecated IndexChapterAsync from WriterAgent
- fix: extract DefaultChunkSize and DefaultOverlap constants in TextChunker
- fix: add explicit HasMany mappings for Book entity in AppDbContext
- docs: document API key plaintext limitation, singleton scaling limitation, approximate token counting, MEAI migration path, OpenTelemetry TODO

## v0.1.8 — 2026-05-09

- feat: PWA support — app is now installable (add to home screen / desktop); service worker precaches static assets and serves offline fallback; `sw.js` and `manifest.webmanifest` generated into `wwwroot/` at build time
- feat: optional browser notifications (in-tab/background) for `AgentQuestion`, `AgentError`, and workflow completion events; opt-in toggle in Global Settings; fires only when the browser tab is in the background
- feat: `PwaUpdatePrompt` component — shows a dismissable bottom-right banner when a new app version is deployed, prompting the user to reload
- feat: site favicon updated to `pwa-192x192.png` (PNG replaces SVG); `apple-touch-icon` also points to the same image

## v0.1.7 — 2026-05-09

- fix: Embedder `TokenUsageRecord` rows now populate `Endpoint` and `ModelName` (using `config.Endpoint` and `config.EmbeddingModelName`) in all three creation sites — `GetRagContextAsync`, `IndexChapterVersionAsync` (AgentBase), and the legacy indexing helper in WriterAgent

## v0.1.6 — 2026-05-08

- fix: chapter outlines not persisted after planning — replaced per-chapter `AddChapterAsync` loop with atomic `ReplaceChaptersAsync` (single DB transaction); a mid-loop failure can no longer leave the DB in a partially-saved state
- fix: planning streaming preview flashes empty briefly after completion — `plannerBuffer` is now cleared only after `refreshBook()` resolves (not before), so chapter cards appear directly without an intermediate empty state; also added `.catch` to always clear `runStatus` even on network errors

## v0.1.5 — 2026-05-08

- fix: chapters disappear after planning completes — `WorkflowProgress(isComplete=true)` now clears `runStatus` only AFTER `refreshBook()` resolves, so the "No chapters yet" empty state can never flash while chapters are still loading

## v0.1.4 — 2026-05-08

- fix: "Clear All" chapters button now works — `Chapter` TS interface gains `isArchived`, sidebar and Chapters list filter archived chapters out so they disappear visually after archiving
- feat: archive/restore individual chapters — 🗄 button on each chapter card in the Chapters list and in ChapterView header; archived chapters appear in a collapsible "Show archived" section with ♻ Restore and 📜 History buttons
- fix: version history (📜) now available for archived characters and plot threads in their "Show archived" sections

## v0.1.3 — 2026-05-08

- fix: complete `[JsonIgnore]` coverage on all reverse/parent navigation properties — added `Book.User`, `AppUser.ApiToken`, `CharacterCardVersion.CharacterCard`, `PlotThreadVersion.PlotThread`; also marks `AppUser.ApiToken` non-serializable for security


- fix: concurrent RAG queries caused EF Core "second operation" and identity-map key conflicts — `PgvectorVectorStoreService` now creates a fresh `DbContext` per operation via `IDbContextFactory`; `WriterAgent` and `EditorAgent` RAG queries are now sequential to prevent concurrent `SaveChangesAsync` on the shared scoped `BookRepository`
- fix: added `[JsonIgnore]` to all reverse navigation properties (`Book`, `Chapter`, `User` references and collection navprops on `Book`/`Chapter`) to prevent infinite dependency trees during serialization; also hide `PasswordHash` on `AppUser`

## v0.1.1 — 2026-05-08

- fix: chapter list and book list in the sidebar now scroll with the panel instead of having their own nested scrollbars

## v0.1.0 — 2026-05-08

Initial public release.

- Multi-user agentic book-writing app with cookie-based authentication
- Four AI agent pipeline: Story Bible → Characters → Plot Threads → Chapter Outlines → Write → Edit → Continuity Check
- Pluggable LLM providers: Ollama, OpenAI, Azure OpenAI, Google AI Studio, Anthropic (via proxy)
- Per-book and global LLM configuration with credential presets
- Human-assisted mode: agents pause to ask clarifying questions before and between phases
- PostgreSQL storage with pgvector embeddings for RAG-based context retrieval
- Durable run persistence: agent runs survive server restarts
- Export: HTML, FB2, EPUB, and metadata HTML
- Per-item version history for characters, plot threads, chapters, and planning phases
- Token usage statistics per book and per agent
- MCP server embedded in API for external tool integration (Claude Desktop, VS Code Copilot)
- React SPA served as static files from ASP.NET Core wwwroot (single Docker container)
- Multi-platform Docker image (linux/amd64, linux/arm64)
- Soft-archive for agent messages and token records
- Anti-repetition system for Writer/Editor/Continuity Checker agents
