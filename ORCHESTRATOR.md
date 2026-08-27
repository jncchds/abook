# ABook Orchestrator Playbook

This is the **operational entrypoint** for any coding agent, autonomous orchestrator, or MCP client working on ABook.

Read this file before changing code or operating a book. `AGENTS.md` remains the detailed architecture/source-of-truth document; this playbook tells you **how to enter the repository, decide what to do, execute safely, validate, and stop**.

## 1. Authority and source-of-truth order

1. The user's explicit task and constraints define the goal.
2. Current code, tests, database schema, and runtime evidence define what the system actually does.
3. `AGENTS.md` defines architectural invariants and implementation decisions.
4. `README.md` defines supported user-facing behavior and deployment instructions.
5. `CLAUDE.md` and `.github/copilot-instructions.md` are client-specific summaries; they must not contradict the files above.

If documentation and implementation disagree, do not silently choose one. Inspect the code/runtime, determine intended behavior, fix the inconsistency, and update the relevant documentation in the same change.

## 2. Classify the task before doing work

Every task must be assigned one or more operating modes:

| Mode | Typical request | Primary surface |
|---|---|---|
| **READ-ONLY DIAGNOSIS** | audit, explain, bug hunt, production-readiness assessment | repo + tests + runtime, no writes |
| **CODE CHANGE** | fix bug, add behavior, refactor | `src/ABook.*`, tests, docs |
| **FRONTEND CHANGE** | React/UI/UX/API contract | `src/abook-ui` + relevant API surface |
| **AGENT ENGINE CHANGE** | planning/writing/checking/editor/streaming/prompt flow | `src/ABook.Agents` + runtime/tests |
| **MCP CHANGE** | tools, auth, ownership, run control | `src/ABook.Api/Mcp` + MCP tests |
| **DATABASE CHANGE** | entity/schema/index/migration | Core + Infrastructure + EF migration |
| **RUNTIME / DEPLOYMENT** | Docker, config, Data Protection, dependencies | Dockerfile/Compose/Program/docs |
| **BOOK OPERATION** | plan/write/edit/check a live book via MCP | `/mcp`, not repository files |
| **RELEASE / PR** | commit, version, release notes, pull request | Git + release metadata |

A task may span multiple modes. Apply the union of their safety and validation gates.

## 3. Repository-entry sequence

Before editing files, establish state:

```bash
git status --short --branch
git rev-parse --short HEAD
git remote -v
```

Then:

- preserve all pre-existing user changes;
- do not reset, clean, overwrite, or mass-format unrelated files;
- if the tree is dirty, identify which changes predate the current task;
- inspect the relevant implementation and existing tests before proposing a fix;
- for substantial or risky work, prefer an isolated worktree/branch and promote only a tested diff;
- search this repository's instructions before applying generic framework assumptions.

## 4. Define the change contract

Before implementation, be able to state:

- **Goal** — what observable behavior must change?
- **Scope** — which subsystem owns that behavior?
- **Invariants** — what must remain unchanged?
- **Evidence** — which tests/runtime checks prove success?
- **Rollback boundary** — which files, DB objects, volumes, config, or runtime resources are affected?

Prefer the smallest coherent change. Do not mix unrelated cleanup unless it blocks correctness, security, build health, or the requested goal.

## 5. Execution rules by subsystem

### Backend / API

Trace `Controller or MCP tool -> Core interface/model -> Infrastructure implementation -> tests`. Preserve authorization, ownership, persistence, and cancellation semantics across every entry point.

### Frontend

Treat `src/abook-ui/src/api.ts` as the client contract. If UI fields/actions depend on backend data, verify the API actually serializes those fields. Do not "fix" lint/type failures by deleting useful behavior until you confirm the path is genuinely dead.

### Agent engine

Trace the complete run:

`entry point -> AgentRunnerService -> AgentOrchestrator -> agent -> repository/notifier -> persisted run state -> SignalR/UI`

All LLM calls are streaming. Human-assisted pauses, cancellation, persisted run recovery, token accounting, partial-output salvage, archived-content exclusion, and snapshots/versioning are part of the contract.

### MCP

Treat MCP as a public authenticated API. Tool description, input schema, ownership rule, return value, and side effect must agree. Per-book operations must validate ownership. Message-level operations derive ownership through the message's `BookId`.

### Database

Never hand-write migration files. Change models/configuration first, then generate with `dotnet ef migrations add`. Inspect the generated migration and consider existing-row compatibility before applying it.

### Runtime / Docker

A compile is not a deployment test. Build the final multi-stage image and smoke the documented Compose path. Secrets, certificates, passwords, and private runtime files stay outside Git. Optional hardening must not accidentally break the documented quick start unless a breaking change is explicit.

## 6. Validation matrix

Run the gates that match **every** touched mode:

| Change surface | Minimum required gate |
|---|---|
| Documentation only | `git diff --check`; verify commands/paths against the repo |
| Backend/Core/Infrastructure | `dotnet test src/ABook.Tests/ABook.Tests.csproj` and build affected projects |
| Frontend | `npm run lint` + `npm run build` from `src/abook-ui` |
| Dependencies / lockfile | frontend gates + `npm audit --json`; prefer the Dockerfile's Node/npm toolchain if host lockfile behavior differs |
| MCP | backend tests + MCP safety regressions + authenticated `initialize`/`tools/list` smoke when runtime is available |
| Agent workflow | backend tests + targeted run/status/cancellation tests; use a bounded deterministic/mock LLM unless a real provider is required |
| Database/schema | backend tests + generated migration review + fresh-database startup where practical |
| Docker/runtime | full `docker build` + official `docker-compose.yml` config/smoke; preserve persistent volumes |
| Security/auth | test both allowed owner access and denied cross-user access |
| Release/PR | all applicable gates + `git diff --check` + secret/path scan + clean staged diff |

A gate failure is evidence. Determine whether it is caused by the change, pre-existing debt, environment/toolchain, or the test harness. Do not label a product bug from a broken harness, and do not dismiss a real failure as environment noise without evidence.

## 7. Invariants that must survive every change

- **Ownership:** books, chapters, messages, run state, and MCP controls remain user-scoped.
- **LLM config isolation:** effective reads may fall back `book -> user -> global`; writes update the exact intended scope and never mutate a fallback object accidentally.
- **Run-state truth:** only `Running` and `WaitingForInput` are active; terminal states must not be reported as running.
- **Cancellation parity:** REST and MCP operations that start runs use `AgentRunStateService.CreateRunCts(bookId)` so Stop behaves consistently.
- **Archived content isolation:** archived chapters/characters/plot threads are retained for history/restore only and must not enter prompts, RAG, exports, public output, or agent mutations.
- **Streaming:** agent LLM paths remain streaming-first; do not introduce a hidden non-streaming path without an explicit architectural decision.
- **Persistence:** user-visible generated/edited states are versioned or snapshotted according to existing subsystem rules.
- **Secrets:** never commit API keys, tokens, PFX/PEM/private keys, password files, `.env` secrets, or runtime-private directories.
- **Single runtime frontend:** React is built into ASP.NET `wwwroot`; do not introduce a second frontend runtime service accidentally.
- **Backward compatibility:** repository quick-start/deployment behavior remains valid unless the task explicitly authorizes a breaking change.

## 8. Stop / ask / escalate conditions

Stop and surface the issue instead of guessing when:

- the requested action would destroy or overwrite user data;
- two sources of truth disagree and code/runtime inspection cannot resolve intent;
- a migration would reinterpret existing data without a compatibility path;
- authentication/ownership semantics are ambiguous;
- a production secret would need to be invented, exposed, rotated, or committed;
- the task requires a provider/account/permission that is unavailable;
- a supposedly read-only task would require mutation to continue.

Do **not** stop merely because a task is large. Decompose it, use isolation, execute the safe portion, and report any remaining blocker precisely.

## 9. Completion definition

A repository task is complete only when:

1. the requested behavior is implemented or the diagnosis is evidenced;
2. applicable gates pass, or remaining failures are explicitly characterized with evidence;
3. docs reflect changed architecture or user behavior;
4. `git diff --check` is clean;
5. no secrets or unrelated files are included;
6. test resources are removed unless intentionally persistent;
7. commit/PR/release actions are performed only when requested or already part of the active workflow.

## 10. Book orchestration workflow via MCP

Use this section when the task is to operate ABook as a writing system rather than modify source code.

### MCP startup sequence

1. Authenticate to `/mcp` with the user's Bearer API token or authorized session.
2. `get_current_user` — establish caller identity.
3. `list_books` — locate the target; never guess a `bookId` from stale context.
4. `get_book` — read current metadata and planning-phase status.
5. `get_agent_status` — determine whether a run is active, waiting, failed, cancelled, or idle.
6. Read relevant state before mutation: `get_story_bible`, `list_characters`, `list_plot_threads`, `list_chapters`, `get_agent_messages`, and `get_token_usage` when cost matters.

### Decide the next operation from state

- **New/unplanned book:** `start_planning` by default. Use `start_workflow` only when autonomous plan+write is explicitly wanted.
- **Partially planned book:** `continue_planning`; completed phases are idempotently skipped.
- **Planned book with chapters left:** `start_workflow` or `continue_workflow`; already-Done chapters are skipped.
- **One chapter:** `write_chapter`, `edit_chapter`, or `run_continuity_check(bookId, chapterId)`.
- **Whole-manuscript review:** `run_continuity_check(bookId)` without a chapter id.
- **Waiting for input:** inspect unresolved Questions and use `answer_agent_question`; do not start a competing run to bypass the pause.
- **Need to abort:** `stop_workflow`, then re-read status before starting another run.

### Planning-first default for serious books

For a substantive novel or non-trivial book, prefer:

`premise -> Story Bible -> Characters -> Plot Threads -> Chapter Outlines -> human review -> chapter writing -> per-chapter checks/edits -> full continuity check`

Do not start Chapter 1 merely because a book exists. First confirm planning artifacts meet the user's quality bar. A user can deliberately choose a lighter workflow; the orchestrator must not silently skip planning.

### Human-in-the-loop

When status is `WaitingForInput`:

1. fetch `get_agent_messages`;
2. find the latest unresolved Question for that book;
3. answer only from information actually supplied by the user/project;
4. if a creative/user preference is unknown, surface it to the user instead of inventing canon;
5. call `answer_agent_question` and monitor status/messages until the run advances or terminates.

### Monitoring background runs

Long-running MCP tools dispatch background work. A successful tool response means **accepted/started**, not **finished**. Poll `get_agent_status` and inspect messages/book state. Treat `running=false` plus a terminal state as finished; never infer completion from elapsed time.

### MCP mutation rules

- read before write;
- update the smallest entity required;
- preserve IDs and ownership;
- do not modify archived entities through agent workflows;
- do not use user-level LLM setters when book-specific config is intended;
- do not run competing workflows on the same book;
- after any write/run, re-read affected state instead of assuming success.

## 11. Git, commit, version, and PR workflow

Do not commit, push, tag, or open a PR unless the user requested it or the active task already explicitly includes delivery through Git.

When delivery through Git is required:

1. fetch/inspect the target remote and base branch;
2. confirm the working tree contains only intended changes;
3. run all applicable validation gates;
4. run `git diff --check` and inspect the staged file list;
5. scan staged paths/content for secrets, private keys, password files, runtime-private directories, and accidental generated artifacts;
6. for a commit that changes code or behavior, inspect whether the current `VERSION` value has already been pushed to the target base history:
   - if the version is already published, bump PATCH and create/update the matching `RELEASE_NOTES.md` heading;
   - if the version has not yet been published, reuse it and append the change under its existing heading;
   - documentation-only commits do not require a version bump;
7. use the authenticated contributor identity, not placeholder Git metadata;
8. push to a feature branch/fork; never force-push shared history without explicit authorization;
9. open the PR against the intended upstream/base and include scope, compatibility notes, and test evidence;
10. after push, verify the PR head/base and confirm no local-only runtime secrets/resources were included.
