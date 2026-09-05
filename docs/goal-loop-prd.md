# Goal loops (manager-driven iterative image making) — PRD + implementation

Status: **implemented 2026-09-04** in the `--ui` web app. This document is the
durable statement of the requirement, the settled decisions, and where each
part lives in code.

## 1. What it is

The original use case is one shot: type an image description, pick targets,
look at the output. The goal loop is the second use case:

1. The user enters a **goal** — the outcome they want, not a prompt.
2. The user picks **one or more image generators** (1–8, any image targets
   from the composer catalog; protocol 5) and exactly **one manager model**
   (a text+vision LLM). The manager sees each generator only as a stable
   **source letter** (A, B, …). Optionally the user adds **independent
   critics** (0–6 models from the same catalog; protocol 6).
3. The manager receives the goal and answers with **two** image designs
   (protocol 4): a **refine** prompt (its primary design) and a **fresh**
   prompt (a clearly different from-scratch approach), each complete, plus
   its reasoning.
4. The server renders both prompts on every chosen generator, concurrently
   (2 × generators images per turn).
5. When critics are configured, each critic — a fresh model instance with a
   one-message conversation — receives the goal and this turn's images (not
   the prompts, not the manager's reasoning) and returns a strict JSON
   critique: score, assessment, problems, and ideas per render plus an
   overall verdict. All critics run concurrently.
6. Every rendered image (or the generator's error for it) goes back to the
   manager, in the **same growing conversation**, together with the turn
   number, the remaining budget, and every critique verbatim (labeled
   "Critic 1", "Critic 2", …; identities withheld) as evidence beside its
   own judgment.
7. The manager scores every render 0–10 against the goal, explains what it
   sees, names the exact render (variant + source) the lineage **continues
   from**, and either sends the next pair — a refine of the chosen render
   and a new from-scratch attempt informed by everything learned — (steps
   4–7 repeat) or declares the loop done and names the best turn, render,
   and source. The objective is one best image from any source.

Every step is recorded and shown live on a dedicated page. The loop can be
stopped, resumed, granted more turns, and forked from any message — including
after editing the text of that message.

## 2. Requirements (user-specified 2026-09-04)

| # | Requirement | Behavior |
|---|-------------|----------|
| R1 (amended by R21) | Goal text in, one or more generators, one manager | New-loop form on `goal.html`. The generator picker accepts 1–8 checked generators (catalog order = source-letter order); the manager selector accepts exactly one. Unavailable targets are listed disabled with their exact availability problem. |
| R2 | Manager stays in one long conversation | Every manager call replays the full conversation rebuilt from the loop's entries (system prompt, goal, each earlier design/review reply, each review request with its image). |
| R3 | Manager iterates until the goal is met | System prompt instructs deliberate iteration, learning what the generator responds to, returning to earlier directions, and stopping when done or when further renders are unlikely to help. |
| R4 | Dedicated live-updating page | `goal.html` polls the selected loop every second and appends turns as they happen. Head shows status, activity, score, best turn, spend. The header's **main page** chip and the `MultiImageClient` title both link to `./` (the composer and job feed), matching the composer's **goal loops** chip. |
| R5 | Identify every contribution | Each entry leads with its contributor’s full name and role. Request direction and timing remain under Details & actions. |
| R6 | Click reveals everything sent and received | Manager entries have a `sent / returned` disclosure with the exact wire request (image bytes replaced by placeholders) and the raw provider response. Review requests show the exact text, the attached original's dimensions/MIME/bytes, and how it travelled (`transport`); each manager entry shows the exact request byte count and, per image, the conformance label inside the stored request. Embedded JSON strings in payloads are shown expanded for reading with the verbatim text beneath. |
| R7 | Full image plus all metadata | Render results show the image, the generator's display name, returned pixel size, cost, job link, and error recovery hint when rendering failed. |
| R8 | Full score, thinking, and next-step considerations | Review entries show the 0–10 score (large), goal-met flag, assessment, problems, keep list, the manager's reasoning, the provider-side reasoning/thinking when the provider returns it, design notes, decision, and next prompt. |
| R9 | Compare successive turns | Each turn shows images, a contributor score table, then expandable contributions. Requests and events remain in entry order inside a disclosure. |
| R10 | Stop / resume / resume from here | Stop marks the loop `stopped` and cancels in-flight work at the next boundary. Resume continues from the effective tail. Fork from any entry creates a child loop that continues from that point. |
| R11 | Edit text inside a turn, fork after modifying | Editable entries (goal, design, render request, review request, review) have an `edit + fork` control. The fork copies entries up to that point, replaces the text, drops everything derived from the old text, and runs from there. |
| R12 | Default 6 turns, settable at initiation | `maxTurns` defaults to 6, range 1–30, set on the new-loop form; resume may grant more turns. A "turn" is one manager design cycle: one render before protocol 4, the refine + fresh pair from protocol 4. |
| R13 | Manager is not told the generator identity | The system prompt says the generators' identities are withheld. No generator name, label, or model appears in any manager-bound text. Protocol 5 names each generator only by a stable letter (source A, B, …). |
| R14 | Manager rates and explains whether to try again | Required evaluation on every reply after a render (`evaluation` before protocol 4, `evaluation` + `freshEvaluation` on protocol 4, one `evaluations[]` entry per render shown on protocol 5); `decision` is `render` or `done` with the reason in `reasoning`/`doneStatement`. |
| R15 (2026-09-04) | "As many as possible" must not stop early | Protocol 3 goal kinds. An open-ended goal may end only after a render pushed past the best result and degraded; the turn budget is never a reason to stop; the server objects to a premature `done` and re-asks with `done` barred. See section 3, "Goal kinds". |
| R16 | Same viewer, page-independent | `viewer.js` is a standalone viewer module with a source-parameterized interface (`MultiImageViewer.create({ items, resolveUrl })`). The goal page walks every render of the selected loop with it: preview-first atomic paint, ±10 preloading over 6 fetch slots, arrow/wheel/side-button/Home/End navigation, `f` fullscreen, `?` help, Esc/click-outside close. |
| R17 | Reuse the existing identity | The page shows the composer's creating-as name (authenticated profile display name first, else the canonical personal configuration document's `creatingAs`, else the legacy mirror key) as a read-only chip. A name input appears only when none exists; what it collects is written back to the canonical document. |
| R18 | See the prompt changes turn to turn | Word-level LCS diff (`ins`/`del`, `+N −M words` summary, plain-text toggle) on every render request against the previous rendered prompt, and on every review's next prompt against the prompt just rendered. |
| R19 | One image with every step | **build all-turns contact sheet** on the loop head renders one PNG: header band (goal, generator, manager, kind, best turn, status) above a square grid of every rendered turn with its score, `turn N of M`, pixel size, the exact prompt, and the manager's assessment/problems. Rebuild is offered when entries were added since. |
| R20 (2026-09-04, later) | Leave the rut: two renders per turn | The user observed loops iterating on one image, each turn a small edit of the last prompt, never leaving a weak composition. Protocol 4: every turn renders a **refine** prompt (an improvement of the render the manager chose to continue from; on turn 1 the primary design) **and** a **fresh** prompt (a from-scratch re-attempt at the goal: new composition, staging, camera, medium/style, palette — a different way to convey the same point, written with what has been learned so far). The manager scores both and sets `continueFrom` to the render its next refine builds on, so the lineage can jump to the fresh image at any turn. See section 3, "Protocol version 4". |
| R21 (2026-09-04, later) | Several generators per loop | The user asked to choose 1, 2, 4, … generators instead of exactly one, with every generator's output sent to the manager so it can learn and evaluate how each is doing; every later turn keeps all of them; the writer may choose which one to focus on; the objective stays **one image from any source that best satisfies and covers the requirements**. Protocol 5: each turn's refine and fresh prompts are rendered by every selected generator (renders per turn = 2 × generators); the manager sees each generator as a stable **source letter** (A, B, …), scores every render in an `evaluations[]` array, and names the exact render (`continueFrom: {variant, source}`) the next refine builds on; `bestSource` completes `bestTurn`/`bestVariant`. See section 3, "Protocol version 5". |
| R22 (2026-09-04, later) | Critiques from several independent agents | The user asked to get critiques — feedback, problems, ideas, ratings of how well the image meets the requirements — from multiple agents independently, even with one main author set: e.g. Fable as the manager, plus a new clean instance of Fable, GPT, and Grok each asked what it thinks of the current image. Protocol 6: the new-loop form takes 0–6 **critics** from the manager catalog (the manager's own model allowed; the same critic twice not). After every turn's renders, each critic is called once, concurrently, as a fresh instance with a one-message conversation carrying the goal and the turn's images only; it returns a strict JSON `critiques[]` (exactly one `{variant, source, score, goalMet, assessment, problems, ideas}` per render shown) plus `overall`. The review request forwards every critique verbatim as `Critic N`, identities withheld, framed as evidence and not instructions; the manager's reply contract is unchanged. See section 3, "Protocol version 6". |
| R23 (2026-09-05) | Reduce repetition and distinguish contributors | Show each image once in the default turn view. Pair each generator’s Refine and Fresh images. Name every contributor in score rows and contribution headers. Separate manager and critic instances of the same model by role. Keep full records expandable. |
| R24 (2026-09-05, not implemented) | Configurable participants with separate histories | The user wants image makers, image understanders, and prompt writers with controllable instructions and context. Allow multiple participants using the same model, including two Groks with different personas. Each participant knows only its assigned context and own conversation history. |
| R25 (2026-09-05) | Reusable visual artifacts for every group image loop | Every loop provides best/latest/all/per-generator browsing, named contributors, fullscreen viewing, compact PNG pages, and portable HTML export. |
| R26 (2026-09-05) | Usable goal selectors | Hide unavailable models. Let the user resize the left picker with pointer or keyboard. |
| R27 (2026-09-05) | Astra critic | Offer GPT-6 Astra through the shared manager/critic catalog and existing OpenAI key. |

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
- **Protocol version 5: several generators per loop (2026-09-04, later).**
  The user wants to pick 1, 2, 4, … generators and have every output sent
  to the manager, so the manager learns how each generator performs, keeps
  all of them every turn, and may write for the one it chooses to focus on;
  the objective is one best image from any source. Version 5 changes:
  - **Loop model.** `UiGoalLoop.Generators[]` (`{key, label, source}`),
    1–8 entries (`UiGoalLoopSources.MaxGenerators`), in the order picked;
    `source` is the letter at that index (`A`…`H`). `GeneratorKey` /
    `GeneratorLabel` keep the first generator for stored loops and legacy
    readers; `GeneratorList()` returns the list, or the single legacy
    generator with `source = null` for loops stored before version 5.
    `rendersPerTurn` = 2 × generators on version 5.
  - **Sources are opaque and stable (R13).** The manager is told how many
    sources exist and their letters in the goal message; every review names
    each render as `REFINE render, source A` etc. The same letter is the
    same generator on every turn, so the manager can learn a source's
    strengths and failure modes without being told what it is. No name,
    label, model, or provider reaches any manager-bound text.
  - **Every prompt on every source.** `UiGoalLoopPlanner.PlannedRenders`
    yields refine on A, B, …, then fresh on A, B, …; the runner starts all
    2 × N `UiJob`s concurrently (one per render, each a single-generator
    job carrying `goalLoop { …, variant, source }` in its `accepted` event;
    the main-feed badge reads `goal loop · turn N · refine · source A`).
    Pending-render detection (crash, stop, fork) matches on variant **and**
    generator, so exactly the missing renders re-run.
  - **Reply contract.** `evaluations`: null on the first design; afterwards
    an array with exactly one `{variant, source, score, goalMet,
    assessment, problems, keep}` per render shown — a missing render, a
    duplicate, an entry for a render not shown, or the old
    `evaluation`/`freshEvaluation` objects are parse errors (fail closed;
    the runner passes the parser the exact `(variant, source)` list of the
    review request's turn). `continueFrom`: null on the first design; after
    a review `{variant, source}` naming one of the renders shown (a string
    is rejected). `bestSource` joins `bestTurn`/`bestVariant`. Everything
    else is unchanged from version 4.
  - **Review request.** Renders are listed refine A, refine B, …, fresh A,
    fresh B, …, with the attached images in the same order; a failed render
    is described by its error text and skipped in attachment numbering; a
    source with no render request is reported as "not rendered". The
    request states which render the refine built on ("the fresh render of
    source B from turn 1") and the best so far with its source.
  - **Open-ended rule is per source.** `IsDonePermitted` accepts `done`
    only when a later turn's **refine render on the source that holds the
    best result** scored strictly below the best. A weaker source scoring
    low says nothing about the best source's limit, and a fresh render is
    still exploration. The objection names the source; the loop's
    `lastScore` is the latest review's best refine score across sources.
  - **Image transport.** 2 × N images per turn join the replayed
    conversation as before (one at a time from disk/B2 to a temp-file
    body). Provider count/size thresholds (`ManagerImageLimits`) apply to
    the whole request, so with many sources a provider's published
    downscale rule (e.g. Anthropic above 20 images) engages in fewer turns;
    each change is recorded in the stored request as before.
  - **Page.** The new-loop form is a checkbox picker with a live
    "N selected: A gpt-image-2, B … · 2N renders per turn" readout. The
    loop head lists the sources with their generator names; every render
    entry carries a `source A · <generator>` tag next to its refine/fresh
    badge; the chosen-lineage marker compares variant **and** source;
    reviews show one scored block per render (heading `refine render ·
    source B (grok-web pro)`), the decision names the render continued
    from; the viewer walks turn → refine → fresh → source order and titles
    each item with its source and generator; the sheet orders cells the
    same way, labels each `source A: <generator>`, and lists all generators
    in the header. Stored version-1–4 loops render exactly as before
    (single source, no letters).
  - **Compatibility.** `rendersPerTurn` and the parser branch on the
    recorded `protocolVersion`; version-4 loops keep the pair fields and
    string `continueFrom`; `POST /api/goal-loops` accepts repeated
    `generators` fields or comma-separated values and still accepts the
    legacy single `generator` field.
- **Protocol version 6: independent critics (2026-09-04, later).** The
  user wants critiques — feedback, problems, ideas, ratings against the
  requirements — from several agents independently of the main author, so
  that with Fable as the manager a clean new Fable instance, GPT, and Grok
  can each be asked what they think of the current image. Version 6
  changes:
  - **Loop model.** `UiGoalLoop.Critics[]` (`{key, label, model, index}`),
    0–6 entries (`UiGoalLoopCritics.MaxCritics`), each a manager-catalog
    key. The manager's own model is allowed as a critic (it runs as a
    separate instance with no shared context); listing the same critic
    model twice is rejected. `CriticList()` returns the list or empty;
    critic tokens and cost accumulate separately (`criticCostUsd`,
    `criticCostKnown`, `criticInputTokens`, `criticOutputTokens`).
  - **Critics are clean instances.** A critic call is one system prompt
    (`UiGoalLoopProtocol.CriticSystemPrompt`) plus one user message
    (`BuildCritiqueRequestText`): the GOAL, the goal kind when known, and
    the turn's renders listed refine A, B, …, then fresh A, B, … with the
    successful ones attached in that order at full rendered resolution
    (same `UiGoalLoopImageTransport` rules, per the critic provider's
    published limits). Nothing of the manager's conversation is sent, and
    **prompts are withheld**: a critic judges what the picture shows, not
    what it was meant to show. Failed renders are listed with the
    generator's error and scored 0 by contract. Generator identities are
    withheld from critics as from the manager (letters only).
  - **Critic reply contract.** Exactly one JSON object: `critiques[]` with
    exactly one `{variant, source, score 0–10, goalMet, assessment,
    problems[], ideas[]}` per render shown (missing, duplicate, or unknown
    renders fail the parse) and a non-empty `overall` comparative verdict.
    `ParseCritiqueReply` is strict fail-closed; a markdown fence is the only
    tolerance.
  - **Scheduling.** The planner adds `AskCritics` between the last render
    result of a turn and the review request: `PendingCritiques` lists the
    critic indices without an accepted critique for the turn; all of them
    run concurrently (`Task.WhenAll`, each through the manager-call
    semaphore). A provider refusal or a contract error is recorded on that
    critic's `critique` entry with the raw reply; after every critic has
    answered, any failure fails the step and pauses the loop `failed`
    ("resume retries this step"). Resume re-asks **only** the critics still
    owed, reusing the turn's existing `critique-request` entry for that
    critic so the re-sent message is exactly the recorded one. Loops
    without critics skip the step entirely.
  - **The manager receives critiques verbatim.** The turn's review request
    ends with an `INDEPENDENT CRITIQUES (N)` section
    (`AppendCritiquesSection`) before the closing instruction: each critic
    as `Critic 1`, `Critic 2`, … (identities withheld, like sources) with
    the exact accepted reply text, including JSON formatting and score precision.
    The section states that each critic is a
    separate instance that saw only the goal and the same images, and that
    critiques are evidence, not instructions. The version-6 system prompt
    tells the manager to look again where several critics agree on a
    defect it missed, to say in its reasoning where it disagrees and why,
    and that its evaluations and decision remain its own. The manager's
    reply contract is unchanged from version 5.
  - **Forwarding simplification (2026-09-05).** Remove the intermediate prose formatter.
    Forward each accepted critique entry's original `Text` under its critic number.
    Parsing still validates the reply before forwarding.
    The old formatter rounded scores and changed whitespace, punctuation, and array boundaries.
    Keep parsed fields for the page, contact sheet, and acceptance checks.
    Existing persisted review requests retain their recorded text during replay.
  - **Page and sheet.** The new-loop form has an "independent critics"
    checkbox picker over the manager catalog (none checked by default: each
    critic is one more vision call per turn) with a count line naming
    `Critic N: <full model label>`. The loop head lists the critics and a
    separate critic cost; the list shows `· N critics`. Each turn shows a
    `critique request` entry (message + attached images) and an
    `independent critique` entry per critic (score block per render,
    assessment, problems, ideas, overall verdict, usage, wire request and
    response); a failed critique is labeled `reply rejected` /
    `provider refused` with the contract error. Sheet cells append
    `CRITIC N (<label>): score — assessment / Problems / Ideas` under the
    manager's assessment; the header lists the critics.
  - **Compatibility.** Stored version ≤ 5 loops have no critics and plan
    exactly as before; `DetermineNextStep` ignores a critic count below
    version 6. `POST /api/goal-loops` takes repeated `critics` fields or
    comma-separated values; `/api/config.goalLoop.maxCritics` publishes
    the cap; summaries carry `critics[] {key, label, model, index}`. Fork
    re-validates every critic's availability like the manager's.
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


### Goal selectors and sidebar width (2026-09-05)

- Show only available image generators, managers, and critics in the creation form.
- Keep availability enforcement on the server. This changes the selector presentation only.
- Allow dragging the divider beside the left picker to set its width on desktop.
- Support Left/Right arrows, Home, and End when the divider has keyboard focus.
- Remember the chosen width in browser storage. Clamp it to preserve room for the conversation.
- Keep the single-column layout on narrow screens.
- Double-click the divider to restore the initial width proportion.

### Contributor view (2026-09-05)

The default page supports comparing images and tracing individual contributions.
The shared page applies to every existing and future loop. No per-loop migration is required.
The previous page repeated images in requests and repeated prompts across full-height entries.
Generic role badges obscured the model responsible for each contribution.

- Group each turn’s images by generator, with Refine beside Fresh.
- Label images with their recorded source letter, full generator name, and variant.
- Mark the exact best image and the image selected for continuation.
- Show scores in one table, with a named row per manager or critic instance.
- Keep failed or missing scores explicit. Never average different contributors’ scores.
- Open the exact contributor’s reply when its score is selected.
- Give each critic a stable number and color within the loop. Keep names and roles visible alongside color.
- Distinguish the manager and critic when both use the same model.
- Show a brief excerpt from each contribution. Expand it to read all prompts, assessments, problems, and ideas.
- Keep completed failed critic attempts under Requests & events. Keep unresolved failures visible among contributions.
- Keep request records, timestamps, wire data, and fork controls accessible through disclosures.
- Collapse the creation form when selecting a loop. The New loop button reopens it.
- Keep settings and token usage in a disclosure. Report an unknown price as unknown, without a misleading zero-dollar total.
- Preserve disclosure state during polling when the underlying entry remains unchanged.
- Match viewer prompts and refinement comparisons by source and variant. Different sources can have different prompts after editing and forking.
- Keep older protocol records readable under their recorded identities.

The operator’s display names do not change the identities withheld from models.
This presentation change does not alter model calls, context sharing, or the loop’s stopping rules.

### Participant direction (2026-09-05, not implemented)

A model is an engine, not a participant identity.
Two Groks can serve different roles with different instructions and independent conversation histories.
Shared context must be an explicit input to each participant.
A participant must not inherit another participant’s history merely because both use the same model.

The outer frame includes the goal, role instructions, supplied context, and rules controlling who acts next.
The user wants control over that frame, rather than a fixed manager-and-critics arrangement.
Image makers receive their selected prompt and references through their supported provider interfaces.
Text and vision participants can maintain separate conversations through persisted request history.

The current protocol still has one manager and zero to six distinct critic models.
Critics still start fresh each turn, and the current API rejects duplicate critic models.
The contributor view does not claim to implement configurable personas or independent persistent critic histories.
Manual turns and a configurable repeating sequence remain the proposed control modes, pending interaction design.

### Reusable visual recap (2026-09-05)

Presentation decisions (2026-09-05):
- Use the shared `style.css` and `goal.css` styling, header, and navigation controls.
- Link to the main page, goal loops, and the exact source conversation. Mark the current results page.
- Use short model labels: Fable 5.1, Opus 5, GPT-5.6 Sol, and Gemini 3.5 Flash.
- Omit redundant Claude prefixes and provider parentheses. Preserve model versions and generator transport distinctions.
- Keep stored provider identities unchanged. Short names affect display only.
- Omit the routine offline-preview notice from the page. Keep export limitations in export feedback and documentation.
- Serve local recap previews through the image app on port 5960, not a separate preview server.
- Standalone exports omit private source-site links. The local preview can link explicitly to the local app.


The page heading is **Goal loop results**. Show the supplied goal verbatim.
Use brief, literal labels without added emphasis or slogans (owner decision, 2026-09-05).

Every goal conversation exposes **Visual recap · browse / PNG / HTML** beside its sheet controls.
This applies to existing and future loops without new model calls.
The recap uses a complete snapshot from the existing loop endpoint.
Reload the page to include subsequent turns.

- Show participant badges with model names and distinct manager, critic, and generator roles.
- Provide all images, best per generator, latest per generator, and a generator filter.
- Define best as each generator’s highest accepted manager score, retaining every tie.
- Define latest as both variants from each generator’s last successful turn.
- Label missing manager reviews explicitly. Latest does not mean approved or final.
- Preserve each contributor’s scores and comments separately. Never average them.
- Join prompts and comments by recorded job, turn, source, and variant identities.
- Reject incomplete or ambiguous snapshots. Failed replies never supply scores.
- Show images through the existing shared viewer, including keyboard, wheel, and fullscreen controls.
- Export the selected images as 1920-pixel-wide PNG pages (maximum height 1320 pixels), with at most eight images per page.
- Use card previews for PNG composition. Label the pages as previews.
- Export one portable HTML snapshot containing all previews, comments, prompts, and reusable selection controls.
- Embed page assets in the HTML. Preview browsing works offline.
- Original images retain their recorded URLs and require access to their original host.
- Portable HTML requires external HTTPS originals. Reject private app URLs instead of exporting the secret application path.
- Never include private app paths, authentication data, creator logins, or wire payloads in the exported snapshot.
- Build exports on demand in the browser. Do not retain new server image caches or rerun providers.

The existing large all-turns sheet remains available beside the recap.
The recap supplies a standard artifact view, rather than a separate one-time fruit page.
Exports do not publish a website or change access to the production application.

## 4. Manager catalog

Defined once in `TextLLMs/ManagerChatClients.cs` (`ManagerCatalog`), exposed
through `/api/config` `goalLoop.managers`. Availability is gated by the same
settings keys as the describe endpoints.

| Key | Model | Transport |
|-----|-------|-----------|
| `manager-gpt-6-astra` | gpt-6-astra | OpenAI Responses API, medium reasoning, JSON object mode |
| `manager-gpt-5.6-sol` | gpt-5.6-sol | OpenAI Responses API, reasoning summaries, JSON object mode |
| `manager-claude-fable-5-1` | claude-fable-5-1 | Anthropic Messages, adaptive thinking |
| `manager-claude-opus-5` | claude-opus-5 | Anthropic Messages, adaptive thinking |
| `manager-claude-sonnet-5` | claude-sonnet-5 | Anthropic Messages, adaptive thinking |
| `manager-gemini-3.5-flash` | gemini-3.5-flash | Google generateContent, thoughts returned, JSON MIME |
| `manager-grok-4.6` | grok-4.6 | xAI Responses API |

Gemini 3.5 Pro is not offered (partner-only as of 2026-09-04).

GPT-6 Astra joins the shared manager/critic catalog on 2026-09-05, including the critic selector.
It uses the existing OpenAI key, image transport, strict critique contract, and medium reasoning.
The manager selector also exposes it because both roles use the same catalog.
Pricing estimates use $10 input and $50 output per million tokens.
Above 272,000 input tokens, input rates double and output rates multiply by 1.5.
Sources: [Astra model](https://developers.openai.com/api/docs/models/gpt-6-astra) and
[OpenAI image inputs](https://developers.openai.com/api/docs/guides/images-vision), checked 2026-09-05.
Provider acceptance still depends on the configured account.
Verification: all 12 Astra catalog/pricing and critic tests passed. No live Astra critic call was made.

## 5. Entry model

`UiGoalLoopEntry` kinds, in loop order for one turn:

| kind | from → to | text |
|------|-----------|------|
Parties: `user` (the person who started the loop), `manager` (the LLM),
`generator` (the image endpoint), `system` (the loop runner acting as the
operator between them), `critic` (protocol 6: an independent critic
instance).

| kind | from → to | text |
|------|-----------|------|
| `goal` | user → manager | the goal message (includes the turn budget) |
| `design` | manager → system | raw JSON reply; `manager.parsed` holds the contract fields (protocol 4: `prompt` + `freshPrompt`), `manager.providerReasoning` the provider's thinking when returned |
| `render-request` | system → generator | the exact prompt rendered; `render` holds job id, generator key/label, options, (protocol 4) `variant` = `refine` \| `fresh`, and (protocol 5) `source` letter; 2 × generators per turn from protocol 5 |
| `render-result` | generator → system | image url/thumb/size/cost, or error + recovery hint; `render.variant` / `render.source` as above |
| `critique-request` | system → critic | protocol 6: the one message a critic receives (goal + this turn's renders); `images[]` as on a review request; `critic {index, key, label, model}` names which critic; one per critic per turn |
| `critique` | critic → system | protocol 6: raw JSON reply; `critic` adds call metadata (tokens, cost, `requestBytes`, `providerStop`, `parseError`) and `parsed {critiques[] {variant, source, score, goalMet, assessment, problems, ideas}, overall}`; a failed critique keeps `error` and is re-asked on resume |
| `review-request` | system → manager | review text (protocol 6: ends with the `INDEPENDENT CRITIQUES` section when critics exist); `images[]` holds the exact attachments sent, each with its `variant` and `source` (refine first, then fresh; sources alphabetical) |
| `review` | manager → system | raw JSON reply; protocol 4: `evaluation` (refine), `freshEvaluation` (fresh), string `continueFrom`; protocol 5: `renderEvaluations[] {variant, source, evaluation}`, `continueFrom` + `continueFromSource`, `bestSource`; plus both next prompts |
| `objection` | system → manager | protocol 3: the previous review's `done` is not accepted (open-ended goal, no demonstrated limit); the review is asked again with `done` barred |
| `note` | system → user | stop/resume/fork/done/crash annotations; never advance the loop |

Each manager entry also carries `wireRequest` (the exact provider request
body with image payloads replaced by placeholders) and `wireResponse` (the
raw provider response). Render entries carry the `gen-result` event JSON as
`wireResponse`.

## 6. API

- `recap.html?loop={id}` — reusable visual snapshot with PNG and portable HTML exports. Uses the existing GET endpoint.
- `GET /api/goal-loops` — list summaries (newest first).
- `GET /api/goal-loops/{id}?after=N` — metadata + entries from index N;
  `revision` changes mean refetch from 0.
- `POST /api/goal-loops` — form: `user`, `goal`, `generators` (repeated
  field or comma-separated, 1–8 distinct keys; legacy single `generator`
  still accepted), `manager`, `maxTurns`, `shape`, `detail`, `quality`,
  `moderation`, `goalKind` (`auto` | `bounded` | `open-ended`), `critics`
  (repeated field or comma-separated, 0–6 distinct manager-catalog keys).
  `/api/config.goalLoop.maxGenerators` and `.maxCritics` publish the caps.
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
  `goalLoop { id, turn, entryIndex, variant, source, manager }` lineage
  object; `app.js` renders it as the card badge.
- `MultiImageClient/Ui/wwwroot/goal.html`, `goal.js`, `goal.css` — the page
  (contributor roster, paired images, score comparison, expandable contributions,
  request records, identity chip, prompt diffs, sheet controls, and viewer wiring).
- `tools/tests/goal-recap.test.cjs` — exact joins, tied best scores, missing reviews, failed replies, and legacy identities.
- `MultiImageClient/Ui/wwwroot/goal-sidebar.js` — persistent, keyboard-accessible sidebar resizing.
- `MultiImageClient/Ui/wwwroot/recap.html`, `recap.css`, `recap-model.js`, `recap.js` — reusable recap and browser exports.
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
  refine-only degradation rule, single-render replay of stored v3 loops);
  `GoalLoopMultiSourceTests` covers protocol 5 (`evaluations[]` exactness,
  object `continueFrom`, source letters in goal/review/objection text,
  2 × N planning, per-generator pending re-render, per-source done rule,
  `rendersPerTurn` derivation); `GoalLoopCriticTests` covers protocol 6
  (critique parse exactness and fail-closed cases, critique request text
  without prompts, verbatim forwarding in critic order inside the review
  request, `AskCritics` scheduling after renders and before the review,
  re-asking only owed critics, model round trip).
- `GET /api/goal-loops` summaries and `GET /api/goal-loops/{id}` carry
  `generators[] {key, label, source}`, `critics[] {key, label, model,
  index}`, `rendersPerTurn`, `bestVariant`, `bestSource`, and the critic
  cost/token totals.

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

## Contributor-view verification (2026-09-05)

- Inspected local completed loops using protocol 6 with two critics, protocol 5 with two generators, and protocol 1.
- Verified that a score opens its exact contributor reply.
- Verified separate labels for Gemini as manager and Gemini as critic.
- Verified that two failed critic replies remain available after a successful retry.
- Verified original-image loading and prompt display in the shared viewer.
- Checked the layout at desktop width and a 390-pixel viewport.
- Ran JavaScript syntax validation and five source-identity assertions for prompt selection and refinement comparisons.
- The existing 76 goal-loop tests passed after the critique-forwarding change.
- No new provider generation ran during these presentation checks.
