# Splitting one prompt into several images

Settled 2026-11-03. Updated 2026-11-10 (results group by image generator), 2026-11-19 (inline Stage-1 text step, parallel multi-writer splits up to 4 per account, explicit intermediate review tray with independent child image model selection and bail, feed split-group round-trip controls, and configurable writer models in personal configuration version 6), 2026-11-20 (on-demand multi-page series contact sheets, copy-all-pages, OpenAI JSON user-message fix, expanded parallel split/rewrite concurrency, duplicate in-flight guard + cancel-running, optional Stage-1 direct auto-render persisted in personal configuration version 7, shared series aspect ratio suggestions with per-card `Series AR` control, story-expansion support in `PromptSplit.SystemPrompt`, and built-in + personal instruction presets for both Split and Rewrite in personal configuration version 8), and **2026-11-26 (Stage-1 `Ctrl+Enter` routing, editable split review card part prompts with `splitEditedPrompts` validation, `sessionStorage` Stage-1 review tray persistence across page reload with `GET /api/prompt/exchange/{id}`, and `open in Stage 1` on saved splits in prompt history)**.

## Owner decisions (2026-11-26): Stage-1 keyboard shortcut, editable split review cards, tray persistence, and history reopen

1. **Mode-aware `Ctrl+Enter` / `Cmd+Enter` (`app.js`, `prompt-pipeline.js`):**
   - Pressing `Ctrl+Enter` / `Cmd+Enter` inside `#prompt` runs Stage 1 (`startStage1Run("ctrl_enter")`) whenever **Stage 1** is set to **Rewrite** or **Split multi-page**, instead of bypassing Stage 1 into direct image generation.
   - Pressing `Ctrl+Enter` / `Cmd+Enter` inside `#stage1-rewrite-instruction`, `#stage1-split-instruction`, or `#stage1-split-style` also runs Stage 1.
   - `#prompt`'s placeholder reflects the active mode (`Ctrl+Enter to send` / `Ctrl+Enter to rewrite` / `Ctrl+Enter to split`).
2. **Editable split part prompts in `#stage1-results` (`prompt-pipeline.js`, `PromptSplit.ResolveSubmission`, `POST /api/jobs`):**
   - Each part on a ready multi-part split review card renders in an editable `<textarea class="stage1-part-prompt">` with a live character counter and a per-part **reset** button to restore the model's exact split output.
   - Editing any part live-updates per-target prompt-limit checks, disables **Render** if any part is blank, and updates **copy all pages** immediately.
   - When any part was edited before rendering, `renderStage1Card` sends `splitEditedPrompts` (JSON string array in part order) to `POST /api/jobs`. `PromptSplit.ResolveSubmission` validates exact count, non-empty text, max length, and per-target prompt limits before creating the split jobs, failing closed on any violation.
3. **Reload-safe Stage-1 review tray (`prompt-pipeline.js`, `GET /api/prompt/exchange/{exchangeId}`):**
   - Ready Stage-1 split and rewrite cards (up to 8, excluding cards with unsaved local-only `File` blobs that lack a server `sourceJobId`) save compact references (`exchangeId`, selected generators, Series AR, edited part prompts / rewrite text) to environment- and account-scoped `sessionStorage` (`mic_stage1_tray_v1`).
   - On page load, `hydrateSavedStage1Tray()` restores those review cards from `GET /api/prompt/split/{exchangeId}` and `GET /api/prompt/exchange/{exchangeId}` (identity-scoped, non-split rewrite exchanges only) without re-calling any LLM. Dismissing via **Bail** or **Clear finished** removes the card from `sessionStorage`.
4. **Reopen saved splits from prompt history (`prompt-rewrites.js`, `prompt-pipeline.js`):**
   - Multi-part succeeded splits in **prompt history** show an **open in Stage 1** button (`window.openSavedSplitInStage1(exchangeId)`), placing the saved plan directly into `#stage1-results` for editing or rendering at zero LLM cost.

## Owner decisions (2026-11-20): story-expansion system prompt & built-in/personal instruction presets (personal config v8)

1. **`PromptSplit.SystemPrompt` story-development allowance (`MultiImageClient/TextLLMs/PromptSplit.cs`):**
   - Previously, `PromptSplit.SystemPrompt` forbade adding any story events, characters, or text not in the source and mandated using the fewest images—directly contradicting user instructions that ask the splitter to expand a short premise into an 8-page narrative comic.
   - Updated `PromptSplit.SystemPrompt` to preserve lossless splitting and fewest-images behavior by default, while explicitly honoring story-development instructions (requested page count, invented scenes, visual storytelling beats, cinematography, dialogue, and captions) and still keeping every detail from the source, visual continuity prefaces, character limits, and daytime lighting defaults.
2. **Built-in instruction presets for BOTH Stage-1 Split multi-page and Stage-1 Rewrite:**
   - **Split multi-page (`PromptSplit.BuiltinPresets`, exposed in `/api/config` under `promptSplit.presets`):**
     - `default` — **Faithful split (default)** (`PromptSplit.DefaultInstruction`)
     - `story-8-pages` — **8-page visual story (show, don't tell)** (`PromptSplit.StoryCraftInstruction`: multi-page visual narrative craft up to 8 pages, concrete actions/cinematography/dialogue/captions instead of abstract summary)
   - **Rewrite (`DirectedPromptRewrite.BuiltinPresets`, exposed in `/api/config` under `claudeAdvice.rewritePresets`):**
     - `default` — **Standard flesh-out (default)** (empty custom instruction)
     - `story-page` — **Multi-panel story page (show, don't tell)** (`DirectedPromptRewrite.StoryPageInstruction`: turns a short premise into one rich multi-panel comic/story page)
3. **User-saved personal instruction presets (personal configuration schema version 8):**
   - Inline preset selector, **save preset**, and **delete preset** controls sit directly beside `#stage1-split-instruction` (in Split mode) and `#stage1-rewrite-instruction` (in Rewrite mode), with an inline save-name bar for naming/overwriting presets without leaving the composer.
   - Stored in `promptTools.splitPresets` and `promptTools.rewritePresets` (arrays of `{ name, text }`, up to 20 presets each, unique case-insensitive trimmed names 1–60 chars, non-blank text 1–4,000 chars) inside the canonical portable personal configuration document (`personal-config.js` schema version `8`, migrated from version `7` via `migrateVersion7` setting both arrays to `[]`, and verified in `Ui/tests/personal-config.test.js`, `tools/test-prompt-split-browser.cjs`, and `tools/test-global-append-preload.cjs`).

## Owner decision (2026-11-20): consistent series aspect ratio (`shape` + per-card `Series AR`)

Production audit of 25 split groups across AVE and Vibecoders showed 21 of 25 (84%) left `output AR` on `auto`, producing mixed aspect ratios in 40 of 93 per-generator series (43%) and mid-story orientation flips in 26 of 93 (28%), versus 0% mismatches when an explicit shape was set.

1. **Splitter frame awareness and recommendation (`PromptSplit.cs`, `POST /api/prompt/split`):**
   - `POST /api/prompt/split` accepts `shape` (`auto` or any `UiShapeMapping.Shapes` key, validated fail-closed) and passes both `shape` and `imageAttached` into `PromptSplit.BuildUserMessage`.
   - `PromptSplit.SystemPrompt` and `PromptSplit.BuildUserMessage` add `"shape"` (`"square" | "landscape" | "portrait" | "wide" | "tall" | "auto"`) to the strict JSON reply contract.
   - When the user selected a fixed composer `output AR`, the splitter composes every page for that frame and returns that shape. When `output AR` is `auto` with no attached image, the splitter chooses one consistent shared shape for continuous stories/comics/sequences (allowing `"auto"` only for unrelated subjects or 0/1-image replies). When an input image is attached with `output AR = auto`, the splitter keeps `"auto"` (`match input image`).
   - `ParseReply` requires a valid canonical `"shape"` on every multi-image reply (`imageCount >= 2`) and validates it strictly if present on 0- or 1-image replies. `ParseStored` accepts `"shape"` as optional so legacy stored splits remain readable.
2. **Per-card editable `Series AR` selector (`prompt-pipeline.js`):**
   - Every multi-part Stage-1 split card (and **re-render plan** card) displays a `Series AR` dropdown populated from server `cfg.shapes`.
   - Precedence: (a) an explicit composer `output AR` (`!= "auto"`) always wins; (b) an attached-image `auto` run stays on `match input image` (`auto`) so edit targets (Ideogram 4.5/4.6 Precise Edit, Recraft, `grok-web chat`) remain compatible; (c) otherwise, a text-only `auto` split initializes `Series AR` to the splitter's `suggestedShape` (labeled `(suggested)`), and auto-render submits that shape automatically.
   - The user may change `Series AR` to any shape or back to `auto (vary per page)` before rendering (or re-render the same plan in another shape at zero LLM cost). Card-level `aspectIncompatible` checks follow the card's `Series AR`.

## Owner decisions (2026-11-20): direct auto-render, OpenAI JSON fix, and parallel split reliability

1. **Direct auto-render toggle (`#stage1-auto-render`, personal configuration version 7):**
   - Sits directly beside `#stage1-run` inside `#stage1-actions` for both **Split multi-page** and **Rewrite** modes (`promptTools.stage1AutoRender`, boolean, default `false`, migrated from v6 via `migrateVersion6`).
   - When unchecked (default), Stage-1 runs stop at the `#stage1-results` review tray until the user clicks **Render**.
   - When checked, `#stage1-run` displays the live count of selected image models (`& render (N)`), requires at least one selected image model before starting, captures the checked image models and output options at click time, and automatically submits each text model's finished output to `POST /api/jobs` as soon as it arrives (all split pages for `imageCount >= 2`, the unsplit prompt for `imageCount == 1`, or the rewritten prompt for Rewrite). Declined splits (`imageCount == 0`), aspect-ratio removals leaving zero targets, and prompt-limit failures fail closed without submitting and surface the error on the Stage-1 card.
   - The Stage-1 card still appears in `#stage1-results` for visibility, **copy all pages**, **Use in prompt**, or re-rendering to different models.
2. **OpenAI `/v1/responses` JSON contract fix (`PromptSplit.BuildUserMessage` and `PromptSubjectSynthesis.BuildSynthesisUserMessage`):**
   - OpenAI's `/v1/responses` endpoint rejects `text.format: { type: "json_object" }` with HTTP 400 unless a user-role input message mentions `JSON`. Both `PromptSplit.BuildUserMessage` and `PromptSubjectSynthesis.BuildSynthesisUserMessage` now explicitly state the required JSON object keys in the user message (also reminding split models to include `"images": []` on 0- or 1-image replies).
3. **Parallel split/rewrite concurrency and duplicate guard:**
   - Raised `PromptSplit.MaxConcurrentSplitsPerAccount` from `4` to `8`, `PromptSplit.MaxConcurrentSplits` from `8` to `16`, and `DirectedPromptRewrite.Slots` from `2` to `8`, preventing HTTP 409/502 slot collisions when running 3–4 parallel Stage-1 writers or starting a second batch while slower models finish.
   - `startStage1Run` skips firing duplicate HTTP requests for models already running on the exact same mode, prompt, instruction, and style note.
   - `#stage1-results-head` exposes **cancel running** (`#stage1-results-cancel-running`) while any Stage-1 card is in flight to abort active requests and free server slots immediately.

## Owner decisions (2026-11-20): series contact sheets and copy-all-pages

1. **Multi-page series contact sheets (`GET /api/split-groups/{groupId}/contact-sheet?text=full|none&gen=<optional>`):**
   - Renders one combined PNG for an entire completed split series on demand (same architecture as `UiDescribeSheet`; zero automatic disk/RAM/B2 cost unless opened).
   - Multi-generator series (`M >= 2`) render a **Generator rows × Page `1..P` aligned columns** matrix with page header bands, generator bands, optional gold-framed `INPUT` strip, multi-image sub-grids inside cells (e.g. `grok-web` side-by-side), and clipped red error boxes for failed cells. Single-generator series (or per-model `?gen=<key>`) render a balanced grid (`1..4` pages in one row; `5..8` pages in two balanced rows).
   - `text=full` appends a **Full text** section beneath the images showing every page's verbatim prompt (`Page i of P · {title}`) plus a metadata panel with the original source prompt, split reason, style note, and endpoint extra text. `text=none` omits the full text section and keeps only compact structural labels, sizes, and timings.
   - Feed `.split-group` headers expose **contact sheet (full text)** and **contact sheet (no text)** once all `count` parts finish and none are hidden; multi-generator groups also show inline **sheet (full text)** and **sheet (no text)** links on each `.split-maker` heading.
   - Memory and fail-closed safety: runs through `_finalizationLimit` (HTTP 503 if busy) and `PixelWorkGate` (one full-size pixel task per process); single-canvas two-pass renderer steps `cellMaxEdge` down (`1024..256`) to stay within 32,000,000 pixels (~128 MiB RGBA); decodes one cell at a time via `Raster.DecodeForBox` (or `SvgRaster.Rasterize` for SVGs); fetches B2-evicted originals with SHA-256 verification via `UiJobRunner.TryGetImageBytesIncludingHostedAsync`; checks prompt/image visibility before and after rendering (HTTP 404 if any part or image is hidden); rejects unfinished groups with HTTP 409, invalid `text`/`gen` with HTTP 400, and corrupt/oversized data with HTTP 422.
   - Works for archived and evicted split series via `SplitGroupId` indexed in `UiHistoryIndexEntry` and `UiHistoryIndexCache` schema version 2 (existing v1 caches automatically reset and rebuild from disk `job.json`).
   - Per-part `grid` contact sheets stay intact for backwards compatibility and public sharing.
2. **Copy all pages as one text block:**
   - Both Stage-1 split review cards (`.stage1-card-copy-all`, when `imageCount >= 2`) and feed `.split-group` headers (`.split-group-copy-all`) expose a single **copy all pages** button.
   - Formats pages in page order (`1..P`) as `Page {n} ({title}):\n{prompt}` (or `Page {n}:\n{prompt}` when untitled), separated by blank lines (`\n\n`), and writes to the clipboard with inline status confirmation or a clear failure alert.

## Owner decisions (2026-11-19): Stage-1 text step, intermediate review tray, and split-group round-trip

1. **Inline Stage-1 text step (`#stage1-bar`):** Sits directly above `#endpoint-picker` in `#composer`. Starts collapsed in **Direct** mode so normal generation stays one click. Switching to **Rewrite** or **Split multi-page** (or clicking **consider splitting into multiple images** under `#prompt`) reveals inline controls: a writer picker starting with the user's saved default (plus **make default** when changed and **+ add model** for up to 4 parallel text models), the split instruction (`use default`), and the optional shared style note. Both texts stay synced with `promptTools.splitInstruction` / `promptTools.splitStyleNote` in personal configuration version 6. Supersedes the former modal `#prompt-split-dialog`.
2. **Explicit intermediate review step (`#stage1-results`):** Clicking **Split with …** runs every selected text model in parallel (`POST /api/prompt/split`; concurrency raised to `MaxConcurrentSplitsPerAccount = 4` / `MaxConcurrentSplits = 8`; empty `generators` allowed using `DefaultUnconstrainedPartBudget = 10_000`) and creates one independent review card per text model. **No image generation starts automatically.** Supersedes the 2026-11-03 auto-submit behavior.
3. **Independent handling per split card:** Each review card shows its live elapsed timer and `cancel` while running, then its part count, model reason, and full part prompts. Each card carries its own child image model picker (initialized from the composer's checked image generators, with `sync from composer`, `SOTA`, and `none`), live prompt-limit checks against those chosen models, **Render P parts × M models** (reusable on different child models at zero extra LLM cost), **retry split**, and **Bail** (dismiss without generating). One-image recommendations (`imageCount == 1`) offer **Render unsplit × M models**, **retry split**, and **Bail**.
4. **Feed split-group round-trip controls:** Every `.split-group` header in live and archive feeds exposes **source prompt** (lazily fetches `GET /api/prompt/split/{exchangeId}?jobId=...` to show the original prompt, instruction, and style note), **re-split** (restores source prompt, inputs, instruction, style note, and writer model into Stage-1 Split mode), **re-render plan** (places a ready-to-render card in `#stage1-results` at zero LLM cost; `POST /api/jobs` verifies cross-member visibility via `splitSourceJobId`), and **set source active** (loads the original unsplit prompt and input images into `#prompt`).
5. **Frontend modularization:** Prompt pipeline and feed split-group round-trip code live in `MultiImageClient/Ui/wwwroot/prompt-pipeline.js`; activity center, sketch composer, and image viewer were split out of `app.js` into `activity-center.js`, `sketch-composer.js`, and `image-viewer.js`.

## Owner request (2026-11-03, modal and auto-submit superseded 2026-11-19)

The owner asked for one more AI help box: **consider splitting this into multi-image**.
It opens a text box with a default instruction.
The user can add a general style note.
Opus 5.5 decides how many images the prompt needs and writes the prompt for each image.
Each prompt starts with shared preface text, so the images keep one style.
For example, a comic prompt with 20 panels can become 4 prompts instead of one crowded image.
Every part goes to the backend automatically.
The global N setting counts as 1, because the split already makes several images.
The feed shows each part's results as they arrive.
The user can step through all parts in their logical order.

The default instruction is the owner's sample, with its spelling corrected and "rec" written out:

> The following prompt may be better served by having 2, 3, or more images. Please make a decision about that and return your recommendation.

## Owner request (2026-11-10): group results by image generator

Before this change, each part card held one result cell per image generator.
The feed therefore grouped results by part.
With three generators, one generator's pages sat in three separate cards.
The owner asked for one row per image generator instead:

> endpoint A: page 1,2,3 of results
> endpoint B

Each generator's pages now read side by side, in part order.
The viewer follows the same order.

## Requirements and behavior

| Requirement | Behavior |
|---|---|
| Button | **consider splitting into multiple images** sits in the prompt tools under the prompt, after the flesh-out buttons. |
| Dialog | Shows the instruction and an optional style note. **use the default instruction** restores the default. |
| Saved text | Both texts persist in personal configuration version 5. Each edit saves immediately. |
| Model | Opus 5.5 (`claude-opus-5-5`). The dialog's submit button reads **ask Opus 5.5**. |
| Decision | Opus 5.5 chooses 1 to 8 images and gives a reason. |
| One image | Nothing is sent. The progress line shows the reason. The prompt stays unchanged. |
| Several images | One job per part starts at once, in part order. Each job's prompt is its part's prompt. |
| Shared style | Every part prompt begins with the same preface, including the style note. |
| N | Every part runs with N = 1 on each selected image generator. The N setting does not apply. |
| Inputs | Every part receives all attached images, the same generators and options, and the same endpoint extra text. |
| Feed | The parts appear in one group. Each image generator has one row with parts 1 to k in order (2026-11-10). Results fill in as each generator finishes. |
| Navigation | The image viewer steps through one generator's parts from 1 to k, then the next generator's (2026-11-10). Its generator line names the part. |
| Waiting | The dialog closes at once. A progress line shows elapsed seconds and **cancel split**. The composer stays usable. |
| History | Prompt history keeps every split, including failures. The source prompt can be restored. Parts are read-only. |
| Fail closed | A malformed reply, a part over the limit, or a changed prompt starts nothing and shows the error. |

## Declines (2026-11-10)

On 2026-11-10, Opus 5.5 declined one local split on content grounds.
Its reply was complete JSON: `imageCount` 0, a reason, and an empty `images` list.
The request used 1,543 input and 566 output tokens. The provider reported no early stop.
The contract then allowed only 1 to 8, so the page showed a format error.
That error read "Opus 5.5's reply must give imageCount as a whole number from 1 to 8."
It suggested a defect and hid Opus's reason.

Now the system prompt defines this reply as a decline.
The error quotes the reason, and nothing is sent.
The prompt box keeps the source prompt. **Generate** still sends it as one job.
Nothing falls back to the unsplit prompt on its own.
Prompt history records the decline as a failed split with the full reply.

## Model, cost, and call budget

- The request uses the manager catalog entry `manager-claude-opus-5-5`: Anthropic Messages with adaptive thinking.
- The output cap is 64,000 tokens. The HTTP timeout is 15 minutes. Goal-loop managers use the same values.
- The prompt-rewrite transport allows 16,000 output tokens. Eight long parts need more.
- Opus 5.5 costs $4 per million input tokens and $20 per million output tokens.
- Estimate: a 10,000-character source with the system prompt is about 3,000 input tokens, or about $0.01.
- Four parts of 3,000 characters each are about 3,000 output tokens, or $0.06, before thinking.
- Adaptive thinking adds output tokens. The 64,000-token cap limits output to $1.28 per request.
- No live request was measured. The tests use mocked replies.

Call budget:

- Only a dialog submit calls Opus 5.5. Page loads, reloads, extra tabs, and polling never call it.
- The server runs at most one split per account and two per server process.
- An excess request receives HTTP 409 and makes no call.
- The server never retries a failed request.
- **start the jobs again** reuses the stored plan. It makes no Opus call.
- Every dialog submit asks Opus 5.5 again, even for an unchanged prompt.
- A submit is an explicit request for a new decision, and replies vary.
- Condensing differs: it reuses an identical stored result, because it only needs a fitting text.

## Split request

`POST /api/prompt/split` takes a URL-encoded form:

| Field | Meaning |
|---|---|
| `prompt` | The exact source prompt, 1 to 100,000 characters. |
| `instruction` | The decision instruction, 1 to 4,000 characters. |
| `styleNote` | Optional, at most 2,000 characters. |
| `user` | The creating-as name, resolved like a job's creator. |
| `generators` | Comma-separated image generator keys. Describe and layout-map keys fail. |
| `generatorExtraTexts` | Required JSON of each target's endpoint extra text, as for jobs. |
| `imageAttached` | `true` when the composer has an attached image. |

The server computes a part budget before the call:

1. Take each target's published prompt limit. Use the image-job limit when the target receives the image.
2. Use 32,000 characters, the GPT image limit, for a target without a published limit.
3. Subtract the target's endpoint extra text and its two-character separator.
4. Take the smallest result.

A budget under 100 characters fails before the call.
Grok-web text-to-image truncation does not count as a limit.
Grok-web text-to-image therefore still cuts a part over 8,192 UTF-8 bytes, as for any prompt.

The user message holds the instruction, the style note, the limits, and the source prompt.
The source prompt sits inside `<source_prompt>` tags.
The system prompt (`PromptSplit.SystemPrompt`) sets these rules:

- Return only one JSON object with `imageCount`, `reason`, and `images`.
- Return one image with an empty `images` list when one image serves the source best.
- Return `imageCount` 0 with an empty list and a reason when it will not write prompts for the source (2026-11-10).
- Otherwise write exactly `imageCount` images in viewing order, each with `title` and `prompt`.
- Use the fewest images that keep every part clear. One image can still hold several comic panels.
- Begin every prompt with the same shared preface, because each model sees one prompt alone.
- Keep every subject, name, count, action, and requested on-image text. Copy dialogue and captions word for word.
- Put each piece of content in exactly one image. Add no events, characters, or text.
- For sequences, state which panels or steps each image shows, their order, and their layout.
- Keep stated lighting. Without stated lighting, ask for clear, bright, full daytime light.
- Write in the source prompt's language, within the character limit.

The server checks the reply strictly:

- It removes a Markdown fence at either end. Any other text outside the JSON fails.
- The object has exactly the three keys. Each image has exactly `title` and `prompt`.
- `imageCount` is a whole number from 0 to 8. A stored plan accepts only 1 to 8.
- Zero declines the split and requires an empty list. One image also requires an empty list.
- A larger count requires exactly that many entries.
- A decline fails with "Opus 5.5 declined to split this prompt, so nothing was sent. Its reason: …".
- A decline shows no provider hint. Its reason is model text, which can contain billing or key words.
- `reason` is text of 1 to 1,000 characters.
- A title is one line of at most 80 characters.
- A prompt over the part budget fails. The server never truncates it.
- A provider stop before a complete reply fails.

A success returns `{exchangeId, model, modelLabel, partBudget, imageCount, reason, images:[{title, prompt}]}`.
A failed call returns HTTP 502 with `error`, `exchangeId`, and any provider action hint.
Invalid input returns HTTP 400.

## Records

- Each request is stored in `ui_claude_prompt_exchanges` with `purpose = 'split'`.
- Failed and cancelled requests are stored too.
- `instruction` holds the instruction, followed by "Style note: …" when the user gave one.
- `result_prompt` holds the plan JSON. `raw_response` holds the reply text.
- Prompt history labels the entry **Split into images · Opus 5.5**.
- Its source reads **Split source · exact prompt** and keeps its restore button.
- Each part shows as **Part i of k · title** with its full prompt and no restore button.
- The server refuses to restore a split's result, because it holds several prompts.
- **set active** on a part's job card loads that part into the composer.
- Character writing reads only prompt edits (`purpose = ''`), so it skips plans.
- Part prompts reach character writing through job history.

## Job intake

The composer sends the plan to `POST /api/jobs` with the usual fields plus `splitExchangeId`.
The server rejects the whole request with HTTP 400 unless all of these hold:

- The environment's prompt-rewrite switch is on.
- `n` is exactly `1`.
- The request has no `condensedPrompts` field and no composition sketch.
- The exchange belongs to the submitting account and has the split purpose and the Opus 5.5 model.
- The exchange succeeded and was made from the exact job prompt.
- The plan has at least two images.
- Each generator is an image generator, named once.
- Every part, with each target's extra text, fits that target's limit.

The server admits all parts to the job queue before any part starts.
Without room for all of them, it returns HTTP 503 with "Nothing was started".
It then creates the jobs in part order.
Each submission gets a new 12-character group id.
A resubmitted plan therefore forms a separate group.

The response is `{jobs:[{id, split}], user, originalUser, ownerId}`.
An ordinary job keeps its `{id, user, originalUser, ownerId}` response.

Each part job stores `split`: `{groupId, exchangeId, index, count, title, model, modelLabel, reason}`.
`index` starts at 1.
`job.json` persists it.
`job-known` events, `GET /api/jobs`, and archive days carry it.

## Composer

- The dialog submit captures the prompt, username, image generators, extra text, and attached images.
- It also captures shape, detail, quality, and moderation.
- Later composer edits do not change what the parts receive. The prompt box keeps the source prompt.
- The button refuses an empty prompt, a missing username, an attached sketch, or no selected image generator.
- A sketch lays out one image, so it blocks splitting.
- Selected describe targets do not run on the parts. The dialog says so.
- The dialog names the image generators and states that N does not apply.
- It also states that every part receives the attached images.

The progress line sits under the prompt tools. It reports each stage:

- Waiting: "Opus 5.5 is deciding how many images this prompt needs.", elapsed seconds, and **cancel split**.
- Starting: "Opus 5.5 planned k images. Starting k jobs…"
- Done: "Opus 5.5 split the prompt into k images. Below, each image generator shows parts 1 to k in order."
- One image: "Opus 5.5 recommends one image, so nothing was sent.", followed by the reason.
- Declined, as an error: "Opus 5.5 declined to split this prompt, so nothing was sent. Its reason: …"

- **cancel split** aborts the request. The server then cancels the Opus call.
- A cancelled split sends nothing. Prompt history records it as failed.
- A username change during the call starts no jobs. The plan stays in prompt history.
- When the jobs request fails, **start the jobs again** resends the same request with the same plan.
- The split button stays disabled while a split runs in the page.
- After the jobs start, the unsubmitted-edit highlight clears, as after **Generate**.

## Feed and viewer

Updated 2026-11-10: results group by image generator, not by part.

- The parts share one `section.split-group`.
- Its heading reads "Opus 5.5 split one prompt into k images". The reason follows.
- A line of part heads comes next, in part order, whatever order the parts arrive in.
- Each part head shows **part i of k · title**, the part's prompt, and its controls.
- Each part's contact-sheet link sits under its part head.
- Each image generator then gets a heading with its name and one row of result cells.
- A row holds that generator's cells for parts 1 to k, in part order.
- Part i's cell sits in the same column as part i's head.
- Each cell names its part as **part i**. The row heading names the generator.
- Rows follow the job's generator order, which is the chooser order at submission.
- Part prompts show at most six lines at every screen width. **show full prompt** reveals the rest.
- Every column is at least 240 px wide. Heads, links, and rows share the same columns.
- When k columns do not fit, the parts wrap onto lines of balanced length.
- For example, five parts with room for four columns form lines of three and two.
- A phone shows one column: the part heads, then each generator's parts in order.
- A part hidden by the night or people filter leaves its column empty.
- The other parts keep their columns, so positions stay stable when a filter changes.
- A group hides when the night or people filters hide all its parts.
- The live feed puts a new group at the top when its first part arrives.
- Archive days put a group where its first part falls.
- The image viewer walks a group the way its rows read: generator A's parts 1 to k, then generator B's.
- Left and right arrows step through every image in that order.
- Ctrl+Left and Ctrl+Right jump between cells in the same order. Each jump lands on the cell's first image.
- The viewer's generator line adds "· part i of k · title".
- Session cost totals include parts inside groups.
- Tab focus still moves part by part: a part's head, then its cells, then the next part.
- Only the screen order and the viewer walk follow the generator rows.

How the layout works:

- Each part card and its cell list use `display: contents`.
- Their heads, links, and cells therefore become items of the group's CSS grid.
- Each cell stays inside its own job card in the page source.
- Per-job code, filters, deletion, and event replays therefore work unchanged.
- `app.js` sets `--split-column` and `--split-line` on each card, and `order` on each cell and generator heading.
- A `ResizeObserver` recomputes the column count when the group's width changes.
- CSS `reading-flow: grid-rows` did not put Tab focus in screen order in Chromium 156.
- With it, Tab visited part 1's cells twice. The page therefore does not use it.

## Personal configuration version 5

- `promptTools` gains `splitInstruction` (at most 4,000 characters) and `splitStyleNote` (at most 2,000).
- Both fields live in the browser document. Account storage does not hold them.
- An empty `splitInstruction` means the server default, from `/api/config` `promptSplit.defaultInstruction`.
- Text equal to the default is saved as empty. A later default then reaches users who never changed it.
- An explicit version-4 migration adds both fields as empty.
- The migration rejects a version-4 document that already has either field.
- Unknown versions and missing or unknown fields still fail.

## Environment switch and configuration

The environment's prompt-rewrite switch also controls splitting.
It covers the route, the `splitExchangeId` job field, the button, and the progress line.
`/api/config` `promptSplit` reports `available`, `availabilityProblem`, `model`, and `label`.
It also reports `defaultInstruction`, `maxImages`, `maxInstructionChars`, and `maxStyleNoteChars`.
Availability depends on the Anthropic key.

## Agent decisions (2026-11-03)

The request left these points open. The agent chose them:

- Use the manager catalog client for its 64,000-token output cap.
- Allow at most 8 images per split.
- Run one split per account and two per server.
- Ask Opus 5.5 again on every submit, without reuse.
- Block splitting while a composition sketch is attached.
- Skip describe targets on the parts.
- Send every attached image to every part.
- Admit all parts together, or none.
- Give each submission its own feed group.
- Correct the spelling of the sample instruction.

## Agent decisions (2026-11-10)

The generator-row request and the first decline left these points open. The agent chose them:

- Keep each cell inside its part's job card and place it with the CSS grid.
- About 20 code paths find a cell through its card, and filters hide whole cards.
- Moving cells into separate row elements would break those paths.
- Put the part heads in one line above the rows, aligned with the part columns.
- Label each cell **part i**, and label each row with the generator's name.
- Clamp part prompts to six lines at every width, so long prompts do not push the rows down.
- Use a 240 px minimum column width and lines of balanced length.
- Leave a hidden part's column empty, so parts keep their places when a filter changes.
- Make the viewer and Ctrl+arrows follow the rows. Leave Tab focus part by part.
- Change the done message to name the new order.
- Define `imageCount` 0 as an explicit decline, because Opus 5.5 chose that form unprompted.
- Quote the decline reason in the error and send nothing. Show no provider hint for a decline.

## Files

- `TextLLMs/PromptSplit.cs`: contract, system prompt, part budget, user message, reply parser, stored plan, intake verification, concurrency limits, and job metadata.
- `Workflows/UiWorkflow.cs`: `/api/config` entry, environment switch, split route, restore refusal, job intake, and the `GET /api/jobs` metadata.
- `Implementation/UiJobs.cs`: `UiJob.Split`, its `job.json` persistence, and the `job-known` metadata.
- `Implementation/UiCommunity.cs`: the `purpose` value for splits.
- `Ui/wwwroot/app.js`: dialog, request capture, progress line, job submission, feed groups, generator rows, column layout, part labels, viewer walk order and label, and cost totals.
- `Ui/wwwroot/index.html` and `style.css`: button, dialog, progress line, and the group's grid of part heads and generator rows.
- `Ui/wwwroot/personal-config.js`: version 5 and `migrateVersion4`.
- `Ui/wwwroot/prompt-rewrites.js`: history entries for splits.
- `Ui/wwwroot/environment-storage.js`: hides the button and progress line when prompt rewriting is off.
- `MultiImageClient.Tests/PromptSplitTests.cs`: reply contract, part budget, user message, stored plan, provider failures, concurrency, intake verification, and restarts.
- `Ui/tests/personal-config.test.js`: version 5 migration and rejection.
- `Implementation/UiSplitContactSheet.cs`: fail-closed split-series reader and single-canvas two-pass PNG renderer (`text=full` and `text=none`, all models or one `?gen=...`, bounded at 32 MP inside `PixelWorkGate`).
- `Implementation/UiJobStorage.cs` and `Implementation/UiHistoryIndexCache.cs`: index `SplitGroupId` in SQLite cache schema v2 and expose `UiJobRegistry.ListBySplitGroup(groupId)` for live and archived series.
- `Implementation/UiJobRunner.cs` and `Workflows/UiWorkflow.cs`: `CreateSplitContactSheetAsync` and `GET /api/split-groups/{groupId}/contact-sheet`.
- `Ui/wwwroot/prompt-pipeline.js`: Stage-1 bar/cards, `formatSplitPagesCopyBlock`, **copy all pages** buttons, series contact-sheet links (`refreshSplitGroupSheets`), and split-group round-trip controls.
- `MultiImageClient.Tests/SplitContactSheetTests.cs`: unit tests for fail-closed reading, full-text vs no-text rendering, multi-model vs single-model layouts, 32 MP cap, archived lookup, and SQLite v1→v2 upgrade.
- `tools/test-prompt-split-browser.cjs`: browser test with mocked Opus and job replies, including copy-all-pages, series contact-sheet gating, and direct prompt field actions.

The same change gives the advice dialog's textarea normal weight (400).
It had inherited bold (700) from its label, so the label was not heavier than the text.

## Verification (2026-11-03)

- The full solution passed 692 C# tests.
- The JavaScript unit tests passed 46 tests. The deploy tests passed 26.
- `tools/test-prompt-split-browser.cjs` passed with mocked Opus and job replies.
- It covers saved and imported dialog text, failure, one image, cancel, retry, groups, part order, and viewer order.
- Eight composer browser tests passed against the local server.
- Two of them failed before this change. Their fixtures were fixed:
  - `test-generator-chooser.cjs` lacked `promptSubjects` since the characters change of 2026-10-15.
  - `test-global-append-preload.cjs` loaded the composer in its shared-viewer page. Its startup then wrote to a replaced document.
- No live Opus 5.5 request was made.

## Verification (2026-11-10)

- `tools/test-prompt-split-browser.cjs` now selects two image generators.
- It checks the generator headings, the **part i** cell labels, and the column positions at 1400, 760, and 390 px.
- At 760 px it checks that part 3 wraps under part 1 in every row.
- It checks that a hidden part leaves its column empty and that the viewer skips it.
- It checks the viewer walk with arrows and Ctrl+arrows, including a cell with two images.
- The test passed with mocked Opus and job replies.
- The full solution passed 713 C# tests. The JavaScript unit tests passed 46; the deploy tests passed 26.
- Eight other browser tests passed: phone layout, text entry, server restart, idle polling, drafts, chooser, visibility, and FableBot viewer.
- `test-share-links.cjs` passed with Playwright's bundled Chromium 156.
- With Google Chrome 157 it failed one unrelated step: a 404 with an empty body became a navigation error.
- Declines: `PromptSplitTests` checks the quoted reason, the `Declined` flag, and the missing provider hint.
- It also checks that a decline with images or without a reason fails, and that stored plans reject 0.
- After the decline change, the full solution passed 714 C# tests. No live Opus 5.5 request was made.
