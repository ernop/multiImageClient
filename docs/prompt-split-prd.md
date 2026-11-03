# Splitting one prompt into several images

Settled 2026-11-03.

## Owner request (2026-11-03)

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
| Feed | The parts appear in one group, in part order. Results fill in as each generator finishes. |
| Navigation | The image viewer steps from part 1 to the last part. Its generator line names the part. |
| Waiting | The dialog closes at once. A progress line shows elapsed seconds and **cancel split**. The composer stays usable. |
| History | Prompt history keeps every split, including failures. The source prompt can be restored. Parts are read-only. |
| Fail closed | A malformed reply, a part over the limit, or a changed prompt starts nothing and shows the error. |

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
- `imageCount` is a whole number from 1 to 8.
- One image requires an empty list. A larger count requires exactly that many entries.
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
- Done: "Opus 5.5 split the prompt into k images. Parts 1 to k run below, in order."
- One image: "Opus 5.5 recommends one image, so nothing was sent.", followed by the reason.

- **cancel split** aborts the request. The server then cancels the Opus call.
- A cancelled split sends nothing. Prompt history records it as failed.
- A username change during the call starts no jobs. The plan stays in prompt history.
- When the jobs request fails, **start the jobs again** resends the same request with the same plan.
- The split button stays disabled while a split runs in the page.
- After the jobs start, the unsubmitted-edit highlight clears, as after **Generate**.

## Feed and viewer

- The parts share one `section.split-group`.
- Its heading reads "Opus 5.5 split one prompt into k images". The reason follows.
- The group lists the parts in part order, whatever order they arrive in.
- Each part card shows **part i of k · title** above its job head.
- The live feed puts a new group at the top when its first part arrives.
- Archive days put a group where its first part falls.
- A group hides when the night or people filters hide all its parts.
- The image viewer walks cards in page order. Arrow keys step from part 1 to the last part.
- The viewer's generator line adds "· part i of k · title".
- Session cost totals include parts inside groups.

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

## Files

- `TextLLMs/PromptSplit.cs`: contract, system prompt, part budget, user message, reply parser, stored plan, intake verification, concurrency limits, and job metadata.
- `Workflows/UiWorkflow.cs`: `/api/config` entry, environment switch, split route, restore refusal, job intake, and the `GET /api/jobs` metadata.
- `Implementation/UiJobs.cs`: `UiJob.Split`, its `job.json` persistence, and the `job-known` metadata.
- `Implementation/UiCommunity.cs`: the `purpose` value for splits.
- `Ui/wwwroot/app.js`: dialog, request capture, progress line, job submission, feed groups, part labels, viewer label, and cost totals.
- `Ui/wwwroot/index.html` and `style.css`: button, dialog, progress line, and group layout.
- `Ui/wwwroot/personal-config.js`: version 5 and `migrateVersion4`.
- `Ui/wwwroot/prompt-rewrites.js`: history entries for splits.
- `Ui/wwwroot/environment-storage.js`: hides the button and progress line when prompt rewriting is off.
- `MultiImageClient.Tests/PromptSplitTests.cs`: reply contract, part budget, user message, stored plan, provider failures, concurrency, intake verification, and restarts.
- `Ui/tests/personal-config.test.js`: version 5 migration and rejection.
- `tools/test-prompt-split-browser.cjs`: browser test with mocked Opus and job replies.

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
