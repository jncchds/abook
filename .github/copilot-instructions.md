# ABook — Copilot Workspace Instructions

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
