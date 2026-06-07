# Release Notes

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
