# Prompt length limits

## Decision — 2026-10-12

A target with a published prompt limit never receives an over-limit prompt.
The server checks each target's exact wire prompt before the provider request.
The wire prompt is the trimmed prompt, a blank line, and the endpoint extra text.
The check counts Unicode characters (code points).
An emoji therefore counts as one character.
A provider that counts UTF-16 units or bytes reaches its limit no earlier than this count.
The check therefore never rejects a prompt that the provider accepts.
An over-limit target fails with a clear error and receives nothing.
The other selected targets of the job still run.
Since 2026-10-26 the user can condense the prompt for the over-limit targets only.
See [Condensing for over-limit targets](#condensing-for-over-limit-targets-2026-10-26).
Grok-web alone truncates its text-to-image prompt, to 8,192 UTF-8 bytes.
Its edits send the full prompt.
That truncation is an owner requirement; see [grok-web-prompt-limits.md](grok-web-prompt-limits.md).
The server does not check targets without a published limit.
Their providers decide, and their rejections remain visible errors.

### Line breaks (2026-10-26)

A browser sends each prompt line break as CR LF, two characters, in the multipart job request.
Chrome 154 did this in a 2026-10-12 check.
The text box itself holds LF only.
The job and video-job routes store the prompt with LF, through `UiJobRunner.PromptFromFormField`.
Each prompt line break therefore counts as one character, as in the text box.
Providers receive LF.
Endpoint extra text travels inside a JSON field and keeps its LF line breaks.
The composer measures both parts the same way, so its notice matches the server's decision.
`PromptLimitTests.FormPromptReachesTheJobWithLfLineBreaks` fails if the server stores CR LF again.
Job records from before 2026-10-26 keep CR LF.

Superseded: from 2026-10-12 to 2026-10-26 the server kept CR LF and counted each line break as two characters.
That choice matched the composer to the server, but it spent one provider character per line break.
On 2026-10-26 the composer reported a prompt as 10,076 characters against seven 10,000-character limits.
The submitted version of that prompt, local job `25b333ad4982`, had 236 line breaks.
With 236 line breaks, the reported draft held 9,840 characters in the text box.
That is within every 10,000-character limit.
The owner shortened the prompt and deselected Recraft V3 to pass the inflated check.

### Every target is classified

`PromptLimitTests.EveryUiTargetHasALimitOrAListedReason` reads every UI target key by reflection.
Each key needs a limit in `UiJobRunner.MaxPromptCharsFor` or a `KeysWithoutAPublishedLimit` entry with its reason.
A new target without either fails the test, so it cannot reach production unclassified.
`ListedTargetsHaveNoLimitOnEitherRoute` fails when a listed target later gets a limit.

### Recraft no longer truncates

`RecraftGenerator` previously cut every prompt over 1,000 characters to 990 characters.
It did this on every Recraft model and logged one line.
The job card showed the full prompt, although Recraft received only the first 990 characters.
No product requirement asked for this truncation, so it broke the fail-closed policy.
Recraft V4 and V4.1 models now receive prompts up to 10,000 characters whole.
Recraft V3 prompts over 1,000 characters now fail visibly.
The generator applies the same per-model check in command-line runs.

## Limits

| Target keys | Limit | Route | Source |
|---|---|---|---|
| `gpt2`, `gpt25-sunburst`, `gpt25-flare`, `gpt1`, `gpt1-mini` | 32,000 characters | Generations and edits | OpenAI image API reference, checked 2026-10-12 |
| `ideogram-v45` | 10,000 characters | Generate and Precise Edit | Ideogram 4.5 API reference; see [ideogram-45-support.md](ideogram-45-support.md) |
| `recraft`, `recraft-v41-utility`, `recraft-v41-pro`, `recraft-v41-vector`, `recraft-v4`, `recraft-v4-pro` | 10,000 characters | Text-to-image and image-to-image | Recraft API appendix, checked 2026-10-12 |
| `recraft-v3` | 1,000 characters | Text-to-image and image-to-image | Recraft API appendix, checked 2026-10-12 |
| `grok-api` (1.0) | 16,000 characters | Text-to-image only | xAI's HTTP 400 reply text, production 2026-09-19 and 2026-09-25 |
| `grok-web` | 8,192 UTF-8 bytes, truncated | Text-to-image only | Owner requirement, 2026-09-09 |

The server publishes and checks no limit for these targets:

- `grok-api-pro` (2.0): xAI publishes no limit.
- `grok-api` edits: the observed 1.0 limit came from text-to-image requests only.
- Ideogram 4.0, 3.0, and 2.0: Ideogram's API pages state no limit.
  A 5,000-character figure for 3.0 appears only on third-party sites.
- BFL FLUX models: BFL states a 32K-token limit for FLUX.2, not a character limit.
- Google models: their token limits exceed every prompt the composer accepts.
- `grok-web` edits: they use grok.com app-chat, which has no known limit.
- Local generators, grok-web chat and video, and the describe and layout-map targets.
  This includes `describe-gemini-argon` (added 2026-10-29): Argon limits input in tokens, far above any composer prompt.

## Evidence (production, 2026-10-12)

- Production logs record 358 Recraft truncations: 246 on the original, 112 on Vibecoders.
- On the original, 129 of 623 Recraft jobs had prompts over 1,000 characters.
- On Vibecoders, 26 of 59 Recraft jobs had prompts over 1,000 characters.
- No Recraft prompt exceeded 10,000 characters.
- Thirty Recraft V3 jobs had prompts over 1,000 characters.
- OpenAI rejected GPT Image 2.5 prompts over 32,000 characters on 2026-09-27 and 2026-10-12.
- One OpenAI rejection reported length 39,224.
- That wire prompt had 39,224 characters and 39,392 UTF-8 bytes, so OpenAI counts characters.
- xAI rejected two `grok-api` text-to-image prompts, of 18,155 and 20,665 characters.

## Composer notice (updated 2026-10-26)

The notice under the prompt has one line per limit and count.
Selected targets with the same limit and the same count share a line.
Each line starts with the excess in bold: the characters, or UTF-8 bytes, to remove.
The line then names its targets, the limit, and the prompt's count.
Red lines name targets that will fail and receive nothing.
Each red line ends with a condense button and a deselect button.
Both act on exactly the targets the line names.
Amber lines name targets that will receive a shorter prompt than the box shows.
That is a condensed prompt, or grok-web's truncation.
Red lines come first, then condensed lines, then truncation lines.
Within each kind, lines appear in order of excess, smallest first.
A line adds "Counts include endpoint extra text." when one of its targets has extra text.
Submission stays available.
The user condenses or shortens the prompt, or deselects the target.
The notice updates on prompt edits, extra-text edits, target selection, and image attachment changes.
A target uses its image-route limit when it receives the attached image.
Grok-web shows no notice when it receives the attached image, because its edits send the full prompt.

The earlier notice repeated the same count and limit once per target: seven times in the 2026-10-26 report.
It did not state the excess.
It also put Recraft V3, 9,076 characters over its 1,000 limit, in one sentence with targets 76 characters over theirs.

## Condensing for over-limit targets (2026-10-26)

### Owner decisions

The owner asked for better default handling of over-limit targets.
Four options were offered:

- Keep the failure and the notice (the agent's recommendation).
- Truncate automatically.
- Condense automatically with Sonnet 5.5.
- Condense only when the user presses a button on the red line.

The owner chose the button.
The owner then added a second requirement: condense only for the targets that need it.
A condensed prompt therefore reaches only the over-limit targets the user condensed for.
Every other target receives the full prompt.
The prompt box keeps the full prompt.
The server never condenses, truncates, or retries on its own.

The agent chose Sonnet 5.5 as the model, because the button option named none.
The button label names the model.
Sonnet 5.5 costs $2 per million input tokens and $10 per million output tokens.
For a 10,000-character prompt, the estimate is about $0.03 per request without thinking.
Adaptive thinking stays on and adds output tokens.
The 16,000-token output cap limits one request to about $0.17.
No request was measured.

### Condense request

The red line's button reads **condense for Recraft V3 · Sonnet 5.5**, or **condense for these 2 · Sonnet 5.5**.
It sends `POST /api/prompt/condense` with the prompt, the line's targets, their extra text, and the image state.
The server computes a budget for each target: its limit, minus the endpoint extra text and its two-character separator.
The request uses the smallest budget of its targets.
The server refuses a target within its limit or without a limit.
It refuses a budget under 100 characters before any provider call.
The instruction asks for at most the budget and aims for 90% of it, because models count characters poorly.
It keeps everything that changes the image, keeps requested on-image text word for word, and adds nothing.
It keeps any stated lighting; without one, it asks for bright, clear daytime light.
The request uses the prompt-rewrite transport: Anthropic Messages, no temperature, and the shared two request slots.
The server rejects a reply over the budget, and applies nothing.
A reply that refuses fails, unless the source prompt itself contains the refusal phrase.
The user can press the button again.

### Records and reuse

Each request is stored in the prompt-version history (`ui_claude_prompt_exchanges`) with `purpose = 'condense'`.
The column defaults to an empty string, which marks prompt edits: advice, flesh out, and restore.
A request with the same account, model, instruction, and exact prompt returns the newest stored success.
That reuse makes no provider call.
**condense again** always asks the model.
One request per account, prompt, and limit runs at a time; a second tab receives HTTP 409.
These limits keep tabs and reloads from repeating provider calls for unchanged input.
Prompt history labels these entries **Condensed for prompt limits**.
Restoring one there puts it in the prompt box, so every target then receives it; that is an explicit user action.
Character writing skips condensed entries, because job history already holds the full prompt.

### Composer

After a successful request, an amber line replaces the red line.
It states the condensed count, the targets, the limit, the full count, and the model.
Its buttons are **show condensed prompt**, **condense again**, and **discard**.
A condensed prompt applies to a target only while three conditions hold:

- The prompt equals the condensed prompt's source exactly, after trimming and line-break normalization.
- The full prompt is over that target's limit.
- The condensed prompt with that target's extra text fits the limit.

An edit to the prompt therefore brings the red line back.
Restoring the exact text makes the condensed prompt apply again.
Condensed prompts live only in the page, so a reload discards them.
Pressing condense again after a reload reuses the stored prompt without a provider call.
Without a login, a change of creator name discards them, because they belong to that name.

### Job intake and execution

`POST /api/jobs` accepts `condensedPrompts`: a JSON object from target key to condense exchange id.
The server verifies every entry and rejects the whole job with HTTP 400 on any failure.
Each entry must name a selected target that is over its limit with the full prompt.
The exchange must belong to the submitting account and have the condense purpose and a prompt-rewrite model.
It must have succeeded and have been made from the exact job prompt.
The condensed prompt with the target's extra text must fit the target's limit.
The runner gives that target's private prompt copy the condensed prompt, then appends the extra text.
A `ClaudeRewrite` transformation step records the model and exchange id.
The limit check still runs on the result.
The `accepted` event lists each condensed prompt with its exchange id, model, text, and targets.
The job card shows a collapsed block under the head and a **condensed prompt** badge in each such target's cell.
The image viewer shows the condensed prompt for that target, above any extra text.
Contact-sheet labels add "condensed prompt".
The prompt-rewrite environment switch controls the condense route, the job field, and the buttons.

Open item: public share pages show the job prompt and extra text, but do not mark condensed targets.
A change there needs a decision under [the sharing contract](public-prompt-sharing.md).

## Contract and files

`GET /api/config` publishes three fields per target:

- `maxPromptChars`: the text-to-image limit, or null.
- `maxImagePromptChars`: the limit when the target receives the attached image, or null.
- `maxPromptUtf8Bytes`: grok-web's truncation budget, or null.

A rejected target creates no generation-archive row, because no provider request occurs.
The job's result event and the server log record the error.

`GET /api/config` also publishes `promptCondense`: model, label, availability, and `minCondensedPromptChars`.
`POST /api/prompt/condense` returns `{exchangeId, model, modelLabel, condensedPrompt, budget, generators, reused}`.
A failed request returns HTTP 502 with `error`, `exchangeId`, and any provider action hint.

- `Implementation/UiJobs.cs`: `UiJobRunner.MaxPromptCharsFor`, `DescribePromptLimitProblem`, `PromptFromFormField`, and the check in `RunOneAsync`.
  Condensing: `WithEndpointExtraText`, `PlanPromptCondense`, `DescribeCondensedPromptProblem`, `ResolveCondensedPrompts`, `ApplyCondensedPrompt`, and `UiJobSpec.CondensedPrompts`.
- `Implementation/UiCommunity.cs`: the `purpose` column and `FindSucceededClaudePromptExchange`.
- `TextLLMs/DirectedPromptRewrite.cs`: `CondenseModel`, `CondensePurpose`, and `CondenseInstruction`.
- `ImageGenerators/RecraftGenerator.cs`: per-model limits and the generator's own check.
- `ImageGenerators/GptImage2Generator.cs` and `ImageGenerators/GrokImagineGenerator.cs`: limit constants.
- `IdeogramAPI/IdeogramClient.cs`: the Ideogram 4.5 limit and its client check.
- `Workflows/UiWorkflow.cs`: the `/api/config` fields, the job and video-job prompt intake through `PromptFromFormField`, the condense route, and the `condensedPrompts` job field.
- `Ui/wwwroot/prompt-limits.js`: the composer measurement and condense checks, also loaded by the tests.
- `Ui/wwwroot/app.js`, `style.css`, and `index.html`: the notice, the condense flow, job cards, and the viewer.
- `Ui/wwwroot/prompt-rewrites.js`: history labels. `Ui/wwwroot/environment-storage.js`: hides condense buttons when prompt rewriting is off.
- `MultiImageClient.Tests/PromptLimitTests.cs`, `MultiImageClient.Tests/PromptCondenseTests.cs`, and `Ui/tests/prompt-limits.test.js`: tests, including the target classification, the line-break count, and condensing.
