# ABook — Copilot Workspace Instructions

## Mandatory entrypoint

Before inspecting or changing the repository, read [`ORCHESTRATOR.md`](../ORCHESTRATOR.md). It defines the required task-classification, repository-entry, execution, validation, safety, and MCP-operation workflow for every orchestrator. Then read `AGENTS.md` for the detailed architecture and current implementation decisions.

Never skip the orchestrator entry protocol because a task appears small. Use the validation matrix for every subsystem touched, preserve pre-existing user changes, and stop/escalate only under the conditions defined there.

## Keep AGENTS.md Current

After every change to this codebase — new files, renamed files, schema changes, dependency decisions, architectural decisions, technical gotchas discovered — update `AGENTS.md` immediately to reflect the new state. Do not wait to be asked.

Specifically, keep these sections accurate:
- **TL;DR** — framework version, key technology choices
- **Data Model** — entity fields and relationships
- **Phases** — what each phase/step actually does (not the original plan)
- **Relevant Files** — actual file paths that exist, with accurate descriptions
- **Decisions** — the real decisions made, not aspirational ones
- **Implementation Notes (Technical Gotchas)** — any non-obvious API quirks, version pinning requirements, or patterns that differ from documentation

AGENTS.md is the authoritative source of truth for this project. It should always reflect the current implementation, not the original plan.

## Keep README.md Current

After every user-visible change — new features, removed features, changed behaviour, new configuration options, updated workflow — update `README.md` immediately.

Specifically, keep these sections accurate:
- **Features** — every end-user-visible capability listed
- **Agent Workflow** — the actual pipeline steps and interaction points
- **Configuration / Environment Variables** — all env vars and their defaults
- **LLM Providers** — supported providers and any notable per-provider behaviour
- **Tech Stack** — framework and library versions

README.md is the public-facing documentation. It must always reflect what the app actually does.

## EF Core Migrations

Always create Entity Framework Core migrations using the `dotnet ef` CLI tool — never by manually creating migration files. Run from the solution root:

```
dotnet ef migrations add <MigrationName> --project src/ABook.Infrastructure --startup-project src/ABook.Api
```

After adding a migration, verify it looks correct before applying it. Apply with:

```
dotnet ef database update --project src/ABook.Infrastructure --startup-project src/ABook.Api
```

## Fix Compilation Errors

After every change — whether to .NET (C#) or TypeScript/React code — check for and fix compilation errors before considering the task done.

- **.NET**: run `dotnet build` from the solution root; resolve all errors and warnings that relate to the change.
- **TypeScript**: run `npm run build` (or `npx tsc --noEmit`) inside `src/abook-ui`; resolve all type errors.

Do not leave a codebase in a broken state. If a change introduces errors in files outside the directly edited file, fix those too.

## Versioning

This project uses semantic versioning (`MAJOR.MINOR.PATCH`). The authoritative version lives in the `VERSION` file at the repo root.

**Before every commit that changes code or behaviour:**
1. Check whether the current `VERSION` value has already been pushed to the target base history (for example with `git log origin/main..HEAD -- VERSION`).
2. If that version is already published, increment the **patch** number (e.g. `0.1.3` → `0.1.4`) and create a matching `## v{version} — {date}` heading in `RELEASE_NOTES.md`.
3. If that version has **not** yet been published, reuse the current version and append the new change under its existing release-note heading rather than bumping again.
4. Documentation-only commits do not require a version bump.

**Only when the user explicitly instructs it:**
- Increment the **minor** number and reset patch to 0 (e.g. `0.1.4` → `0.2.0`) for new features or significant improvements.
- Increment the **major** number and reset minor+patch to 0 (e.g. `0.2.0` → `1.0.0`) for breaking changes or major milestones.

Do not bump the version or add a release note entry for changes that are not committed (e.g. work-in-progress edits within a single task).
