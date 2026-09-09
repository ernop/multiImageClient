# Goal loops (manager-driven iterative image making) — PRD + implementation

Status: **implemented 2026-09-04** in the `--ui` web app. This document is the
durable statement of the requirement, the settled decisions, and where each
part lives in code.

## Multiple samples and operator points — 2026-09-09

Protocol 9 applies to new loops. Existing loops retain their recorded sample counts and JSON contracts.
It retains protocol 8's branching and protocol 7's goal criteria, experiments, and completion checks.

| ID | Requirement | Concrete behavior |
|---|---|---|
| R40 | Generate multiple samples when useful. | Default to two images per prompt from grok-web and one elsewhere. Offer explicit 1/2/4 overrides. |
| R41 | Preserve each sample's identity. | Use candidate IDs c1/c2/c3 and sample variants such as c1-s1/c1-s2. Judge every sample separately. |
| R42 | Accept live owner preferences. | Place +1/−1 controls beside images and prompts. Accumulate separate image and prompt points. |
| R43 | Let late preferences affect planning. | Supply current totals to manager calls and reconsider unstarted plans when new feedback arrives. |

The owner selected provider defaults: grok-web gets two images; all other selected generators get one.
The setup stores `samplesPerPrompt`: zero for provider defaults, or one, two, or four for every selected source.
The manager chooses candidate and source allocations; sample counts follow the operator's setting.
Keep a maximum of twenty-four scheduled images per round. Reject larger plans with an explicit allocation error.
This preserves the previous largest round while permitting repeated samples within that allowance.
Sample counts multiply candidates and source assignments; three ideas on grok-web normally generate six images.

Each sample runs as an independent ordinary image job through existing provider queues and concurrency limits.
The existing UI's multi-image path also uses independent generation calls.
Goal samples disable Grok's separate four-output side-by-side mode to preserve their single-output identity.
Each sample must return exactly one image. Reject unexpected siblings instead of selecting the first result.
The manager, critics, best selection, tree, recap, and resume match exact turn, sample variant, and source.
No fixed seed, reproducibility, or generator reliability is implied by two or four samples.
Current images plus the exact incumbent still attach at most twenty-five images.

Operator feedback applies to one of two explicit target types:

- Image points identify one successful image by its exact job, generator, and output index.
- Prompt points identify exact UTF-8 prompt bytes using SHA-256 within the current loop.

Identical prompts deliberately share prompt points across samples, sources, and later turns within that loop.
Different wording creates a separate prompt target. Image points never transfer automatically to prompts, siblings, or descendants.
Each click changes the cumulative total by +1 or −1. Repeated clicks express stronger preferences.
Points remain separate from critic scores, criterion states, and completion requirements.
Only the loop's controlling owner can add these preferences, following existing local and authenticated creator checks.

Feedback events persist in the loop's disk history, including their actor, delta, exact target, and request UUID.
Retrying the same UUID cannot apply a vote twice. Reusing it for another operation fails visibly.
The UI retries an uncertain vote with its original UUID. Failed saves do not display confirmed points.
Each loop accepts at most ten thousand feedback events. No original image bytes enter feedback storage or caches.
Forks retain only the feedback present in their copied history; subsequent parent-loop votes do not propagate into separate forks.

Each manager call receives a fixed snapshot of cumulative points and records its last included feedback entry.
The snapshot also includes prompt text for prompt targets and exact identities for image targets.
Critics receive no operator points and retain their independent evaluations.
Votes arriving during a call remain available for later calls.
Before starting an unstarted plan, the runner requests another decision if new feedback arrived after its planning snapshot.
It does not cancel image generation already underway. The normal next review receives those votes.
Votes on stopped or completed loops save without starting paid work; explicit resume can reconsider the latest decision.
New feedback can reopen a completed decision on resume. Historical loop protocols retain their existing reply requirements.
The manager must explain material feedback effects without treating preference points as evidence that required outcomes are met.

The Work view exposes image and prompt controls beside each result, with prompt controls also beside request text.
Every sample appears as its own Work image card and its own Tree node, including samples from the same prompt and source.
Work keeps required-outcome counts visible and collapses component details to reduce each comparison row's height.
The Work and Tree buttons sit directly below the loop header on branching loops.
Tree nodes open the corresponding Work record. History shows each feedback change.
The feedback target types and storage stay separate from rendering, so other surfaces can reuse the same target semantics.

### Live sampling and feedback check — 2026-09-09

A local puppy-and-gift loop used grok-web, Fable 5.1, and one independent Grok 4.6 critic.
The default produced two images in each of two rounds. Work showed four cards; Tree showed four separate nodes.
Test clicks added two points to the second first-round image and subtracted one point from its prompt.
The sibling image retained zero image points. Both identical prompt targets displayed minus one.
These votes arrived after the second round started. Its review recorded all three feedback events through entry fourteen.
The manager selected the favored first-round image and explained the separate image and prompt preferences.
It retained the second-round gaze experiment as an alternative and reported that only one sample followed that instruction.
The selected older image relied on its accepted earlier evaluation because its pixels were outside the current visual window.
This check verifies sample identity, display, persistence, and later feedback delivery. It does not establish calibrated image judgments.

## Adaptive branches and tree view — 2026-09-09

Protocol 8 introduced the behavior below. Protocol 9 supersedes its single-image source assignments as specified above.
Existing loops and their forks retain their recorded contracts and manager instructions.
It retains protocol 7's criteria, component studies, evidence records, resource controls, and completion rules.
It supersedes the fixed pair and mandatory refinement/new-idea mix.

| ID | Requirement | Concrete behavior |
|---|---|---|
| R36 | Vary the number and mix of candidates. | Choose one to three candidates within the operator's limit. Explain the allocation. |
| R37 | Search through explicit branches. | Link each candidate to zero to three exact prior images and state each parent's contribution. |
| R38 | Account for each reviewed idea. | Record pursue, branch, hold, or drop, with a concrete reason. Validate these actions against next-round links. |
| R39 | Show the search structure. | Keep chronological Work as the primary view. Provide an optional Tree view with image nodes and parent connections. |

The setup offers `maxCandidates` from one to three, default three.
The manager can use fewer candidates when fewer worthwhile tests exist.
All candidates may refine existing directions, explore independent ideas, or use any supported experiment modes.
Unexpected strengths can inspire new directions. Weak directions can stop receiving work when no useful improvement plan exists.
The manager can revisit earlier images, including previously held or dropped directions, when new evidence supports a concrete plan.
Dropping a direction does not delete its images or history.

Each candidate has a round-local identity `c1`, `c2`, or `c3`, a title, and a complete prompt.
Each parent records `turn`, `variant`, `source`, and `contribution`.
The server accepts only exact successful earlier renders. It rejects unknown, failed, duplicate, or future parent identities.
An empty parent list means an independent idea. Multiple parents allow combining useful elements from different images.
This structure can have multiple parents, although the interface calls the view Tree.
Parent links describe prompt development; they do not send image pixels to the text-to-image generator.
The existing entry controls support operator edits and forks. Selecting a tree node opens its work record.

After each reviewed round, `candidateDecisions` accounts for every distinct candidate across its selected sources:

- `pursue` requires exactly one next candidate linked to that idea.
- `branch` requires at least one next candidate linked to that idea.
- `hold` allocates no descendant in this round and preserves the idea.
- `drop` allocates no descendant in this round and explains why further work lacks a useful plan.

The selected best current candidate cannot be dropped. A completion reply has no render plan.
The manager compares one to six stated options and explains the candidate count and mix in `plan.allocationReason`.
Repeated prompts require `verify` mode for every repeated candidate; another distinct exploration may run alongside them.
All scheduled results receive exact manager and critic evaluations, including the third candidate.

Candidate limits count ideas, not images. Each selected source produces one image per candidate.
Three candidates across eight sources can schedule twenty-four images in a round.
The default ceiling increases from two to three candidates; the manager must justify spending on each candidate.
Source subsets and reduced settings remain available. Existing provider queue and concurrency limits remain unchanged.
Current images plus the exact earlier incumbent can attach at most twenty-five images to a manager request.
Provider request limits still apply. A larger candidate ceiling does not guarantee transport capacity or lower costs.
Independent worker conversations and separate subtask budgets remain planned.

Work preserves the compact chronological image feed, contributor evidence, and expandable requests.
Each image shows its candidate title, mode, scope, parent links, and expandable prompt.
Full experiment plans remain expandable. Tree shows thumbnails, modes, latest candidate actions, and the selected best image.
Tree connections preserve exact source identities. Missing lineage appears as an error without substituting another image.
Displayed prompts come from exact render requests, including operator edits, rather than substituting the original candidate plan.
The tree loads card previews and retains no original image bytes. Historical protocols have no invented tree lineage.
Consistency claims must state their sample count. A lone success supports an observation about that image, not reliability.

New files: `UiGoalLoopFanout.cs`, `GoalLoopFanoutTests.cs`, `goal-tree.js`, and `tools/tests/goal-tree.test.cjs`.

### Local branch verification

Loop `702016816ab7` used Fable 5.1, one Grok 4.6 critic, Sunburst low/standard, and a two-turn budget.
The manager initially selected three independent puppy compositions.
Two requests failed during transport; the remaining image supplied the first visual evidence.
The critic marked failed images uncertain and did not invent visual results.
The manager held the untested ideas, then scheduled one refinement and two independent retries.
The refinement linked exactly to turn 1, candidate c3, source A, preserving its lighting and gift presentation.
All three second-turn images completed. No generation was repeated during manager contract repair.
The final review initially marked an alternative successful despite a partial required component.
Validation rejected that contradiction. Explicit resume supplied the exact error, and the corrected review passed.
The manager selected turn 2, candidate c1, source A, with completion outcome achieved.
This run demonstrates scheduling, lineage, independent evaluation, and repair; it does not establish consistent judgment quality.
One inference overstated reliability from one image despite a qualifying uncertainty statement.
New instructions explicitly require sample counts for consistency claims and independent success checks for every evaluated image.
Tree and Work were checked in the local browser, including the node-to-record link and exact parent label.
Automated checks cover one, two, or three candidates, twenty-four source assignments, archived parents, invalid lineage, and interrupted rendering.

## Pursuit and component experiments — 2026-09-09

Protocol 7 introduced the behavior below. Protocol 8 supersedes its candidate count and lineage rules as specified above.
Existing loops and their forks retain their recorded protocols and manager instructions.
The later historical sections describe protocols 1–6 unless either section explicitly supersedes them.

### Requirements and decisions

| ID | Requirement | Concrete behavior |
|---|---|---|
| R29 | Preserve the actual goal. | Define 3–8 stable criteria, grounded in exact excerpts from the goal. Classify required outcomes, preferences, and context. |
| R30 | Explore options and pursue promising directions. | Publish 2–4 options with reasons. Assign each candidate an explicit question, expected evidence, changed elements, and held elements. |
| R31 | Simplify and rebuild. | Permit minimal core prompts and component studies. Record omitted criterion IDs and the restoration step. |
| R32 | Expose component judgments. | Managers and critics report met/partial/missing/uncertain, visible evidence, and confidence for every criterion. |
| R33 | Stop without false conclusions. | Distinguish achieved, plateau requiring operator review, provider failure, operator stop, and exhausted budget. |
| R34 | Run smaller, targeted experiments. | Each candidate can select a subset of the loop's generators and lower quality/detail settings. |
| R35 | Support long searches without accumulating every image in each request. | Attach current candidates and the exact incumbent. Retain earlier decision records and archive images on disk. |

These requirements supersede R15's mandatory overshoot rule and R20's mandatory fresh redesign for protocol 7.
They also supersede R21's requirement to render every candidate on every selected source.
R22's critics remain independent, but now receive the stable rubric and neutral component-task descriptions.
They still receive no prompts, predictions, manager conclusions, other critics' responses, or previous scores.

The owner requested a planner that can investigate one component before combining the complete scene.
A table study can examine perspective, placement, or palette without generating every subject and background detail.
Successful component studies do not establish that the complete composition works.
The manager must restore required outcomes and inspect their interactions in a full-scene render.

Personal interests supply contextual inspiration unless the goal explicitly requires complete coverage.
Qualitative goals such as warmth or beauty must not become arbitrary object-count maximization.
The manager must preserve emotional impact and overall coherence when those qualities define the goal.
Criteria remain unchanged across subsequent replies; an edited goal starts a new interpretation.
The owner can inspect and edit the initial design through the existing fork controls.

### Candidate plans and resource controls

The two existing candidate slots retain the stored identities `refine` and `fresh` for exact history joins.
Their modes can independently be `pursue`, `explore`, `simplify`, `rebuild`, or `verify`.
Neither slot must change the entire style or setting.
The interface labels these slots Candidate 1 and Candidate 2.
Each option has separate `description` and `reason` strings.
Configuration exposes `goalLoop.protocolVersion`; setup help follows the running server's capabilities.
Identical prompts require `verify` in both slots, permitting repeated samples without pretending they are different designs.
No seed control or reproducibility is promised.

Each candidate records:

- `scope`: `full` or `component`; component scope requires a neutral `componentGoal`.
- `sources`: explicit source letters from the selected catalog, or null for all selected sources.
- `quality` and `detail`: optional reductions from the operator's settings; null inherits those settings.
- `question`, `expected`, `changes`, `holds`, `deferredCriteria`, and `restoreNext`.

Quality accepts low/medium/high/xhigh/max. Detail accepts standard/high/max.
The server rejects unknown sources, duplicate sources, and settings above the operator's chosen levels.
Provider-specific parameter mappings remain unchanged. Some providers ignore quality or detail controls.
Therefore, lower settings do not guarantee a particular speed, resolution, or cost.
Results record their actual requested settings, pixel dimensions, duration, and reported cost.
Interrupted requests preserve their recorded settings when resumed or forked.

Each scheduled source produces one image per candidate.
Protocol 7 schedules 2–16 images per complete turn, depending on source subsets.
These studies use the existing provider queue, concurrency limits, and shared turn budget.
Default turns remain six; the cap remains thirty.
Protocol 7 introduces no unlimited child search or increased default spending. Protocol 8 raises the candidate ceiling as documented above.

Component studies currently share the parent's manager conversation and turn budget.
Independent nested conversations, separate subtask budgets, and automatic specialist-model selection remain planned.
Existing configured critics supply independent evaluations now.

The proposed next stage gives each component worker one question, fixed conditions, and an explicit image/time/cost allowance.
Workers would return compared candidates, observed differences, unresolved uncertainty, and a selected result to the parent manager.
The parent would retain the complete requirements and decide which component results merit integration.
Specialist evaluations could separately examine requirement coverage, visual defects, and the component experiment's result.
Candidate counts should depend on alternatives and observed variation, rather than an arbitrary larger default.
These worker controls and specialist assignments are design directions, not implemented controls.

### Visual context

Before each protocol 7 manager request, select the current turn's images and one earlier incumbent image.
The incumbent uses the manager's exact selected turn, slot, and source.
Do not substitute another image when that identity is missing.
The resulting visual window contains at most seventeen images, independent of completed turn count.
Earlier accepted decision records remain in the conversation; earlier candidate pixels remain in the disk archive.
Archived review messages explicitly state that their images are absent, except for the named incumbent when applicable.
The manager must distinguish historical assessments from current visual inspection.
This is a declared protocol policy, applied before transport, rather than recovery from an oversized request.
Provider transport limits still apply to the selected window; an oversized window still fails visibly.

### Evidence and judges

The stable rubric distinguishes required outcomes, preferences, and contextual inspiration.
Every manager and critic evaluation contains an exact component list matched by criterion ID.
Each component records a categorical result, visible evidence, and low/medium/high confidence.
Confidence is the model's stated uncertainty, not a calibrated probability.
No average converts these categories into a success decision.
The existing 0–10 score remains a secondary rough summary for compatibility.
The primary page shows requirement coverage and component states beside each contributor.
Expanded feedback shows the evidence and uncertainty for each component.
Portable HTML recaps preserve complete component judgments; compact PNG recaps show requirement coverage and the manager's assessment.

Critics additionally audit the rubric's interpretation through `rubricConcerns`.
Each component task receives a separate `experimentAssessment` from the judges.
The manager records observation, tentative inference, uncertainty, critic disagreements, and the next useful test separately.
Judges must not infer prompt wording, image-edit inputs, or causal effects from a single result.
Provider failures remain execution failures; no image means uncertain visual components and a zero compatibility score.
The manager receives accepted critic replies verbatim, retaining disagreement instead of averaging it away.

The manager selects an exact successful image using the criterion profile and stated tradeoffs.
Protocol 7 summaries and the recap's best view follow that selection, even when another image has a higher scalar score.

### Completion and compatibility

A stop requires at least two distinct reviewed turns containing successful images.
This minimum establishes comparison opportunities, not an optimum or a statistical guarantee.
Completion records `outcome`, `evidenceTurns`, `remainingGaps`, and `rationale`.

`achieved` requires an actual full-scope image at the operator's original quality/detail settings.
Its required criteria must be met, with no deferred criteria or remaining gaps.
Remaining gaps mean unmet requirements or material defects, not every possible improvement.
Accepted minor tradeoffs and subjective alternatives belong in `findings.uncertainty`.
`plateau` pauses for operator review without claiming success or proving a generator limit.
The existing resume action asks the manager for another experiment after a plateau.
Protocol 7 recap cards show each contributor's latest accepted evaluation of the same turn.
Earlier accepted evaluations remain in the contribution history, including reviews superseded after a resumed plateau.
Exhaustion retains the pending plan; granting turns executes that plan.
An artistic goal never requires deliberate degradation or endless increases in detail.

Protocol 7 is versioned; running and resumed historical loops retain their previous contracts.
When a protocol 7 manager reply fails validation, resume includes the rejected reply and its exact contract error.
The manager must correct the decision against the same evidence; resume does not fabricate images or silently alter requirements.
This feedback applies only to the latest unresolved manager contract error, without automatic retry loops.
No production release is implied by local testing.
All additional records remain disk-backed metadata. No full-image lifetime cache is introduced.

### Puppy-loop observations

Baseline: `13f8d595ca72`, protocol 6, Fable 5.1 manager, Fable 5.1 and Grok 4.6 critics, three image sources.
The first design converted emotional appeal into maximizing the number of personal interests represented by props.
Both critics also treated omitted interests as defects.
Turn two reached all eight interests, then requested more languages, postcards, and equations to continue increasing detail.
Turn three requested further density despite identifying a busy table.
This directly motivated separating requirements from context and removing mandatory quantity growth.
Turn four acknowledged both critics' clutter warnings, then added further signs, film text, and other props.
Turn five called one poorer sample evidence of a generator limit, despite changing several elements simultaneously.
The run produced 36 images across six turns, with manager reviews for the first thirty images.
The sixth review failed because 36 accumulated images exceeded the manager provider's request-body limit.
That failure motivated R35's fixed visual window.

The baseline also found concrete defects and changed preferred sources and compositions.
Therefore, it demonstrates feedback use, alongside a mistaken objective and weak causal interpretation.
The audit does not establish that one manager or generator is generally better than another.

Implementation: `UiGoalLoopPursuit.cs`, `UiGoalLoops.cs`, `goal.js`, `goal.css`, `goal.html`, `recap.html`, `recap-model.js`, `recap.js`, and `UiWorkflow.cs`.
Validation: `GoalLoopPursuitTests.cs`, historical `GoalLoopTests.cs`, and `tools/tests/goal-recap.test.cjs`.

Local validation on 2026-09-09 passed 104 C# goal/provider-stop tests and six recap tests.
JavaScript syntax checks and the whitespace audit also passed.
The isolated puppy test used one Sunburst source at low quality, Fable 5.1, and a Grok 4.6 critic.
Its first review distinguished visible features, gift clarity, composition, palette, and contextual interests.
The manager disagreed with the critic's success claim and requested clearer gift cues plus a simpler piano scene.
The test generated four images in approximately 18–28 seconds each.
Accepted critic calls took approximately 85–90 seconds each, showing the need for the planned lightweight worker layer.
One critic HTTP 520 failure required retrying the same critic against the saved images.
A Windows metadata replacement failure also paused the test without losing its generated results.
The completed pilot selected the simplified second-turn piano scene and retained the floral scene as a stated alternative.
Its final manager request carried three images: two current candidates and the exact earlier incumbent.
An initial success claim with nonempty remaining gaps failed validation; resume corrected that decision record against the saved images.
The manager still described source reliability too strongly after sparse examples; the structured record made that overstatement visible.
This pilot establishes workflow operation, not a general improvement in image quality or calibrated judgment.
Port 5961 hosts this isolated local revision; active work on port 5960 prevents restarting the original local server during validation.

## 1. Historical workflow (protocols 1–6)

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
| R1 (amended by R21) | Goal text in, one or more generators, one manager | New-loop form on `goal.html`. The generator picker accepts 1–8 checked generators (catalog order = source-letter order); the manager selector accepts exactly one. Unavailable targets remain absent from the picker; the shared settings dialog lists their availability problems. |
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
| R28a (2026-09-08) | Default model view | New users start with only SOTA on both shared pickers. Other views and full configuration remain accessible. |
| R28 (2026-09-05) | Shared generator chooser | Reuse the composer’s buttons, standard and personal groups, defaults, visibility settings, and configuration dialog. |

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

### Default model view (2026-09-08)

New users start with the **only SOTA** view in the composer and goal setup.
This view contains exactly six image generators:

| Display name | Catalog key |
|---|---|
| Nano Banana 2 | `google` |
| GPT Image 2.5 Sunburst | `gpt25-sunburst` |
| GPT Image 2.5 Flare | `gpt25-flare` |
| grok-web | `grok-web` |
| grok-api 2.0 | `grok-api-pro` |
| Ideogram V4 | `ideogram` |

The owner narrowed Grok membership to grok-web and grok-api 2.0.
The view reduces the choices that new users must inspect.
It applies with text, attached images, and composition maps.
Normal availability and input compatibility rules still apply.
Describers and other models remain outside this view.
Bulk controls act only on models in the current view.
Changing views removes selections outside the new view.
The **all models** button restores the configured catalog without selecting additional models.
Standard and personal group buttons provide direct access to their models.
Individually hidden models remain hidden until restored in configuration.
The configuration dialog lists every generator and describer, including unavailable endpoints.
Its default-view setting offers **only SOTA** and **all models**.
New default selections retain only existing default-on models within the SOTA set.
View membership does not make every displayed model default-on.
Saved preferences without a default view retain their previous **all models** behavior.
Existing users can select the new view or save it as their default.

The API adds `only-sota` to `standardGeneratorGroups` and publishes exact memberships through `standardGroupIds`.
Generator preferences add `defaultView`, accepting `only-sota` or `all`.
Account storage adds `ui_generator_preferences.default_view`, with `all` for existing records.
Browser storage and portable configuration preserve this field through the existing preferences document.
Temporary view changes last until reload; the saved default controls fresh pages.
Tests cover both pages, attachment behavior, account persistence, legacy preferences, and access to the complete catalog.

Implementation: `generator-chooser.js`, `app.js`, `goal.js`, `style.css`, `UiWorkflow.cs`, and `UiCommunity.cs`.
Validation: `tools/test-generator-chooser.cjs` and `UiCommunityTests`.

### Shared generator chooser (2026-09-05)

The goal setup and main composer use `generator-chooser.js` for generator chips and selection controls.
Both pages use the same standard groups from `/api/config` and the same editable personal groups.
Both expose Enable all, Disable all, Toggle all, Default, and the configuration dialog.
Group buttons show their eligible generators and replace the current selection with those generators.
Hidden and unavailable generators remain absent from the picker.
Goal loops also exclude videos, describe targets, and generators requiring an input image.
The configuration dialog retains the complete catalog so settings apply consistently across both pages.

Initial selection uses the shared configured defaults.
It no longer selects the first available generator independently.
Source letters follow catalog order after selection.
Groups can select more than eight eligible generators.
The count marks that excess, and submission requires the user to reduce the selection.
Never truncate a group silently to fit the limit.

The shared section-visibility setting also applies to goal setup.
Hiding the image section clears its active selection and leaves the settings button available.
Settings saves preserve the goal text, manager, critics, and output options.
They retain current generator selections where those generators remain visible.
Default selections take effect on a fresh page or through the Default button.

Authenticated preferences use the existing account endpoint.
Local preferences use the existing canonical personal configuration document.
A first goal-page save creates the complete document, preserving legacy browser fields.
Later saves update only its generator-preferences field.
Both pages read the shared settings when loaded; existing pages require refresh to receive changes from another page.
Portable configuration export and import retain the same schema and generator-preferences field.
Malformed preferences fail visibly through the existing validation.
Failed account saves leave the active chooser unchanged.

Per-endpoint extra text still applies only to composer jobs.
Goal-loop managers continue to own their exact prompts.
Private endpoint notes remain available in the shared chooser tooltip.
No loop API, prompt protocol, or running loop changes.

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
  Protocol 8 adds optional `maxCandidates` (1–3, default 3).
  Protocol 9 adds optional `samplesPerPrompt` (0/1/2/4, default 0: grok-web 2, others 1).
  Configuration publishes `goalLoop.protocolVersion`, `.defaultMaxCandidates`, and `.maxCandidatesCap`.
  It also publishes `.defaultSamplePolicy`, `.maxImagesPerRound`, and `.feedbackEnabled`.
  `/api/config.goalLoop.maxGenerators` and `.maxCritics` publish the caps.
- `POST /api/goal-loops/{id}/sheet` — build/rebuild the all-turns sheet;
  returns `{ sheetEntryCount, sheetTurns, url }`. 409 when nothing rendered.
- `GET /api/goal-loops/{id}/sheet` — the PNG (404 until built).
- `POST /api/goal-loops/{id}/stop`
- `POST /api/goal-loops/{id}/feedback` — form: `requestId` UUID, `scope` image/prompt, `entryIndex`, and `delta` +1/−1.
  Returns the persisted feedback operation. Requires control of the loop. Does not start or resume generation.
- `POST /api/goal-loops/{id}/resume` — optional `maxTurns`.
- `POST /api/goal-loops/{id}/fork` — `entryIndex`, optional `text`,
  optional `maxTurns`; returns the child id.

## 7. Files

- `MultiImageClient/Implementation/UiGoalLoopSampling.cs` — sample identities, source defaults, and image-budget validation.
- `MultiImageClient/Implementation/UiGoalLoopFeedback.cs` — durable preference events, idempotency, snapshots, and planning invalidation.
- `MultiImageClient.Tests/GoalLoopSamplingFeedbackTests.cs` — sample isolation, vote identity, replay protection, and planning timing.
- `MultiImageClient/Ui/wwwroot/goal-feedback.js`, `tools/tests/goal-feedback.test.cjs` — exact feedback totals and sample display checks.

- `MultiImageClient/Implementation/UiGoalLoopPursuit.cs` — stable rubric, component experiments, evidence, and completion validation.
- `MultiImageClient/Implementation/UiGoalLoopFanout.cs` — variable candidate contract, exact parent links, and disposition validation.
- `MultiImageClient.Tests/GoalLoopPursuitTests.cs`, `GoalLoopFanoutTests.cs` — experiment and branching contract coverage.
- `MultiImageClient/Ui/wwwroot/goal-tree.js` — exact render graph and optional thumbnail tree.
- `tools/tests/goal-tree.test.cjs` — branching, source identity, archived returns, and broken lineage checks.

- `MultiImageClient/Ui/wwwroot/generator-chooser.js` — shared chips, controls, groups, validation, and configuration dialog.
- `MultiImageClient/Ui/wwwroot/personal-config.js` — canonical chooser persistence, including first-visit browser migration.
- `tools/test-generator-chooser.cjs` — browser checks for both pages, persistence, visibility, groups, and selection limits.

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
