# Release Notes

## v0.1.19 — 2026-07-18

- fix: chapter view no longer shows checker/analysis output (e.g. "No issues found") as if it were chapter prose — narrowed live-stream token routing in `BookContext` to only Writer and Editor roles; ContinuityChecker tokens with a chapterId are now dropped from the chapter display buffer entirely, since that analysis belongs in the chat panel via SystemNote messages
- fix: hard-refresh no longer restores stale PreWriteCheck buffers into ChapterView — `useRestoreStream` now computes `activeRole` only for Writer/Editor roles when the running agent targets the currently-viewed chapter; previously ContinuityChecker status on a chapter caused `getStreamBuffer(bookId, "ContinuityChecker", chapterId)` to return cached analysis text and display it as chapter content

- editor: replaced fragile IndexOf-based patch matching with offset-aware normalization — `NormalizeForMatch` properly unifies CRLF→LF before trimming trailing whitespace; `BuildOffsetMapping` walks trimmed lines and builds a parallel index from normalized→original positions so offsets translate correctly even when \r\n shrinks the string
- editor: added post-apply verification (`VerifyPatchesApplied`) that re-runs match logic on patched content to catch phantom artifacts — residual originalText left by offset misalignment is now logged as Warning and reported in feedback instead of silently disappearing into the output
- checker: restructured ContinuityChecker prompts into four focused scan zones (Intra-chapter Consistency, Cross-chapter Continuity, Grammar & Mechanics, Repetition & Echoed Language) — each zone has its own precision requirements; Zone 1 issues are rewrite-type only, Zones 2-4 require verbatim patches with min 15-char originalText and required Position for disambiguation

- refactor: **full Semantic Kernel → MEAI migration** — replaced `Microsoft.SemanticKernel` with `Microsoft.Extensions.AI` (v10.*) throughout the agent engine and infrastructure layers; all agents now use `IChatClient` + `GetStreamingResponseAsync` instead of SK's `IChatCompletionService`
- refactor: extracted provider-specific chat clients into custom implementations — `OllamaApiClient`, `OpenAiChatClient`, `GoogleAiStudioChatClient`; each wraps its provider's API behind the same MEAI `IChatClient` interface
- refactor: replaced strategy pattern (`ILlmProviderStrategy`) with config-mapper pattern (`IProviderConfigMapper`) in `LlmProviderFactory`; cleaner DI mapping per provider
- refactor: removed all SK packages and pragmas (`SKEXP0070`, `Microsoft.SemanticKernel.*`); simplified csproj references to just `Microsoft.Extensions.AI 10.*` + `Microsoft.Extensions.AI.OpenAI 10.*`
- refactor: `AgentBase.StreamResponseAsync` signature changed — takes `LlmConfiguration` + `IEnumerable<ChatMessage>` instead of SK `Kernel` + `ChatHistory`; reasoning/thinking extraction unified under MEAI's `AdditionalProperties["ReasoningContent"]` surface
- feat: **LLM thinking/reasoning content captured and saved as agent messages** — supports both vendor metadata keys (`ReasoningContent`) from MEAI streaming updates AND inline `<think>…</think>` / `<thinking>…</thinking>` tags (DeepSeek-R1, Qwen3, etc.); merged into a single SystemNote with 💭 prefix; UI refreshes via `MessagesUpdated` SignalR event
- docs: updated README.md architecture diagram and tech stack table to reflect MEAI instead of Semantic Kernel
- docs: updated AGENTS.md Phase 3 description, decisions section, and relevant files list to match the MEAI refactor

## v0.1.18 — 2026-07-16

- llm: added execution parameters (Temperature, TimeoutMs, ReasoningEffort, MaxTokens) to LlmConfiguration and LlmPreset models; UI pages updated with controls for temperature slider, timeout input, reasoning effort selector, max tokens input
- ollama: fixed HttpClient timeout leak — OllamaApiClient now receives custom HttpClient with config.TimeoutMs applied (previously used .NET default 100s)
- migration: AddLlmExecutionParameters adds Temperature, TimeoutMs, ReasoningEffort, MaxTokens columns to LlmConfigurations and LlmPresets tables
- ui: Presets page loads presets on mount; added temperature/timeout/reasoning/max-tokens fields to preset forms (GlobalSettings, BookSettings, Presets)

## v0.1.17 — 2026-07-15

- fix: presets page no longer showed an empty list on mount; added missing `getPresets()` call so the Presets page loads user-owned + global presets from the server (same API as BookSettings/GlobalSettings)
- editor: patch-then-creative flow with new "rewrite" issue type for intra-chapter inconsistencies that need creative rewording instead of mechanical patches
- checker: INTRA-CHAPTER CONSISTENCY SCAN detects same-character contradictions across paragraphs (clothing shifts, timeline impossibilities) and flags them as rewrite issues
- writer: added intra-chapter consistency rules to prevent the LLM from introducing contradictions within a single chapter
- editor: position-first matching with ±3 line window; Position is now nullable since LLM-generated line numbers are unreliable
- ui: per-chapter "🗑 Clear" button in Chapters view (clears content, keeps outline); Book sidebar chapters now use circle badges visible when collapsed; global thin overlay scrollbar

## v0.1.16 — 2026-07-15

- ui: unified page headers across all pages with `.page-header` + `<h2>` pattern (replacing five separate styles); consistent title sizing and spacing everywhere
- ui: all archive/delete actions use unified red ghost style (`.btn-archive`) — Characters, Plot Threads, Chapters cards, ChatPage, Token Stats, ChapterView
- ui: ChatPage restructured to render as normal content; message cards use `book-list-card` layout with type-specific left-border accents; nav item renamed "Agent Messages"
- ui: New Book form integrated inline into Books page; preset form at top of page with model-loading functionality matching BookSettings LLM config
- ui: chapter status badge moved into action buttons bar below header; Clear Chapter and phase-action controls removed from Overview (archiving covers that)
- css: removed dead CSS rules — old header selectors, chat-panel rules, book-detail mobile layout, reader-content/reader-chapter-nav
- docs: completed Azure OpenAI removal from README — removed the provider row from LLM Providers table, updated feature bullet and env-var docs to only list Ollama/OpenAI/Google AI Studio; added "Removed providers" callout block explaining both Azure and LM Studio removals with migration paths (OpenAI + custom endpoint)
- docs: corrected `LlmProvider.LMStudio` note in AGENTS.md — enum value was actually deleted from `Enums.cs`, not just removed from the factory. Added warning that EF Core will fail on old DB rows with provider=4
- docs: updated three `OllamaController` references in AGENTS.md (Phase 2 controller list, Relevant Files file list, Ollama model management note) to point to `ModelsController` which absorbed this functionality
- docs: corrected ProcessChapterAsync workflow description in AGENTS.md — removed the "final informational Check" step since it was actually deleted from the code; patches are deterministic so no re-check is needed

- refactor: extracted shared MCP tool helpers into new `McpToolBase` abstract class; all four MCP tool classes (`UserMcpTools`, `AgentMcpTools`, `BookMcpTools`, `ContentMcpTools`) now inherit from it, eliminating ~40 lines of duplicated `CurrentUserId()` and `GetOwnedBookAsync()` / `EnsureBookOwnershipAsync()` boilerplate
- refactor: extracted `ControllerExtensions.RequireBookOwnershipAndLoadAsync` overload that returns both the ownership error and the loaded `Book` in a tuple; eliminates redundant re-fetches in `BooksController` actions like `Update` and `GetDefaultPrompts`
- refactor: refactored all 9 book-scoped ownership checks in `BooksController` to use `ControllerExtensions.RequireBookOwnershipAsync` / `RequireBookOwnershipAndLoadAsync` consistently, replacing inline `GetById + null-check + UserId comparison` blocks (~21 lines of boilerplate removed)
- fix: removed unused `HasPending(int bookId)` and `GetPendingTask(int bookId)` methods from `AgentRunStateService` — they had no callers
- fix: removed `StopWorkflowAsync` from `IAgentOrchestrator` interface and its implementation in `AgentOrchestrator`; the `AgentController.Stop` action calls `_runState.CancelRun(bookId)` directly, making the wrapper redundant
- fix: removed unused `GetRunStatusAsync(int bookId)` from `IAgentOrchestrator` — UI reads agent status via `AgentRunStateService.GetStatus` instead
- refactor: removed `AzureOpenAI` from the `LlmProvider` enum (was never implemented, only reserved for future use); updated `LlmProviderFactory`, `AGENTS.md`, README.md env-var docs, and frontend `PROVIDERS` list to match
- docs: synced documentation (`AGENTS.md`, `README.md`) with the AzureOpenAI removal from the provider enum

- fix: agent run crash recovery — `AgentController.RunInBackground` no longer silently drops exceptions (`_ = ex;` removed); unexpected errors now log at Error level and force a terminal in-memory status so the book stops appearing stuck on "Running" forever; added `TryRemoveRunId` to `AgentRunStateService` for cleanup
- fix: Ollama pull SSE endpoint — cancelled pulls now emit a Debug-level log (`Ollama pull cancelled by client`) instead of silently swallowing, making "pull appears to hang" issues debuggable
- fix: error surfacing reliability — `ReportErrorAsync` (AgentBase) and `ReportAgentErrorAsync` (AgentOrchestrator) both now log at Error level when DB persistence or SignalR notification fails; previously these nested try/catches swallowed everything silently so an agent could fail without the user ever knowing
- fix: chapter skip visibility — `ProcessChapterAsync` promotes "already Done" skip from LogDebug to LogInformation so it appears in production logs and operators can trace workflow execution
- fix: answer submission diagnostics — `ResumeWithAnswerAsync` now logs Warning-level messages when a submitted answer is silently dropped (null messageId or already-resolved message); previously these race conditions were invisible to debugging
- fix: embedding index failure logging — `WriterAgent.IndexChapterAsync` and `EditorAgent.IndexChapterAsync` now log at Warning level with exception details instead of `{ /* non-fatal */ }`; RAG degradation is now visible in logs
- security: vector search error no longer leaks raw exception message to client — returns generic `"Vector search unavailable."` instead; internal errors logged server-side at Error level
- refactor: extracted duplicated `CheckOwnershipAsync` (~20 lines × 5 controllers) into shared static `ControllerExtensions.RequireBookOwnershipAsync(this, bookId, _repo)` in new `Controllers/ControllerExtensions.cs`; removed the private method from StoryBibleController, CharactersController, ChaptersController, and PlotThreadsController
- refactor: extracted 4× repeated human-assisted pause block in `RunPlanningPipelineAsync` into private `ApplyHumanAssistedNoteAsync(...)` helper; also simplified by removing redundant `qaStr` variable in favour of direct `qaContext.ToString()` calls
- fix: null chapter fallback logging — `ProcessChapterAsync` now emits a Warning when `GetChapterAsync` returns null and falls back to cached data, making stale-state bugs detectable
- feat: checker catches four issue types — `continuity`, `grammar`, `repetition`, and `style` — each returned as a structured JSON patch with verbatim `originalText` (context-rich, min ~20 chars), `replacementText`, and an optional 1-indexed line-number hint for disambiguation
- feat: EditorAgent split into two focused methods — `ApplyPatchesAsync` handles checker patches mechanically (no LLM call) using IndexOf-first matching with whitespace normalization (`CRLF→LF`, trailing-space trim per line); `EditWithLlmAsync` handles manual edits / MCP tools via the creative LLM path
- feat: patch application uses end-first ordering so earlier replacements don't corrupt character offsets for later patches; position field used as a secondary hint only when IndexOf finds multiple matches on the same text, and only if the original text is confirmed present on that target line
- feat: editorial feedback message grouped by issue type (continuity / grammar / repetition / style) shows `original → replacement` inline plus description per fix; unapplied patches reported factually with specific skip reason (no verbatim text provided / text not found in chapter / ambiguous — multiple matches and position did not confirm)
- refactor: removed the Final Check step from `ProcessChapterAsync`; patches are deterministic so re-checking is unnecessary, failures surfacing in chat panel instead

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

## v0.1.19 — 2026-07-20

- feat: **new `LlmProvider.OpenAICompatible` provider** for any OpenAI-compatible endpoint (LMStudio, OpenRouter, Groq, Together, etc.) that isn't the official OpenAI API — uses a custom `OpenRouterHttpChatClient` that bypasses MEAI's SSE parser entirely, parsing streaming responses directly and surfacing both content tokens and reasoning/thinking tokens through MEAI's standard `AdditionalProperties["ReasoningContent"]` channel; this prevents thinking-only chunks (common with Nemotron, GPT-OSS on OpenRouter) from corrupting structured JSON output in planning agents
- feat: **OpenAI-compatible model listing** — `GET /api/models` now handles `OpenAICompatible` provider the same as OpenAI when an endpoint is supplied, fetching models from `{endpoint}/models`
- docs: updated README.md and AGENTS.md to document the new provider alongside Ollama, OpenAI, and GoogleAIStudio
