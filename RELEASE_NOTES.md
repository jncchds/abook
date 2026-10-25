# Release Notes

## v0.5.4 — 2026-10-25

- feat(telegram): **no more topics in the private chat** — the bot never opens topics itself. Tapping a book in `/books` (or creating one with `/newbook`) shows its card right there and **doesn't follow it anywhere**. Following is explicit: the book card's **🔗 Chats** lists the chats that follow the book (each with *disconnect*) and makes a one-time code for the access you pick — the same code as Book Settings → Telegram, with a link for this private chat, a link to add the bot to a group, and `/bind CODE` for a topic or channel. Turn topic mode off in @BotFather if it's on: with it, Telegram starts a new topic for every message you send outside one
- feat(telegram): **`/book` works in every chat that follows a book** — for the owner, and for everyone with **Participate** access there: they can view everything, edit the Story Bible, characters, plot threads and chapter outlines, answer clarifications and download; the book's settings, archiving, restoring versions and writing/checking chapters need **Control**, and only the owner changes the LLM preset or connects chats. Access is checked again on every tap. Replies to a card work in groups too
- feat(telegram): the book card has **⬇️ EPUB / FB2 / HTML / 🗂 Metadata** download buttons

- fix(telegram): a suggestion's **changes** no longer sit in a code block that scrolls sideways on a phone — each changed line wraps, added lines marked 🟢, removed ones 🔴 and struck through
- fix: **every edit of the book's content is a version** — editing a chapter's outline in a Telegram card or through the book chat now saves a chapter version (before it changed the chapter in place, so its version list didn't show it), and the Story Bible and book details keep the state you saved, not only the one before. To keep the lists short, an edit by you (web UI or Telegram) on top of your own last version **updates that version**; anything else in between (an agent, the book chat, a restore) starts a new one. The change log behind Rewind still keeps every save. An outline-only edit keeps the chapter's embeddings and summary instead of making them again
- feat(chapters): **insert, move and split chapters** — **＋ After** / **＋ Insert at the start** add a chapter anywhere, **↑ / ↓** move chapters, and **✂ Split** divides a chapter's outline between it and a new chapter right after it; both go back to Outlined to be written again (the old text stays in the first chapter's versions). Chapter numbers now follow the order (a chapter's place is stored separately from its number), and plot threads' and characters' chapter numbers move along. **Archived chapters keep their place**: *Show archived in place* lists them between their neighbours ("archived · was Ch. N"), and restoring puts them back there. Existing books: number gaps left by archived chapters are closed. Also over MCP (`create_chapter` with `afterChapterId`/`atStart`, `move_chapter`, `split_chapter`). Migration `AddChapterSortOrder`
- feat(chapters): **sections** — the plan is made of sections, each spanning one or more chapters (one by default). Every existing chapter became a section of its own, so books look the same until you use them. **＋ Chapter here** adds a chapter to a section and **✂ Split** now keeps both halves in one section; a section with several chapters shows as a box with its own overall plan (**✎ Plan**), moves, archives and comes back as a whole, and its chapters move within it or into the neighbouring section. A section with one chapter simply has that chapter's plan. Plot threads' "introduced/resolved in" and characters' "first appearance" count sections. The sidebar labels chapters "3" / "3.2" (section.chapter). New Book Settings switch **Show sections in the book**: downloads (HTML, EPUB, FB2) and the reader give each section a part heading ("I. Title"). Rewind covers sections. MCP: `list_sections`, `update_section`, `move_section`, `move_chapter_to_section`, `archive_section`, `restore_section`, and `create_chapter` takes `sameSection`/`sectionId`. Migration `AddSections`
- feat(planning): **planning works in sections** — Plan Book outlines sections (the target count is now a number of sections) and marks the ones that need more than one chapter. **⋔ Break down** (Chapters page, MCP `break_down_section`) has the planner divide an unwritten section into chapters — into a number you give, or as many as it needs (2–4); the section keeps its overall plan. Autonomous runs break down the marked sections on their own before writing them. The Writer gets a chapter's section plan and its sibling chapters, so each tells only its share. Migration `AddSectionPlannedChapters`
- feat(export): the **metadata** download starts with what each section holds **now** — Story Bible, clarifications (open first, archived folded), characters, plot threads (archived folded) and chapters (plan, status, words, summary) — with links to each section, and only then the full timeline

## v0.5.3 — 2026-10-25

- feat(book chat): **Anything else?** under every set of suggestions — write what else to change and tap **↻ Revise suggestions**: the co-author gets those suggestions in full together with your note and answers with a complete corrected list (keeping what was right, fixing the rest, adding what you asked), which replaces the open ones. In Telegram, reply to the suggestions (or their reply) with your note. MCP: `revise_book_chat_suggestions`. Migration `AddChatRevision`
- fix(book chat): **new messages show up without reloading** — a rejoin of the live connection that hung on a half-dead connection (mobile, proxies) blocked every later heartbeat, so nothing arrived until a page refresh; joins now time out and restart the connection, a lost restart is picked up by the heartbeat, and the chat is re-read every 15 s while the live connection is down (every minute while it is up, and at once when the tab is shown again). The studio header shows **⚠ Reconnecting…** while live updates are down
- fix(book chat): "Could not load the chat: Network Error" no longer sticks — a failed load is retried (5 s, doubling up to a minute, and at once when the device is back online or the tab is shown again) and the message goes away as soon as a load succeeds. After a reconnect that could not replay the missed events (e.g. a server restart) the chat is re-read too
- feat(book chat): **several chapter writes in one batch run one full cycle at a time** — the first chapter is written and checked; the next waits (status **Queued**) until every follow-up suggestion of that chapter (fixes, new characters, thread updates) is applied or rejected, then starts on its own, so each chapter builds on the reviewed one before it. Queued writes can be started early (**Start now**) or cancelled, on the web and in Telegram. Migration `AddProposalQueue`
- fix(telegram): a suggestion's **details** now show what the web UI's card shows — its kind and status, the instructions for Write/Revise, the change as the same line diff (+/− lines, unchanged stretches folded), the result, and an **Open ↗** link once applied. Before, write/revise suggestions showed nothing and other changes only loose field blocks

## v0.5.2 — 2026-10-24

### Telegram: your account and your books
- feat: **link your Telegram account** — Settings → Telegram → **Link Telegram account**, then open the one-time link (or send the `/start acc_…` command to the bot). One Telegram account per abook user; **Unlink** in Settings or `/unlink`
- feat: **create books in Telegram** — `/newbook` in the private chat with the bot is a wizard card: reply with the title and premise, pick genre, language, mode and chapter count with buttons (or type them), tap ✏️ to change anything, then **✅ Create**. The book gets its own topic and its book card. It needs your default LLM preset
- feat: **manage your books in the private chat** — `/books` lists them (tap one to open it in its own topic), `/book` in a book's topic opens its card: title, premise, genre, language, target chapters, mode, illustrations, the LLM preset, and the Story Bible, characters, plot threads, chapters and clarifications. Each is one message edited in place: ✏️ a field, **reply to the card** with the new text, then **💾 Save** (it warns if something changed meanwhile). Characters, threads and chapters can be added, archived and brought back; every part has **🕘 Versions** with restore; chapters show their outline (editable), **📄 File**, **✍️ Write** and **🔍 Check**. Book management is only in the private chat, for the owner
- feat: the owner, through the linked account, has **full rights on their books** in any connected chat
- feat: **suggestion details** — every suggestion line ends with a **details** link; tapping it posts what exactly the suggestion changes (field by field, was → now, the conflict it answers) with its own Apply / Reject
- feat: **rich messages** (Bot API 10.1) for the suggestions, their details, the cards, the status card, the rewind preview and `/help`: headings, tables, collapsible sections, links inside the text (each card field has its own *edit* link; list items are links), long fields shown in full, and times in the reader's own time zone. When a Bot API server doesn't support rich messages, the bot sends the same content as plain messages with the links as buttons

## v0.5.1 — 2026-10-24

- feat: **Telegram: pick several suggestions** — tick boxes on each suggestion, then **Apply ticked** / **Reject ticked** (one batch, like Apply selected on the web); **Apply all** / **Reject all** when nothing is ticked; one **Choose option A/B** button per alternative; ↻ Run again for steps. Or reply to the suggestions with their numbers: `1 3`, `apply 1, 3`, `reject 2`
- fix: the Telegram settings card (code, links, `/bind` command) and the prompt placeholders were white on white in the dark theme; each value now has **Copy** (works on plain-HTTP installs too) and links an **Open** button
- fix: the MCP setup instructions show this server's real address instead of `localhost:5000`, have **Copy** buttons, and put your API token into the copied config while it is shown
- fix: **Telegram: suggestions can be applied from chats with Participate access** (the default). Applying, rejecting and undoing book-chat suggestions was limited to Control, so in a newly connected chat the suggestions arrived with no buttons at all. Applying is part of using the book chat, so it now comes with Participate; Control keeps starting/stopping runs, `/rewind` and `/unbind`. Suggestion messages already in a chat get their buttons on the next sync; in a Read-only chat they say why there are none

## v0.5.0 — 2026-10-24

### Rewind
- feat: **rewind the whole book and its chat to an earlier moment** (book sidebar → ⏪ Rewind): pick a moment — before one of your chat messages, after a reply, after a chapter version, before/after a run — check the preview, confirm. Every part gets the state it had then **as a new version** (chapters, Story Bible, characters, plot threads, clarifications, title/premise/planning progress, book-chat suggestions), so everything in between stays in each part's history; parts made since are archived, parts deleted since come back. Settings and illustrations are not rewound
- feat: book-chat messages after the moment are **hidden** — collapsed in the chat, never sent to the LLM again — and the chat's memory goes back to what it was then. There is no fast-forward
- feat: rewind from **Telegram** — reply `/rewind` to a message (or pick a moment from buttons), confirm the preview; every connected thread gets a divider, rewound replies are crossed out, changed chapters come as new file versions. Control only, chat admins in groups. And over MCP: `list_rewind_points`, `preview_rewind`, `rewind_book`
- feat: a **change log** of every change to the book's content (whatever makes it) is what the rewind works from; it starts when this version first runs, so books can be rewound to any moment from then on

### Telegram bot
- feat: **follow and steer a book from Telegram.** One bot per server (`TELEGRAM_BOT_TOKEN`; `ABOOK_HOSTNAME` for a webhook, otherwise long polling). Connect a book from Book Settings → **Telegram** with a one-time code: a private chat (each book its own topic when topic mode is on in BotFather), a group, a forum topic (`/bind CODE`) or a channel
- feat: **access is the chat** — everyone who can post in a connected chat or topic gets the access chosen for it: **Read** (status, notices, files), **Participate** (default: also answer the planner's questions, `/export`, `/note`, the book chat) or **Control** (also start, continue and stop runs and apply book-chat changes; can be limited to the chat's admins). No abook accounts on the Telegram side; disconnect from Book Settings, with `/unbind`, or by removing the bot
- feat: a pinned **status card** edited live (step, chapters done, words written so far, tokens of the run) with Stop / Start / Continue, Refresh and Export buttons
- feat: the planner's **questions one at a time** — reply to the question message to answer, or Skip; plain messages never count as answers
- feat: **a file after each chapter edit** (EPUB, FB2 or HTML per chat), sent as a reply to the chapter's previous version (v2, v3…); manual edits are sent once they settle. Also the planning document when planning completes, the **whole book** when the last chapter is done, rendered **illustrations** as photos, and `/export` on demand
- feat: **live drafts** in private chats — the chapter as it is being written, with a stop button that stops the run (Control)
- feat: **Interactive books in Telegram** — write in the chat to talk to the book chat; replies arrive with their suggested changes as ✅ / ✖️ / ↩️ buttons (Apply all, Retry, confirm when something changed since)
- feat: answers, notes and chat messages sent from Telegram show **who sent them** (✈️ Telegram · @name · chat) in Agent Messages, Clarifications and the book chat; starting, stopping and connecting add a note to the book's messages
- feat: single-chapter export (`BookExportService.ExportChapterAsync`)
- feat: `TELEGRAM_API_URL` — use a self-hosted Bot API server (files up to 2 GB instead of 50 MB)
- feat: chapters edited by hand in the web UI or over MCP (and restored versions) reach the bot too

## v0.4.3 — 2026-10-23

- docs: README, AGENTS.md and CLAUDE.md checked against the code — Semantic Kernel is gone (direct provider clients), the OpenAICompatible provider is listed, the MCP tool count is 53, the workflow buttons are Plan Book / Write Book / Illustrate Book, and the book sub-pages list matches the routes (no `StatePage`)
- chore: removed the unused Semantic Kernel package (Core now references `Microsoft.Extensions.AI.Abstractions`, Infrastructure `Microsoft.Extensions.AI.OpenAI`) and the unused `handleContinue` / `continuePlanning` / `continueWorkflow` UI code
- feat: **re-run a step from the book chat** — ask the chat to check a chapter's outline, re-run the continuity check of a written chapter, or summarize a chapter again, and it offers a card (`check_outline`, `check_chapter`, `summarize_chapter`) with **▶ Run** / **↻ Run again**. The step changes nothing itself; its findings come back as proposals — a corrected outline, patches or a revision, new characters and plot-thread changes. Steps run after the other changes applied with them, and are skipped when the same apply already re-checked that chapter
- feat: **tick the book-chat suggestions you want** — when a reply has several cards, each has a checkbox; **Apply selected** / **Reject selected** act on the ticked ones (**Apply all** / **Reject all** when none are ticked). Alternatives (option A/B) are still chosen as a whole
- feat: **fixes to one chapter applied together are one edit** — several passage fixes of the same chapter, applied with Apply selected or Apply all, make a single chapter version instead of one per fix; undoing any of them undoes that edit (the others are marked undone too). A fix whose text is gone fails on its own without stopping the rest
- fix: patches that overlap each other are no longer applied on top of one another (which could garble the text) — the overlapping one is skipped

## v0.4.2 — 2026-10-23

- feat: **no live update is lost on a flaky connection** — every live event of a book carries a sequence number and a server session id, so the page knows exactly what it missed while disconnected and gets those events replayed when it rejoins (streamed text included) instead of re-reading everything; only after a server restart or a gap of more than 5,000 events / 10 minutes does it fall back to a full re-read. A **heartbeat** every 15 seconds (and when the tab or network comes back) restarts a connection that stopped answering and catches up on any event that went missing

## v0.4.1 — 2026-10-23

- fix: **Autonomous is the default mode** again when creating a book (and listed first)
- fix: **a new Interactive book waits for Start** — creating it (UI, REST or MCP) no longer starts the chat, so the premise and LLM preset can be checked first; the chat page links to both and has a **Start the book chat** button (MCP: `start_book_chat`)
- fix: **the book chat no longer flickers** — a sent message appears at once, and the streamed reply stays on screen (with "Preparing changes…" while the LLM writes the proposals) until the saved reply with its cards replaces it in place
- feat: **the book chat opens instantly** — the newest page of each book's chat (up to 50 messages) is kept in browser storage and shown right away, then replaced by the server's copy; earlier messages still load with "Load earlier messages". The stored chats are removed on sign-out
- feat: **next steps without asking** — applying a chapter's outline (a new chapter or an outline change) posts a **Write Chapter N** card at once, without an LLM call; for a chapter already written, a card to revise it to the new outline. Not offered when such a card is already open or the same apply wrote the chapter
- fix: **mobile: the page no longer scrolls as a whole** — the layout fits the visible screen (browser toolbars and the on-screen keyboard included) instead of `100vh`, so the header no longer slides away leaving an empty band below the UI
- fix: diffs of something new (a character, a fact) no longer show a red empty line removed; blank lines and empty fields are left out of the diff

## v0.4.0 — 2026-10-22

### ⚠️ Interactive books replace human-assisted mode
- ⚠️ **breaking: human-assisted mode is gone.** A book is now **Autonomous** (the planning pipeline and workflow, unchanged — minus the review pauses) or **Interactive**. Books that were human-assisted become Interactive; those that already have prose start with their premise and Story Bible locked. The section chat, its review pauses (after each planning phase, before each chapter and before each rewrite) and their REST/MCP endpoints are removed
- feat: **Interactive books are written in a chat.** Tell the LLM what you want — a character, a plot thread, a chapter, a change — in any order. It replies, flags **conflicts** with what the book already establishes, and proposes changes as cards (with a diff) that do nothing until you **Apply** them; Reject, Apply all, and **Undo** (restores what the change replaced; a later change to the same thing must be undone first). For a conflict it offers alternatives (option A: adapt the request, option B: change the book) — choosing one rejects the other
- feat: **no chapter count, no up-front outline** — chapters are added one at a time, always at the end (only the last chapter can be removed). `write_chapter` runs the Writer; afterwards the chapter is summarized and checked, and the follow-ups are posted as proposals: patches and a revision for the checker's issues, plus the characters and plot-thread changes the chapter introduced
- feat: **foundation and writing stages** — a new Interactive book opens with the LLM proposing a Story Bible from the premise and asking questions. The premise and Story Bible can be discussed until you press **Lock bible & start writing** (or apply the first chapter write); from then on they are fixed canon — the chat flags requests that contradict them instead of changing them, and the Story Bible page is read-only. Only you can unlock them, in Book Settings
- feat: **the chat stays cheap however long it gets** — each request sends a size-capped digest of the book (premise and notes, Story Bible, one line per character and thread, and what each chapter tells), a rolling summary of older messages, and only the last 6 messages, each cut to 1,500 characters; past replies are resent as their text plus what became of their proposals
- feat: **mobile-friendly** — the chat is a single column with proposal cards under each reply and the input pinned to the bottom. On phones the sidebar now starts collapsed (when you have not chosen yet) and, when expanded, overlays the page instead of squeezing it
- fix: a second edit of the same character or plot thread from a chat no longer fails on a duplicate version number (the old section chat had the same problem)
- REST: `api/books/{id}/book-chat` (`GET`, `POST`, `retry/{messageId}`, `kickoff`, `proposals/apply` (`{ ids, force }`, 409 with the stale ones), `proposals/reject`, `proposals/{id}/undo`, `lock-bible`, `unlock-bible`); `mode` on book create/update (target chapters optional for Interactive). Planning and workflow endpoints return 409 for Interactive books; editing a locked Story Bible or premise returns 409. SignalR `BookChatUpdated(bookId, messageId)` replaces `SectionChatUpdated`
- MCP: `get_book_chat`, `send_book_chat_message`, `start_book_chat`, `apply_book_chat_proposals`, `reject_book_chat_proposals`, `undo_book_chat_proposal`, `lock_story_bible` (they wait for the work to finish); `create_book` / `update_book` take `mode`. There is deliberately no MCP unlock

### Groundwork for Interactive books
- feat: **chapter summaries** — a new summarizer records what a written chapter actually tells (a short account plus the facts it establishes, with characters and plot-thread changes it introduced). Where a chapter has one, prompts use it for the "story so far" instead of the chapter's outline, which written prose may have drifted from. Token stats list the calls as `ChapterSummarizer`
- change: a book with target chapter count 0 is open-ended — prompts say "open-ended" and the Writer is no longer told "chapter N of 0"
- internal: `EditorAgent.PatchAsync` applies approved verbatim patches without changing the chapter's status and reports which applied

## v0.3.1 — 2026-10-19

### Human-assisted mode
- feat: **chat with the LLM about the section you are reviewing.** When a human-assisted run pauses — after the Story Bible, Characters, Plot Threads and Chapter Outlines, and before each chapter's creative rewrite — the section's page opens with a chat panel beside it. Ask questions or say what to change; the LLM sees the section as it was when the chat began, the whole conversation, and its current version, and answers or rewrites the section, which is saved straight away (with history: snapshots, versions and archived — not deleted — characters and threads) and shows on the page. Press **Complete** to continue the run. Replies stream live, Stop also stops a reply, and Complete waits until a reply has finished. Token stats list the calls as `SectionChat`
- feat: **a review pause before each chapter is written** — after the pre-write check, the chapter's page opens with the chat on its outline, so you can add details (or edit the outline yourself) before the Writer starts
- feat: **keep the decisions of a chat** — Complete asks the LLM for a short note of the decisions and preferences from the discussion; edit it and save it as a clarification (editable on the Clarifications page, read by every later prompt), or complete without one
- feat: **see and undo the chat's changes** — every reply that changed the section has **Show changes** (a line diff), and the latest change has **Undo**; undoing is itself a change, so it can be redone. Characters and plot threads that come back get their archived card back, portrait included
- MCP: `get_section_chat`, `send_section_chat_message` (waits for the reply), `undo_section_chat_change`, `summarize_section_chat`, `complete_section_review` (optional note and rewrite instructions)
- change: the "Want anything to add?" question after each planning phase is gone — the pause is now a review step (edit, chat, Complete), and nothing is saved as a clarification any more. Notes saved by earlier versions are kept. The pause before a chapter's rewrite still takes optional rewrite instructions on the Agent Messages page
- fix: edits made during a planning pause now reach the next phase — before, the next phase worked from the copy made before the pause
- REST: `GET/POST /api/books/{bookId}/section-chat/{questionMessageId}`, plus `/undo`, `/summary` and `/complete` (`{ note, answer }`); SignalR `SectionChatUpdated(bookId, questionMessageId, busy)`. `POST messages/answer` (and MCP `answer_agent_question`) return 409 while a chat reply is in progress

## v0.3.0 — 2026-10-01

### ⚠️ Breaking changes
- ⚠️ **breaking: sign-in moved from cookie sessions to JWT — everyone has to sign in once after updating.** The browser now holds a short-lived access token in memory and renews it with a rotating, revocable refresh token in an httpOnly cookie that is only sent to `/api/auth`; signing out (or an admin changing your password) ends the session for real. Books, presets and MCP API tokens are unaffected; the login page explains the one-time sign-in
  - New env vars: `Jwt__SigningKey` (32+ random characters), `Jwt__AccessTokenMinutes` (15), `Jwt__RefreshTokenDays` (30). **Nothing has to be added to update:** without `Jwt__SigningKey` ABook generates a key, stores it in the database and keeps using it, logging a ⚠️ warning at each startup and showing admins a banner. Move it into your configuration with `docker compose exec abook-api dotnet ABook.Api.dll secrets show`
  - REST clients: send `Authorization: Bearer <accessToken>` from `POST /api/auth/login` (or `/api/auth/refresh`); `/mcp` accepts that token or the MCP API token, no longer the session cookie
  - fix: the Docker image installs `libgssapi-krb5-2`, so the PostgreSQL driver no longer logs a `libgssapi_krb5.so.2` error on every start (and above the `secrets show` output)
- ⚠️ **breaking: no more global or per-user LLM configuration — every book has its own settings, and every user has a default preset.** A new book gets a copy of its owner's default preset (a sequel copies its base book); changing the preset or the default later never touches existing books. The update migrates automatically: each book that relied on the user or global configuration gets a copy of it, each user gets a private **Default** preset made from their old settings (or the global ones) as their default, and the old global/user rows are removed. `LlmDefaults__*` no longer configures anything at startup — it only seeds the "Default" preset of users created from now on
  - **Settings** now has a **Default LLM Preset** picker instead of the LLM form; edit a book's settings on its own Settings page, and manage presets (with **Make default**) on the Presets page
  - **Shared presets** (no owner) can be used by everyone, including as a default, but only admins can create, edit or delete them; admins share a preset with **Shared with everyone** (one-way). A user's private presets are now deleted with the user instead of turning shared
  - REST: `GET/PUT /api/configuration` require a `bookId` you own (before, any signed-in user could read any book's settings, API keys included). New `GET /api/presets/default` and `PUT /api/presets/{id}/default`
  - MCP: `get_llm_config` / `set_llm_config` act on your default preset, or on one book with `bookId` (a shared default is copied into a private "Default" preset rather than edited); `apply_preset` makes a preset your default, or copies it into one book with `bookId`; `list_presets` reports `isShared` / `isDefault`; `generate_book` refuses to start when you have no default preset
  - A book without settings (e.g. created while you had no default preset) stops with a message telling you to choose a preset on its Settings page

### Illustrations
- feat: consistent objects, clothes and places — the illustrator keeps a per-book registry of recurring visual assets (props, outfits, places, creatures, vehicles), registering each the first time an image shows it and repeating its description word for word in every later image; characters get a usual outfit and wear it unless the story dresses them differently, and each chapter is planned with the prompts of the images just before it so outfits and places carry over. Review and edit the registry on the Illustrations page or with MCP `list_visual_assets` / `update_visual_asset`; an edit updates every prompt that shows the asset. Use **Rewrite descriptions** once on existing books to split clothing out of the character descriptions
- feat: the base number of illustrations per chapter is a setting (**Illustrations per chapter** in the image settings and presets, default 2; MCP `imagesPerChapter`); the illustrator still plans more when a chapter genuinely needs them
- feat: downloaded books are much smaller — illustrations are re-encoded as JPEG (longest side up to 1600 px) in the HTML, EPUB, FB2 and metadata exports; the stored originals stay lossless
- feat: Unsloth Studio as an image provider — pick **Unsloth Studio** in the image settings, enter the Studio address and an `sk-unsloth-…` API key, and optionally an **Image model** that ABook loads when Studio has none loaded (Studio swaps out its text model by itself, so one Studio can serve both: use it as an **OpenAI Compatible** text provider at `…/v1`). Seeds, steps, CFG and negative prompts are all honoured, so character consistency works as with Z-Image. MCP `set_llm_config` gains `imageProvider`, `imageApiKey` and `imageModel`

### Exports
- feat: **Download Metadata** is now a chronological log of how the book was made — one timeline (grouped by day) of book and story-bible edits, generated and edited characters and plot threads, chapter outlines and versions (word counts and planning changes, never the prose), agent questions and answers, feedback, notes and folded reasoning, runs, workflow steps, every LLM call, and illustration plans and renders. An overview sums up LLM calls and tokens by agent and by model, and the 👁 button next to the font controls opens a list of categories to show or hide. The book text and the images shown in the book are left out; character portraits stay
- feat: the metadata export uses the full window width instead of a 980 px column
- feat: clarifications in the metadata export read as a dialogue — **Question** above the question, **Answer** above the answer — instead of a two-column table
- feat: the metadata timeline shows each clarification question as one entry — the question, then how you answered (or skipped) it and whether it was later edited or archived; questions imported from older chat rounds and notes after a planning phase replace their separate chat question and answer — and the overview lists the current clarifications. Any chat question answered from now on also appears as one entry with its answer (answers now record which question they answer; older ones are left as they were)

### Planning
- feat: **Clarifications page** — the planner's clarifying questions now appear together on the book's new Clarifications page (sidebar ❓) instead of one by one in Agent Messages; answer each in its own box or skip it, and submitting the last answer completes the clarification step and planning carries on. Answers are saved as you go, so nothing is lost if planning is stopped or the server restarts, and a completed round is not asked again (reopen it from the page to get fresh questions). Edit, add, archive or restore clarifications there at any time. Agent Messages keeps its answer box for the human-assisted pauses; a note you add after a planning phase is kept as a clarification too
- feat: clarifications are part of the premise wherever an agent uses it — all planning phases, the writer, editor, checker and illustrator prompts, custom system prompts using `{PREMISE}`, and the prior-book context of sequels — so decisions made in the round are never dropped, even if planning was interrupted. Existing answers from the chat are imported on upgrade. MCP gains `list_clarifications`, `add_clarification` and `update_clarification`

### Runs
- fix: refreshing the page (or losing the connection) during a run no longer loses track of it — the sidebar shows the step actually running (e.g. "Editing Ch. 3…" rather than the step the run started with), the latest progress message comes back, and the text streamed so far reappears on the Story Bible, Characters, Plot Threads, Chapters and chapter pages, with new text continuing from where it left off
- fix: the Chapters page no longer shows the **+ Add** button to viewers who can't edit the book

### Settings
- fix: loading the model list from an endpoint that rejects the API key no longer signs you out — the provider's 401 used to be passed through and mistaken for an expired session. Failures (wrong key, wrong URL, timeouts, replies that aren't a model list) no longer crash the request either, and the Presets and book Settings pages now show why the list couldn't be loaded

## v0.2.0 — 2026-09-30

### Illustrations
- feat: AI book illustrations with a self-hosted Z-Image server. With **Illustrate this book** on, the LLM plans each finished chapter's illustrations (what they show, the paragraph they follow, positive and negative prompts chosen for the genre) and the cover once the book is written; nothing is rendered until you press **Illustrate book**, which runs the image model alone so the LLM can be unloaded first (automatic for Ollama)
- feat: consistent characters — each character gets an LLM-written visual description, repeated word for word in every prompt that shows them, and a reference portrait whose seed is reused for the scenes centred on them; re-roll a portrait, then re-render that character's scenes
- feat: new Illustrations page, figures inline in the chapter view and public reader, portraits and editable visual descriptions on the Characters page; every plan is editable, and an edited image keeps showing until re-rendered
- feat: rendered illustrations and the cover are embedded in the HTML, EPUB and FB2 downloads; the metadata export shows portraits
- feat: image endpoint, visual style, steps, CFG, sizes and timeout are part of the LLM configuration and presets (with Test connection); MCP gains `list_illustrations`, `update_illustration`, `plan_illustrations` and `render_illustrations`
- feat: one style for the whole book — the illustrator writes an art direction once (style, a portrait composition shared by the whole cast, a book-wide negative prompt) that is added word for word to every image; editable on the Illustrations page
- feat: planned illustrations are saved one by one as the LLM streams them, so an interrupted planning run keeps what it already wrote
- change: the illustrator plans one or two images per chapter unless a chapter genuinely needs more
- change: illustrations never rely on negative prompts (Z-Image Turbo ignores them at CFG 1.0) — every request tells the LLM to make each image come out right from its prompt alone, and whether negatives take effect at the configured CFG
- change: the Illustrations page follows the workflow — a whole-book Plan → Render bar, then Art direction, Characters, Chapters and Cover, each with its plan and render buttons in the same place; the sidebar gains **Illustrate Book** next to Write Book and Plan Book

### Security
- fix: the agent endpoints (`/api/books/{id}/agent/*`) required no login and checked no ownership — anyone who could reach the server could start runs on any book (spending the owner's API credits), stop them, or read their stream. They now require sign-in and book ownership
- fix: the SignalR hub required no login and let any connection join any book's group, receiving its full streamed text. It now requires sign-in and `JoinBook` checks ownership
- fix: `GET /api/models` and `POST /api/ollama/pull` were anonymous (an open proxy to caller-supplied URLs, and unauthenticated multi-GB downloads). Both now require sign-in; the model-list API key moved from the URL (`?apiKey=`, which lands in access logs) to an `X-Api-Key` header
- fix: MCP `stop_workflow`, `answer_agent_question` and `get_agent_status` did not check book ownership

### Stability
- fix: double-clicking a start button (or a UI and an MCP client starting together) could launch the run, then mark the *running* run Failed, dispose its cancellation token (Stop stopped working) and let a third run start. Runs now start only through `AgentRunnerService.TryStart`, which claims the book, the concurrency slot and the cancellation token in one locked step
- fix: a server restart while an agent was waiting for an answer locked the book for good — the answer resolved nothing, every new run was refused as "already in progress", and the next restart repeated it. Such runs are now closed out like interrupted running ones: the question is resolved and a chat note says to use Continue
- fix: Stop now always frees a book — a status left active with no live run is forced to Cancelled
- fix: runs started over MCP had no cancellation token, so Stop did nothing for them
- fix: an LLM stream that stalled mid-response hung the run and its concurrency slot forever (`HttpClient.Timeout` only covers the response headers, and the OpenAI/Google clients ignore `TimeoutMs`). An idle timeout between chunks now fails the call (`TimeoutMs`, default 5 minutes)
- fix: a SignalR failure while reporting a run's outcome could mark finished work as Failed or skip the error message; terminal notifications are now best-effort
- fix: the stream buffer was read by the refresh-restore endpoint while the agent appended to it (a `StringBuilder` is not thread-safe); it is now locked. The Editor's stop-at-notes check scans only the last lines instead of the whole chapter on every chunk
- fix: the Ollama and OpenAI-compatible clients created a new connection pool per call; they now share one
- feat: anonymous `GET /health` reports database connectivity

### Streaming & chat
- fix: reasoning models' inline `<think>` text was streamed into the chapter / planning previews (and could put phantom items in the live planning preview); thinking is now never streamed. Thinking — inline or from the provider's reasoning channel — is saved as one "💭 Thinking" chat message when each call ends, now also when it fails, times out or is stopped
- fix: question-gathering output was streamed into the chapter-outline preview; question gathering and the Continuity Checker no longer stream (only book content does)
- fix: after any network blip the UI silently stopped receiving live updates until a page refresh (a SignalR reconnect loses group membership). It now rejoins, retries the initial connection, and re-reads messages, chapters, token stats and run status
- fix: answering a question on one device left the question box open on every other tab/device; answers now broadcast and the other tabs clear it. Out-of-order message refreshes can no longer overwrite newer messages
- fix: the planner's question overview stayed in the chat forever as an unanswered question; it is now an informational note
- fix: a failed answer submission was silently swallowed; it is now shown with the text kept for retry. **Skip** sent whatever had been typed instead of an empty answer
- fix: "Continuity Checker" chat notes and prompts contained garbled characters (`â€”`, `â„¹ï¸`) from a double-encoded source file

### UI
- fix: "+ Add" on the Chapters page did nothing — the add-chapter form was lost in the header redesign; restored. The chapter "↺ Clear" button, dropped in the same redesign, is back
- feat: a render error on one page shows a message instead of blanking the whole app; an expired session now signs you out instead of every request failing silently
- fix: the public reader no longer tries to show planning notes (POV/foreshadowing/payoff) the public API never returns
- fix: all 22 existing TypeScript errors; CI now type-checks the UI (the Docker build does not)

### Prompts
- refactor: every LLM-facing string now lives in `AgentPrompts.cs` — `DefaultPrompts`, new `PromptTemplates` (fixed system prompts, all user-message templates and context blocks) and `RagQueries`. The prompt text itself is unchanged. The repository returns ancestor data instead of formatting it into prompt text; the Writer/Editor RAG retrieval was de-duplicated into `AgentBase.GetChapterRagAsync`
- fix: "Load Defaults" in Book Settings filled in a stale, hand-written copy of the prompts (its checker prompt asked for a JSON shape the parser doesn't read, breaking checks for books that used it). It now returns the real default templates
- fix: MCP `run_continuity_check` ignored its `chapterId`
- refactor: removed the unused `EditorSurgical` prompt; run states are `RunStates` constants instead of string literals

## v0.1.23 — 2026-08-25

- fix: planning agents no longer abort the whole run when an OpenAI-compatible endpoint (LM Studio, Ollama, vLLM) ignores the JSON schema types. Chapter numbers returned as `1.0`, `"3"` or `"Chapter 4"` are coerced instead of throwing `System.FormatException`, and text fields returned as arrays or numbers are flattened instead of throwing. Shared `LenientJson` reader used by `StoryBibleAgent`, `CharactersAgent`, `PlotThreadsAgent` and `PlannerAgent`
- feat: planning failures are self-describing — the chat message names the agent and quotes what the model actually returned instead of showing a bare `System.FormatException`. New `PlanningParse` helper wraps every `JsonDocument.Parse` in the planning agents, so "no JSON", "invalid JSON" and "root was not an object" all arrive with a 300-character snippet of the response
- feat: a single malformed entry no longer aborts a planning run — each array element is parsed on its own, unusable ones are dropped with a reason (index, cause, and the offending JSON), and the run continues. On success the agent posts a `⚠️` note listing what it dropped; when nothing survives, the error lists why each entry was rejected
- feat: `AgentBase.ReportNoteAsync` posts a non-fatal `⚠️` SystemNote — used for drops that did not fail the run, so they no longer have to masquerade as errors
- fix: keep-partial and Story Bible failure notes append the underlying `Reason:` instead of replacing it with generic advice ("Try again or simplify the premise")

## v0.1.22 — 2026-08-06

- feat: in human-assisted mode the per-chapter pause moved to where it is useful — after the Editor applies the mechanical patches, before the creative rewrite. The author now reads already-corrected prose instead of guessing what the Checker will find
- feat: the creative rewrite pass only runs when the Checker raised `rewrite` issues or the author typed instructions during the pause — a clean chapter no longer costs a full-chapter LLM call
- feat: author instructions from the pause are passed to the rewrite as a highest-priority block, and the rewrite re-reads the chapter from the DB, so edits made while the run was paused are the text being rewritten
- feat: nothing is locked while an agent waits for an answer — the Overview, Chapters and Chapter views keep their edit, clear and archive controls, and both the chat panel and the chapter edit form say so
- feat: chapter **content** is now editable from the UI (the edit form was title + outline only)
- fix: hand-edited chapters no longer disappear from RAG — a manual edit creates a new active version, and `SearchAsync` only reads the active version's chunks, so the chapter had no chunks at all. Author edits and re-activated versions are now embedded in the background via the new `ChapterIndexingService`
- fix: `ChapterVersion.HasEmbeddings` is actually set now — it was written as `false` at every call site and never updated, so the version history's indicator was always wrong
- refactor: extracted the embedding routine into `ChapterIndexer.IndexVersionAsync`, shared by the agents and the API; `EditorAgent` split into `ApplyMechanicalFixesAsync` and `RewriteAsync` so the orchestrator can pause between them
- refactor: dropped the unused `humanNotes` parameter from `ContinuityCheckerAgent.CheckAsync` — author guidance now reaches the Editor, not the Checker
- feat: new `ToggleField` switch component replaces the bare native checkbox for "Human-assisted generation" on the new-book form, book settings and the book overview

- feat: LLM calls that are cancelled or time out now record their token usage instead of vanishing — previously `OperationCanceledException` rethrew before anything was persisted
- feat: added `TokenUsageRecord.FailureReason` (migration `AddTokenUsageFailureReason`) storing why a call failed — `Cancelled by user`, `Timed out after {n}ms`, HTTP status codes, or exception type + message
- feat: Token Stats page gained a **Status** column (`❌ Failed` / `⏹ Cancelled`) with the failure reason shown inline beneath the row
- feat: failed calls now push over SignalR, so the Token Stats page updates live instead of only after a manual refresh
- fix: embedding failures in `GetRagContextAsync` and `IndexChapterAsync` now record a failed `Embedder` row (previously swallowed with no accounting)
- refactor: consolidated the five duplicated token-usage write sites in `AgentBase` into a single `RecordUsageAsync` helper
- fix: replaced the mojibake character in the Token Stats "Archive" button label
- feat: the three JSON planning agents (Characters, Plot Threads, Chapter Outlines) now keep whatever finished streaming when a run fails — the salvaged items are merged into the existing set, versioned, and snapshotted as `agent-partial`, so the content the author already watched appear on screen survives a timeout, a dropped connection, or Stop
- feat: added `PartialJson` — recovers the complete elements of a truncated JSON array (string/escape aware, skips reasoning tags and bracketed prose) and reports whether the array was ever closed
- feat: a re-run of any of the three agents now sends the already-saved (non-archived) characters, plot threads, or chapter outlines back to the LLM and asks it to refine and extend that set instead of starting from scratch
- fix: archived characters, plot threads, and chapters are no longer deleted by an agent re-run or a snapshot restore — `DeleteCharacterCardsAsync`, `DeletePlotThreadsAsync`, and `ReplaceChaptersAsync` now only touch active rows
- fix: a re-plan no longer wipes written prose — `ReplaceChaptersAsync` updates chapters matching by number in place, keeping their content, status, versions, and message links
- fix: an empty or cut-off planning response no longer wipes the book's existing characters, plot threads, or outlines; it is reported as a failure with whatever was salvaged kept
- test: 37 new unit tests covering partial-JSON salvage and the three planning parsers (103 total)
- fix: archived chapters no longer reach the planning pipeline or the Continuity Checker — both now load the book via the new `GetByIdWithActiveDetailsAsync`, whose chapter collection excludes archived rows (`GetByIdWithDetailsAsync` still returns everything, for the UI's archived sections)
- fix: archived chapters no longer appear in HTML, FB2, EPUB, or metadata exports, nor in the public book endpoint
- fix: Writer, Editor, and the pre-write Continuity Check now refuse to run on an archived chapter (`AgentBase.EnsureNotArchived`); the `write`/`edit` REST endpoints and the `write_chapter`/`edit_chapter` MCP tools reject the request up front instead of failing inside a fire-and-forget run
- fix: the `update_chapter`, `update_character`, and `update_plot_thread` MCP tools now refuse to modify archived rows
- test: 6 new unit tests pinning archived exclusion in the four export generators and the agent guard (109 total)

## v0.1.21 — 2026-07-25

- test: added `ABook.Tests` xunit project with 66 unit tests covering `EditorAgent` patch-application logic, `AgentBase` static helpers, `ContinuityCheckerAgent` report formatting, `TextChunker` chunking, and `BookExportService` Markdown/filename utilities
- ci: added `test` job to GitHub Actions workflow that runs unit tests before the Docker build

- fix: MCP `get_chapter` now rejects archived chapters so external LLM agents cannot use archived content for writing
- fix: removed redundant in-memory `IsArchived` filter in `BuildChapterSynopsesAsync` (DB query already excludes archived chapters)

- refactor: extracted `refreshTokenStats` callback in `BookContext` to eliminate duplicated token-stat mapping logic
- refactor: extracted `startAgentRun` helper in `BookContext` to collapse identical `handleWriteBook`/`handlePlanBook` bodies
- refactor: `isPhaseComplete` now reuses `phaseStatusKey` map instead of repeating the same switch
- refactor: `ReportAgentErrorAsync` in `AgentOrchestrator` now delegates to `AgentBase.ReportAgentErrorCoreAsync` — shared static core, no duplicate logic
- refactor: extracted `GetPlanningSkipFlags` helper in `AgentOrchestrator`; collapsed `StartPlanningAsync`/`ContinuePlanningAsync` into one private method
- refactor: extracted `FormatCharacterCard` helper in `ContinuityCheckerAgent` — used in both `BuildEstablishedFactsAsync` and `BuildStructuredContextAsync`
- perf: `GetAncestryBookIdsAsync` now uses a single recursive CTE instead of one query per ancestor
- perf: `CharactersAgent` and `PlotThreadsAgent` now batch-insert cards/threads and their initial versions in two `SaveChangesAsync` calls instead of 2N
- infra: added `AddCharacterCardsBatchAsync`, `AddCharacterVersionsBatchAsync`, `AddPlotThreadsBatchAsync`, `AddPlotThreadVersionsBatchAsync` to `IBookRepository`
- api: `getStreamBuffer`, `getModels`, `getCharacters`, `getPlotThreads` now use axios `params` consistently instead of manual `URLSearchParams`/string concatenation
- types: merged `CharactersSnapshotMeta` and `PlotThreadsSnapshotMeta` into `ContentSnapshotMeta`; old names kept as deprecated type aliases
- types: extracted `LlmSettings` base interface shared by `LlmConfig` and `LlmPreset`

## v0.1.20 — 2026-07-24

- infra: removed Semantic Kernel entirely; replaced with direct SDK calls per provider (OllamaSharp for Ollama, OpenAI SDK for OpenAI/GoogleAIStudio, custom SSE reader for OpenAICompatible)
- infra: introduced `OpenAICompatible` provider type (LMStudio, OpenRouter, vLLM, etc.) — streams raw SSE, captures both `content` and `reasoning_content` deltas, does not send `reasoning_effort`
- infra: new thin `ILlmChatClient` / `LlmStreamChunk` abstractions replace all SK types across agents and infrastructure
- infra: Ollama thinking tokens now sourced from native `Message.Thinking` field (via `Think = true`); JSON schema sent as native `Format` parameter
- infra: GoogleAIStudio now uses OpenAI SDK directly against Google's compat endpoint (`/v1beta/openai`) — no separate Google connector
- ui: `OpenAI Compatible` added to all provider dropdowns; `Reasoning effort` field hidden for `OpenAICompatible` (not sent by that provider); provider names now display human-readable labels
- fix: model list loading now works for OpenAI and OpenAICompatible providers; ModelsController handles OpenAICompatible via `/models` with optional Bearer token
- fix: API key is now optional for OpenAI and OpenAICompatible providers (useful for local/proxied endpoints)

## v0.1.19 — 2026-07-24

- fix: checker and editor messages now appear in the chat panel in real-time after each agent finishes; `StatusChanged("Done")` for Writer/Editor/ContinuityChecker now calls `refreshMessages()` alongside the chapter reload
- ui: agent message cards now always render as collapsible — first line is the summary header, details expand on click; `BuildEditorialFeedback` header changed to plain-text `✏️ Editorial Notes — N applied, M skipped` (also fixes malformed `)` when no skips); thinking header simplified to `💭 Thinking`
- fix: chapter view no longer shows ContinuityChecker streaming output (pre-write or post-write analysis) in place of chapter content; checker tokens are now discarded on the frontend (the analysis is saved to the chat panel as a SystemNote); also fixes hard-refresh restore incorrectly pulling checker output into the chapter area
- checker: chapter content is now sent to the LLM with `N | ` line-number prefixes so position hints are read directly from the source rather than estimated; `originalText` instructions strengthened to require 30+ char context and character-for-character verbatim copying
- editor: mechanical patch matching upgraded to a two-pass strategy (exact → smart-quote normalization); position window widened from ±3 to ±5 lines; ambiguous multi-matches resolved by closest-offset heuristic instead of being silently dropped; content pre-normalized before patching so apply-loop offsets are always valid
- CLAUDE.md: added workflow rules for release notes on every commit, date verification on the current version heading, no running outside Docker, and fixing compilation warnings

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
