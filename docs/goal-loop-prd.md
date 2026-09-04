# Goal loops (manager-driven iterative image making) — PRD + implementation

Status: **implemented 2026-09-04** in the `--ui` web app. This document is the
durable statement of the requirement, the settled decisions, and where each
part lives in code.

## 1. What it is

The original use case is one shot: type an image description, pick targets,
look at the output. The goal loop is the second use case:

1. The user enters a **goal** — the outcome they want, not a prompt.
2. The user picks exactly **one image generator** (any image target from the
   composer catalog) and exactly **one manager model** (a text+vision LLM).
3. The manager receives the goal and answers with **two** image designs
   (protocol 4): a **refine** prompt (its primary design) and a **fresh**
   prompt (a clearly different from-scratch approach), each complete, plus
   its reasoning.
4. The server renders both prompts with the chosen generator, concurrently.
5. Both rendered images (or the generator's error for either) go back to
   the manager, in the **same growing conversation**, together with the turn
   number and remaining budget.
6. The manager scores each render 0–10 against the goal, explains what it
   sees, names which of the two renders the lineage **continues from**, and
   either sends the next pair — a refine of the chosen render and a new
   from-scratch attempt informed by everything learned — (steps 4–6 repeat)
   or declares the loop done and names the best turn and render.

Every step is recorded and shown live on a dedicated page. The loop can be
stopped, resumed, granted more turns, and forked from any message — including
after editing the text of that message.

## 2. Requirements (user-specified 2026-09-04)

| # | Requirement | Behavior |
|---|-------------|----------|
| R1 | Goal text in, one generator, one manager | New-loop form on `goal.html`. Both selectors accept exactly one choice; unavailable targets are listed disabled with their exact availability problem. |
| R2 | Manager stays in one long conversation | Every manager call replays the full conversation rebuilt from the loop's entries (system prompt, goal, each earlier design/review reply, each review request with its image). |
| R3 | Manager iterates until the goal is met | System prompt instructs deliberate iteration, learning what the generator responds to, returning to earlier directions, and stopping when done or when further renders are unlikely to help. |
| R4 | Dedicated live-updating page | `goal.html` polls the selected loop every second and appends turns as they happen. Head shows status, activity, score, best turn, spend. The header's **main page** chip and the `MultiImageClient` title both link to `./` (the composer and job feed), matching the composer's **goal loops** chip. |
| R5 | Who sent what to whom, visibly | Each entry carries `from → to` party pills (user, manager, generator, system) plus the entry kind and timing. |
| R6 | Click reveals everything sent and received | Manager entries have a `sent / returned` disclosure with the exact wire request (image bytes replaced by placeholders) and the raw provider response. Review requests show the exact text, the attached original's dimensions/MIME/bytes, and how it travelled (`transport`); each manager entry shows the exact request byte count and, per image, the conformance label inside the stored request. Embedded JSON strings in payloads are shown expanded for reading with the verbatim text beneath. |
| R7 | Full image plus all metadata | Render results show the image, the generator's display name, returned pixel size, cost, job link, and error recovery hint when rendering failed. |
| R8 | Full score, thinking, and next-step considerations | Review entries show the 0–10 score (large), goal-met flag, assessment, problems, keep list, the manager's reasoning, the provider-side reasoning/thinking when the provider returns it, design notes, decision, and next prompt. |
| R9 | Vertical readable series of turns | Entries are grouped under turn headings, newest at the bottom, each turn one bordered column. |
| R10 | Stop / resume / resume from here | Stop marks the loop `stopped` and cancels in-flight work at the next boundary. Resume continues from the effective tail. Fork from any entry creates a child loop that continues from that point. |
| R11 | Edit text inside a turn, fork after modifying | Editable entries (goal, design, render request, review request, review) have an `edit + fork` control. The fork copies entries up to that point, replaces the text, drops everything derived from the old text, and runs from there. |
| R12 | Default 6 turns, settable at initiation | `maxTurns` defaults to 6, range 1–30, set on the new-loop form; resume may grant more turns. A "turn" is one manager design cycle: one render before protocol 4, the refine + fresh pair from protocol 4. |
| R13 | Manager is not told the generator identity | The system prompt says the generator's identity is withheld. No generator name, label, or model appears in any manager-bound text. |
| R14 | Manager rates and explains whether to try again | Required `evaluation` object on every reply after a render; `decision` is `render` or `done` with the reason in `reasoning`/`doneStatement`. |
| R15 (2026-09-04) | "As many as possible" must not stop early | Protocol 3 goal kinds. An open-ended goal may end only after a render pushed past the best result and degraded; the turn budget is never a reason to stop; the server objects to a premature `done` and re-asks with `done` barred. See section 3, "Goal kinds". |
| R16 | Same viewer, page-independent | `viewer.js` is a standalone viewer module with a source-parameterized interface (`MultiImageViewer.create({ items, resolveUrl })`). The goal page walks every render of the selected loop with it: preview-first atomic paint, ±10 preloading over 6 fetch slots, arrow/wheel/side-button/Home/End navigation, `f` fullscreen, `?` help, Esc/click-outside close. |
| R17 | Reuse the existing identity | The page shows the composer's creating-as name (authenticated profile display name first, else the canonical personal configuration document's `creatingAs`, else the legacy mirror key) as a read-only chip. A name input appears only when none exists; what it collects is written back to the canonical document. |
| R18 | See the prompt changes turn to turn | Word-level LCS diff (`ins`/`del`, `+N −M words` summary, plain-text toggle) on every render request against the previous rendered prompt, and on every review's next prompt against the prompt just rendered. |
| R19 | One image with every step | **build all-turns contact sheet** on the loop head renders one PNG: header band (goal, generator, manager, kind, best turn, status) above a square grid of every rendered turn with its score, `turn N of M`, pixel size, the exact prompt, and the manager's assessment/problems. Rebuild is offered when entries were added since. |
| R20 (2026-09-04, later) | Leave the rut: two renders per turn | The user observed loops iterating on one image, each turn a small edit of the last prompt, never leaving a weak composition. Protocol 4: every turn renders a **refine** prompt (an improvement of the render the manager chose to continue from; on turn 1 the primary design) **and** a **fresh** prompt (a from-scratch re-attempt at the goal: new composition, staging, camera, medium/style, palette — a different way to convey the same point, written with what has been learned so far). The manager scores both and sets `continueFrom` to the render its next refine builds on, so the lineage can jump to the fresh image at any turn. See section 3, "Protocol version 4". |

## 3. Settled decisions

- **Strict JSON protocol, fail closed.** Every manager reply must be exactly
  one JSON object (`reasoning`, `evaluation`, `decision`, `prompt`,
  `designNotes`, `doneStatement`, `bestTurn`). A markdown fence is stripped;
  nothing else is tolerated. Prose, a missing prompt on `render`, a missing
  `doneStatement` on `done`, an evaluation on the first reply, or a missing
  evaluation after a render is a hard per-turn failure: the raw reply is
  recorded with the parse error, the loop stops as `failed`, and no design is
  ever inferred from prose. Resume re-asks the same request.
- **Every user-role message names JSON.** OpenAI's `json_object` output
  mode rejects a request whose input messages never contain the word
  "json"; the `instructions` field does not count (observed live
  2026-09-04). The goal message and every review request therefore say
  "the required JSON object" explicitly. The test suite asserts this.
- **Provider refusals are the step error, and there is no fallback model.**
  Each client reads the provider's own abnormal-completion signal
  (Anthropic `stop_reason` other than `end_turn`/`stop_sequence` with
  `stop_details`; OpenAI/xAI Responses `status` other than `completed` or a
  `refusal` content part; Gemini `promptFeedback.blockReason`, no
  candidates, or `finishReason` other than `STOP`) into
  `manager.providerStop`. The step fails with "the manager did not
  answer — …" carrying that text verbatim; the body is never parsed as a
  design; the page labels the entry "provider refused"; resume retries the
  same manager. Anthropic's refusal text recommends configuring a fallback
  model. That is not done: the user chose the manager.
- **Protocol version 2 wording (2026-09-04).** Claude Fable 5.1 refused
  every version-1 request with `stop_reason: refusal`, category
  `reasoning_extraction`, zero output tokens (2/2 replays); Sonnet 5 and
  Opus 5 accepted the identical request. Live bisection isolated one line,
  the `reasoning` field description: "your full considerations: what you
  see, what worked and failed, what you will try and why, alternatives you
  considered". Renaming the field did not help; removing the line did.
  Version 2 describes the field as "your design rationale for this turn:
  what the image shows, what to keep, what to change, and why" (2/2
  accepted on Fable 5.1; two other candidates also passed). The field
  name, parser, and stored version-1 loops are unchanged; a resumed
  version-1 loop keeps its recorded system prompt. A test asserts the
  trigger phrases are absent from the current prompt.
- **Protocol version 3: goal kinds and the stopping rule (2026-09-04).**
  What happened: a live loop on "as many different types of fish … as
  possible" rendered 20 labeled fish (9/10, `goalMet: true`), then 30 with
  no defect (9/10), then declared `done` — reasoning "with renders exhausted
  and a strong result in hand, stopping is the right call". Two errors: the
  goal names a maximum, so no single clean image meets it (no ceiling had
  been demonstrated), and the budget belongs to the operator, who can grant
  more turns. The version-2 prompt invited both: it defined `goalMet` as
  "9 or above", and it told the manager to declare done when "further
  renders are unlikely to help" without requiring evidence.
  Version 3 changes:
  - The manager classifies the goal in a required first-design field
    `goalKind`: `bounded` (a recognizable finished state) or `open-ended`
    (a maximum / superlative / "as … as possible"). The operator may
    pre-declare the kind on the new-loop form (`auto` = manager decides);
    the operator's choice wins. The enforced value is `EffectiveGoalKind`
    with `GoalKindSource` = `operator` | `manager`.
  - Open-ended rules in the prompt: every render pushes the maximized
    quantity beyond the best acceptable result (larger step after a clean
    success); continue until a render overshoots and degrades — that is
    the only evidence of the limit — then bisect; `goalMet` stays false
    until the limit is demonstrated; the score measures how far the
    quantity was pushed, defects as deductions.
  - "The turn budget belongs to the operator, not to you. The number of
    renders remaining is never a reason to declare done." With zero
    renders left the manager still answers `render`; the loop pauses as
    `exhausted` and the operator decides.
  - `done` needs evidence for bounded goals too: a change that failed to
    improve the best, or a defect the generator repeatedly failed to fix.
    A sequence of improving renders is evidence the next would improve.
  - **Mechanical enforcement.** `UiGoalLoopPlanner.IsDonePermitted`: on an
    open-ended loop, `done` is accepted only if some later review scored
    strictly below the best review (equal is not degradation). Otherwise
    the runner appends an `objection` entry (system → manager) stating the
    rule, the best turn/score, the latest score, and that `done` is barred,
    then re-asks the same review; the parser then rejects a repeated `done`
    as a contract violation (the loop fails closed, resume retries). The
    objected-to review stays on file and its score counts.
  - Review requests on open-ended loops restate the rule and the best so
    far. Stored version-1/2 loops keep their recorded contract: no goal
    kinds, no objections, original goal-message wording on replay; an
    edited manager reply in a fork is parsed under the parent's version.
- **Protocol version 4: two renders per turn (2026-09-04, later).**
  What happened: loops iterated on one image. Each review's next prompt
  was a small edit of the prompt just rendered, so a weak first composition
  stayed for the whole budget; the manager never tried another way to
  convey the goal. Version 4 changes:
  - Every `render` reply carries two complete prompts. `prompt` is the
    **refine** render: an improvement of the render the manager chose to
    continue from (on turn 1, the primary design). `freshPrompt` is the
    **fresh** render: a from-scratch re-attempt at the goal with new
    composition, staging, camera, medium/style, and palette, written with
    everything learned so far. The prompt states the test: "if a reader
    could mistake one for an edit of the other, the fresh prompt is not
    fresh". Identical prompt texts are a parse error. `freshDesignNotes`
    explains how the fresh design differs.
  - After a render, the reply carries `evaluation` (refine) and
    `freshEvaluation` (fresh), each the full 0–10 object, and a required
    `continueFrom` (`refine` | `fresh`): the render the next refine builds
    on. Choosing `fresh` moves the lineage onto the new composition. On
    `done`, `bestVariant` names which render of `bestTurn` is best.
  - The runner writes both `render-request` entries, then runs both
    render jobs concurrently (two normal single-generator `UiJob`s; the
    main-feed badge reads `goal loop · turn N · refine|fresh`). Each
    result is appended as it finishes. The review request describes the
    two renders in order (refine, then fresh) and attaches both images in
    that order; a failed render is described by its error text. Stop or
    crash between the two results leaves the turn with pending renders,
    and the planner re-renders exactly the missing variant on resume.
  - **Open-ended rule uses the refine lineage.** Each refine render must
    push the maximized quantity past the best acceptable result; the fresh
    render may explore another way of fitting more in.
    `UiGoalLoopPlanner.IsDonePermitted` accepts `done` only when a later
    turn's **refine** evaluation scored strictly below the best score
    across all renders; a fresh render scoring low is exploration, not a
    demonstrated limit. The objection text asks for a refine prompt that
    pushes further plus a fresh prompt, with `done` barred.
  - `BestReview` returns `(turn, variant, score)`; the loop records
    `bestTurn` + `bestVariant`; `rendersPerTurn` (`2` from version 4, else
    `1`) is derived from the recorded `protocolVersion`, so stored version
    1–3 loops replay, resume, fork, and parse under their own single-render
    contract (`freshPrompt`/`freshEvaluation`/`continueFrom` are ignored
    there, never required).
  - Page: each render request and result carries a `refine` / `fresh`
    badge; the result the manager continued from shows **manager continues
    from this one**; reviews show both scores with the chosen one marked;
    the refine prompt diff is computed against its **lineage base** (the
    prompt of the render the manager continued from, traced through
    `continueFrom`), and the fresh prompt is shown plain, since a diff
    against an unrelated prompt has no meaning. The all-turns sheet orders
    cells turn → refine → fresh, labels each with its variant, marks the
    continued-from render, and the header states "2 renders per turn".
    The viewer walks refine then fresh within each turn.
- **Score scale.** 0–10; `goalMet` is meant for 9+ (and, for open-ended
  goals, only once the limit is demonstrated). The system prompt defines
  the bands so scores are comparable across turns and managers.
- **All-turns contact sheet.** `UiGoalLoopRunner.BuildSheetAsync` snapshots
  the entries, loads each successful render's original one at a time
  (disk, or the exact B2 object with SHA-256 verification), caps it to a
  1024 px long edge, and draws the grid with
  `ImageCombiner.CreateGoalLoopSheetAsync` (reuses the square layout and
  label bands of the composer's combined sheet). Cell label: score
  (`8.2/10`, or `unscored` / `failed`) in bold, then `turn N of M`, pixel
  size, and `manager: done` when applicable; the cell text is `PROMPT:` +
  the exact rendered prompt and `MANAGER:` + assessment + problems. Written
  atomically to `UiGoalLoops/{id}/sheet.png`, mirrored to
  `FlatImageMirrorPath`, recorded on the loop as `sheetFile` /
  `sheetEntryCount` / `sheetTurns`; one build at a time per process.
  Served at `GET /api/goal-loops/{id}/sheet` (`no-cache`; the page appends
  `?v=sheetEntryCount`). Any signed-in viewer may build.
- **Shared viewer module.** `Ui/wwwroot/viewer.js` + `viewer.css` own the
  overlay DOM and behavior; a page supplies `items()` returning
  `{ id, url, thumbUrl, width, height, title, subtitle, prompt, meta[],
  actions[] }` and calls `open(id)`. The list is re-read on every step so
  items that finish while the viewer is open join the walk. The composer's
  viewer in `app.js` still has its own implementation (favorites, hide,
  video, compare, set-active); migrating it onto this module is the
  intended follow-up so both pages share one viewer.
- **The manager owns the prompt.** Per-endpoint "append extra text" is not
  applied to goal-loop renders. The universal daylight/clarity default lives
  in the manager's system prompt instead, so the manager writes it into the
  prompt when it matters.
- **Each render is exactly the manager's text.** A render is a normal
  `UiJob` (one generator, `n=1`, the loop's shape/detail/quality/moderation)
  and appears in the main job feed with a `goal loop · turn N` badge (plus
  `· refine` / `· fresh` from protocol 4) linking back to the loop. Contact sheets, favorites, hide, and video follow-ups work
  unchanged. If the operator edited the prompt in a fork, the manager is told
  and shown the exact rendered text.
- **Images to the manager are full resolution (owner requirement,
  2026-09-04; supersedes the earlier 1024 px cap).** The manager receives
  the renderer's exact file. The app imposes no resolution preference of
  its own; the only changes are those a provider's *published* hard limit
  forces, applied by `UiGoalLoopImageTransport` in this order and recorded
  in each image's label inside that call's stored request:
  1. MIME not accepted → lossless PNG re-encode (xAI accepts only
     JPEG/PNG).
  2. Pixel or patch cap → downscale to the cap. This is the only step that
     lowers resolution. Today it can fire only above Anthropic's 8000 px
     (2000 px when a request carries more than 20 images) or OpenAI's
     30,000 32×32-patch cap (~30 MP), both above every UI render size.
  3. Per-image byte cap → JPEG at full resolution (q92, then q85); only if
     that still exceeds the cap, progressive downscale.
  4. Request-total cap under pressure → every non-JPEG image in that call
     becomes a full-resolution JPEG. If the finished body is still over the
     cap, the call fails closed with the exact byte count. Nothing is
     dropped or shrunk silently.

  Published limits used (all read 2026-09-04, live-verified the same day):

  | Provider | Per image | Per request | Knob sent |
  |---|---|---|---|
  | OpenAI gpt-5.6-sol | 30,000 patches | 512 MB | `detail: "original"` (8.4 MB 2496×1664 PNG = 6,010 tokens; `high` = 2,924) |
  | Anthropic | 10 MB base64; 8000 px; 2000 px above 20 images | 32 MB | none (rejection text observed: "image exceeds 10 MB maximum: 11174516 bytes > 10485760 bytes") |
  | Gemini 3.5 Flash | — | 100 MB inline | per-part `mediaResolution: MEDIA_RESOLUTION_ULTRA_HIGH` (2,198 tokens vs 1,124 default) |
  | xAI grok-4.6 | 20 MiB; JPEG/PNG only | — | `store: false` (an 11 MB image otherwise fails "Response is too large to store"); `detail` has no effect and is omitted |

  Consequence for Anthropic managers: a 2048² gpt-image-2 PNG (≈5–7 MB) is
  verbatim for the first ≈4 turns; from the turn where the replayed history
  would exceed 32 MB, every PNG in the call is sent as a full-resolution
  JPEG instead. Anthropic's own server-side resize to ≈1568 px long edge is
  the endpoint's limit, not ours. The Anthropic Files API (`file_id`
  references) is the documented route past the 32 MB cap and is the next
  step if full-PNG replay is required there.
- **Memory and disk for full-resolution replay.** The whole history is
  resent every call. Image bytes are loaded one at a time from disk (or the
  recorded B2 object, SHA-256 verified, when the local raw was evicted)
  while the JSON body is streamed to a temp file; the request is sent from
  that file; nothing is cached in RAM between calls. Each manager entry
  records the exact request byte count (`manager.requestBytes`). Cost: a
  loop with N turns re-reads N images on its last call; on production, where
  raws live on B2, that is N downloads per call.
- **Wire viewer expands embedded JSON (2026-09-04).** Provider payloads
  carry JSON inside strings (the manager's reply inside a `text` field;
  every replayed assistant turn in the request). The "everything sent and
  received" block shows each payload with such strings expanded into
  objects for reading, labeled as expanded, with the verbatim text in a
  collapsed block beneath. Expansion is static at render time.
- **Conversation replay, not provider threads.** Each manager call rebuilds
  the whole conversation from entries so forks and edits are exact. Provider
  thinking blocks are shown but not replayed into later turns as assistant
  content (Anthropic rejects altered thinking prefixes); the JSON reply text
  is what the manager sees as its own earlier messages.
- **Forking semantics.** A fork copies entries `[0..k]` from the parent and
  records `parentLoopId` + `forkedAtEntry`. Editing entry `k`:
  goal → new goal text; design/review → re-parsed under the contract (the
  edit must itself be valid JSON) and provider accounting dropped; render
  request → job link dropped so the runner renders the new text; review
  request → wording replaced, same image re-sent. Notes and render results
  are not fork points.
- **Turn budget is enforced at render time.** When the manager wants render
  `maxTurns + 1`, the loop pauses as `exhausted` with the pending prompt
  visible; resume with a higher `maxTurns` continues. Protocol 3 tells the
  manager the budget is the operator's: it must answer `render` whenever
  another render would improve the result, even with zero renders left, so
  `exhausted` is the expected pause on an open-ended goal.
- **Fork parsing follows the parent's protocol version.** An edited design or
  review is validated under the version the parent loop recorded, so a
  version-2 fork never demands `goalKind` and a version-3 fork always does.
- **Persistence is disk-first.** Each loop is a folder
  `ImageDownloadBaseFolder/UiGoalLoops/{id}/` with `loop.json` (metadata) and
  `entries.jsonl` (append-only; rewritten only when an unrendered request is
  filled in place, which bumps `revision`). The registry indexes metadata at
  startup; entries hydrate on demand and idle loops evict (cap 48). Loops
  found `running` at startup become `stopped` with an explanatory note.
- **Concurrency.** At most 6 loops run per process. Renders ride the normal
  `UiTargetScheduler` lanes. Manager calls are ordinary HTTP with the
  provider's timeout.
- **Permissions.** With auth on, the loop's `CreatorLogin` and the developer
  login `ernieMultiZone` can stop, resume, and fork; with auth off (local),
  anyone can. Everyone can view every loop.
- **Cost accounting.** Manager token usage and priced cost accumulate on the
  loop (`managerCostUsd`, flagged unknown when a provider publishes no
  price); render cost sums the jobs' estimates.

## 4. Manager catalog

Defined once in `TextLLMs/ManagerChatClients.cs` (`ManagerCatalog`), exposed
through `/api/config` `goalLoop.managers`. Availability is gated by the same
settings keys as the describe endpoints.

| Key | Model | Transport |
|-----|-------|-----------|
| `manager-gpt-5.6-sol` | gpt-5.6-sol | OpenAI Responses API, reasoning summaries, JSON object mode |
| `manager-claude-fable-5-1` | claude-fable-5-1 | Anthropic Messages, adaptive thinking |
| `manager-claude-opus-5` | claude-opus-5 | Anthropic Messages, adaptive thinking |
| `manager-claude-sonnet-5` | claude-sonnet-5 | Anthropic Messages, adaptive thinking |
| `manager-gemini-3.5-flash` | gemini-3.5-flash | Google generateContent, thoughts returned, JSON MIME |
| `manager-grok-4.6` | grok-4.6 | xAI Responses API |

Gemini 3.5 Pro is not offered (partner-only as of 2026-09-04).

## 5. Entry model

`UiGoalLoopEntry` kinds, in loop order for one turn:

| kind | from → to | text |
|------|-----------|------|
Parties: `user` (the person who started the loop), `manager` (the LLM),
`generator` (the image endpoint), `system` (the loop runner acting as the
operator between them).

| kind | from → to | text |
|------|-----------|------|
| `goal` | user → manager | the goal message (includes the turn budget) |
| `design` | manager → system | raw JSON reply; `manager.parsed` holds the contract fields (protocol 4: `prompt` + `freshPrompt`), `manager.providerReasoning` the provider's thinking when returned |
| `render-request` | system → generator | the exact prompt rendered; `render` holds job id, options, and (protocol 4) `variant` = `refine` \| `fresh`; two per turn from protocol 4 |
| `render-result` | generator → system | image url/thumb/size/cost, or error + recovery hint; `render.variant` as above |
| `review-request` | system → manager | review text; `images[]` holds the exact attachments sent, each with its `variant` (refine first, then fresh) |
| `review` | manager → system | raw JSON reply with `evaluation` (refine), `freshEvaluation` (fresh), `continueFrom`, and both next prompts |
| `objection` | system → manager | protocol 3: the previous review's `done` is not accepted (open-ended goal, no demonstrated limit); the review is asked again with `done` barred |
| `note` | system → user | stop/resume/fork/done/crash annotations; never advance the loop |

Each manager entry also carries `wireRequest` (the exact provider request
body with image payloads replaced by placeholders) and `wireResponse` (the
raw provider response). Render entries carry the `gen-result` event JSON as
`wireResponse`.

## 6. API

- `GET /api/goal-loops` — list summaries (newest first).
- `GET /api/goal-loops/{id}?after=N` — metadata + entries from index N;
  `revision` changes mean refetch from 0.
- `POST /api/goal-loops` — form: `user`, `goal`, `generator`, `manager`,
  `maxTurns`, `shape`, `detail`, `quality`, `moderation`, `goalKind`
  (`auto` | `bounded` | `open-ended`).
- `POST /api/goal-loops/{id}/sheet` — build/rebuild the all-turns sheet;
  returns `{ sheetEntryCount, sheetTurns, url }`. 409 when nothing rendered.
- `GET /api/goal-loops/{id}/sheet` — the PNG (404 until built).
- `POST /api/goal-loops/{id}/stop`
- `POST /api/goal-loops/{id}/resume` — optional `maxTurns`.
- `POST /api/goal-loops/{id}/fork` — `entryIndex`, optional `text`,
  optional `maxTurns`; returns the child id.

## 7. Files

- `MultiImageClient/TextLLMs/ManagerChatClients.cs` — multi-turn vision chat
  clients + catalog.
- `MultiImageClient/Implementation/UiGoalLoops.cs` — model, protocol
  (`UiGoalLoopProtocol`), planner (`UiGoalLoopPlanner`: next step, fork),
  storage, registry, runner.
- `MultiImageClient/Workflows/UiWorkflow.cs` — endpoints and
  `/api/config.goalLoop`.
- The runner emits each render job's `accepted` event itself with a
  `goalLoop { id, turn, entryIndex, manager }` lineage object; `app.js`
  renders it as the card badge.
- `MultiImageClient/Ui/wwwroot/goal.html`, `goal.js`, `goal.css` — the page
  (identity chip, goal-kind selector, prompt diffs, sheet controls, viewer
  wiring).
- `MultiImageClient/Ui/wwwroot/viewer.js`, `viewer.css` — the shared viewer
  module.
- `MultiImageClient/promptTransformation/ImageCombiner.cs` —
  `CreateGoalLoopSheetAsync` / `LoopSheetCell`.
- `MultiImageClient/Ui/wwwroot/index.html`, `app.js`, `style.css` — header
  link and job-card badge.
- `MultiImageClient.Tests/GoalLoopTests.cs` — protocol parse (v3 goal kinds,
  barred `done`), next-step planning (open-ended objection, done-permitted
  rule, best review), fork semantics; `GoalLoopPairTests` covers protocol 4
  (two prompts, both evaluations, `continueFrom`, pending-variant re-render,
  refine-only degradation rule, single-render replay of stored v3 loops).
- `GET /api/goal-loops` summaries and `GET /api/goal-loops/{id}` carry
  `rendersPerTurn` and `bestVariant`.

## 8. Live verification (2026-09-04, local `--ui`, gpt-image-2 low)

- Gemini 3.5 Flash, GPT-5.6 Sol, Claude Sonnet 5, and Grok 4.6 each completed
  a full design → render → review cycle with contract-conforming JSON;
  provider reasoning/thinking was captured for Gemini and GPT-5.6.
- Fork with an edited render prompt: the manager was told the operator
  edited the prompt, scored the off-goal render 4/10, and asked to render
  again; the 1-turn budget paused the child as `exhausted`; resume with
  `maxTurns=2` continued to a 7/10 `done`.
- Stop during an in-flight render recorded the finished render, then
  stopped; resume from the page continued with the review.
- Rejection paths: fork from a note, prose edit of a manager reply, resume
  of a `done` loop, and a bodiless resume request each return a specific
  error instead of acting.
- Claude Fable 5.1 refused the version-1 system prompt on the user's first
  real loop (fish chart goal): `stop_reason: refusal`, category
  `reasoning_extraction`, 1,317 input / 0 output tokens, surfaced only as
  "the manager returned no text". Direct API replays confirmed the refusal
  and bisected it to one prompt line; Opus 5 accepted the same request.
  Fixed by protocol version 2 wording plus explicit provider-stop
  reporting (section 3). Fable 5.1 then completed a live loop from the
  page.
- The fish loop that motivated protocol 3 (`cb39b8918c8b`, Fable 5.1,
  gpt-image-2, `maxTurns=2`): turn 1 = 20 fish 9/10 `goalMet: true`,
  turn 2 = 30 fish 9/10 `done`. Under protocol 3 the same entries yield an
  `objection` step (unit-tested), and the `done` would be accepted only
  after a later render scored below 9.
- Protocol 3 page verification (local `--ui`, loop `001c5dbe1244`, 6
  renders at 5056×3392): identity chip shows the composer's name; the
  viewer opened on turn 1, decoded the original, walked with ArrowRight
  (turn 2 chrome + card thumb + "preview" badge painted first, blob swapped
  in after decode, ±10 neighbors preloaded), `End` reached 6/6, Esc cleared
  every field; ten prompt diffs rendered (`+337 −256 words` …); the sheet
  built as a 3072×7944 PNG and served at `?v=27`.

## 9. Relationship to Ideation Mode

[ideation-mode-prd.md](ideation-mode-prd.md) describes N-way concept fan-out
from one brief. The goal loop is the depth axis: one concept, refined by a
manager that sees its own results. The manager clients and the strict JSON
reply pattern built here are the intended substrate for Ideation Mode's
forced-tool-use concept cards.
