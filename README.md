# ABook — Agentic AI Book Writing

ABook is a self-hosted web application that uses AI agents to collaboratively write books. A team of four specialized agents — Planner, Writer, Editor, and Continuity Checker — work together under your direction, streaming their progress in real time and pausing to ask clarifying questions when needed.

## Features

- **Four collaborative agents** working through the book pipeline (Plan → Write → Edit → Continuity Check)
- **Human-in-the-loop** — agents pause and ask you plot/character questions before proceeding; Writer can emit `[ASK: question]` mid-generation to request input on pivotal decisions
- **Real-time streaming** — watch chapters being written token by token via SignalR
- **Token usage statistics** — per-agent prompt and completion token counts displayed in a collapsible panel and persisted to the database
- **RAG context retrieval** — agents query relevant prior chapters via Qdrant vector embeddings to stay consistent across long books
- **Pluggable LLM backend** — Ollama (default, local), LM Studio, OpenAI, Azure OpenAI, or Anthropic; configurable globally, per-user, or per-book
- **Per-book customization** — language, genre, and per-agent system prompt overrides with supported template tokens
- **Inline editing** — edit book metadata, chapter titles/outlines, and add chapters manually without leaving the detail page
- **Multi-user** — cookie-based authentication with admin role for user management
- **Ollama/LM Studio model management** — browse installed models, pull new ones with live progress
- **HTML export** — download finished books with 6 colour themes and adjustable font size
- **Fully containerized** — single `docker-compose up` starts everything

## Architecture

```
[React SPA — served as static files from ASP.NET wwwroot]
        ↕ REST API + SignalR
[ASP.NET Core 10 API]
        ↕ Semantic Kernel          ↕ EF Core          ↕ Qdrant .NET client
[LLM (Ollama/OpenAI/…)]    [PostgreSQL]           [Qdrant]
```

React is built at image-build time and served from `wwwroot/` — there is no separate frontend container.

## Quick Start

### Prerequisites

- [Docker](https://docs.docker.com/get-docker/) + [Docker Compose](https://docs.docker.com/compose/)
- [Ollama](https://ollama.com/) running on the host machine (or any OpenAI-compatible API)

### Run with Docker Compose

```bash
# Pull and start everything
docker-compose up -d

# App is available at
open http://localhost:5000
```

On first launch the app shows a **Create Admin Account** setup screen — the first account registered automatically becomes admin. After signing in, go to **Settings** to configure your LLM provider and pull an Ollama model.

### Run from Docker Hub

```bash
# Create a docker-compose.yml or run directly:
docker run -d \
  -p 5000:8080 \
  -e ConnectionStrings__DefaultConnection="Host=<postgres-host>;Port=5432;Database=abook;Username=abook;Password=abook" \
  -e Qdrant__Host=<qdrant-host> \
  -e Qdrant__Port=6334 \
  --add-host host.docker.internal:host-gateway \
  jncchds/abook:latest
```

> PostgreSQL and Qdrant must be reachable.  The compose file below starts them automatically.

<details>
<summary>Full docker-compose.yml</summary>

```yaml
services:
  abook-api:
    image: jncchds/abook:latest
    ports:
      - "5000:8080"
    environment:
      - ConnectionStrings__DefaultConnection=Host=postgres;Port=5432;Database=abook;Username=abook;Password=abook
      - Qdrant__Host=qdrant
      - Qdrant__Port=6334
      - ASPNETCORE_ENVIRONMENT=Production
    depends_on:
      postgres:
        condition: service_healthy
      qdrant:
        condition: service_started
    extra_hosts:
      - "host.docker.internal:host-gateway"
    restart: unless-stopped

  postgres:
    image: postgres:16-alpine
    environment:
      POSTGRES_DB: abook
      POSTGRES_USER: abook
      POSTGRES_PASSWORD: abook
    volumes:
      - postgres_data:/var/lib/postgresql/data
    healthcheck:
      test: ["CMD-SHELL", "pg_isready -U abook -d abook"]
      interval: 5s
      timeout: 5s
      retries: 10
    restart: unless-stopped

  qdrant:
    image: qdrant/qdrant:latest
    ports:
      - "6333:6333"
      - "6334:6334"
    volumes:
      - qdrant_data:/qdrant/storage
    restart: unless-stopped

volumes:
  postgres_data:
  qdrant_data:
```

</details>

## Configuration

### Environment Variables

| Variable | Default | Description |
|---|---|---|
| `ConnectionStrings__DefaultConnection` | — | PostgreSQL connection string |
| `Qdrant__Host` | `localhost` | Qdrant host |
| `Qdrant__Port` | `6334` | Qdrant gRPC port |
| `ASPNETCORE_ENVIRONMENT` | `Development` | `Production` disables Swagger |
| `LlmDefaults__Provider` | `Ollama` | Default LLM provider (`Ollama`, `LMStudio`, `OpenAI`, `AzureOpenAI`, `Anthropic`) |
| `LlmDefaults__ModelName` | `llama3` | Default model name |
| `LlmDefaults__Endpoint` | `http://host.docker.internal:11434` | Default LLM endpoint |
| `LlmDefaults__ApiKey` | — | API key for the default LLM provider (optional for Ollama/LMStudio) |
| `LlmDefaults__EmbeddingModelName` | — | Embedding model for RAG (optional; falls back to chat model) |

The `LlmDefaults__*` variables seed the global LLM configuration on startup. They can also be set in `appsettings.Local.json` (copy from `appsettings.Local.example.json`) for local development, or in `docker-compose.override.yml` for Docker-based local overrides.

### LLM Providers

Configure the LLM backend in the app's **Settings** page or via the API:

| Provider | Notes |
|---|---|
| **Ollama** | Default. Runs locally; `host.docker.internal` resolves to the host from inside Docker. |
| **LM Studio** | OpenAI-compatible local server. Default endpoint `http://host.docker.internal:1234`. API key defaults to `lm-studio`. |
| **OpenAI** | Provide API key and model name (e.g. `gpt-4o`). |
| **Azure OpenAI** | Provide endpoint, deployment name, and API key. |
| **Anthropic** | Provide API key and model name (e.g. `claude-3-5-sonnet`). |

Configurations can be set globally, per-user, or per-book. The lookup order is: book-specific → user-default → global.

## Agent Workflow

```
User creates book (title, premise, genre)
         │
         ▼
  [Planner Agent] — asks an initial guidance question, then generates chapter outlines
         │ chapter outlines
         ▼
  For each chapter:
  [Writer Agent]  ──asks─→ "How should X react to Y?"  (mid-generation [ASK:])
         │ draft prose
         ▼
  [Editor Agent]  ──asks─→ "This contradicts Ch2 — which version?"
         │ polished chapter + editorial notes
         ▼
  [Continuity Checker] — cross-chapter RAG analysis (per-chapter & full-book)
         │ inconsistency report
         ▼
      Done ✓
```

Agents stream tokens via SignalR as they write. **Write Book** runs the full pipeline automatically; individual stage buttons are also available on each chapter.

## Development

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- [Node.js 20+](https://nodejs.org/)
- Docker (for PostgreSQL + Qdrant)

### Local Setup

```bash
# Start supporting services
docker-compose up postgres qdrant -d

# Start the UI dev server (proxies API to localhost:5178)
cd src/abook-ui
npm install
npm run dev

# Start the API (in a second terminal)
cd src/ABook.Api
dotnet run
```

For local LLM configuration, copy `src/ABook.Api/appsettings.Local.example.json` to `appsettings.Local.json` and fill in your values. Alternatively, create a `docker-compose.override.yml` (see the committed example) to set `LlmDefaults__*` environment variables for Docker runs.

The React dev server runs at `http://localhost:5173` and proxies `/api` and `/hub` to the ASP.NET server at `http://localhost:5178`.

### Build Docker Image

```bash
docker build -t abook .
```

The multi-stage Dockerfile builds the React app (Node 20), compiles the .NET API (.NET 10 SDK), and produces a minimal runtime image (ASP.NET 10).

## Tech Stack

| Layer | Technology |
|---|---|
| Frontend | React 18, TypeScript, Vite, Zustand, react-markdown |
| Backend | ASP.NET Core 10, C#, Semantic Kernel |
| Database | PostgreSQL 16 via EF Core 10 + Npgsql |
| Vector DB | Qdrant |
| Real-time | SignalR |
| Auth | Cookie-based, `IPasswordHasher<T>` |
| Container | Docker, Docker Compose |

## License

[Apache-2.0 license](LICENSE)
