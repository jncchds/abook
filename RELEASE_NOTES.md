# Release Notes

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
