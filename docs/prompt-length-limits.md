# Prompt-length limits

Every UI target declares a prompt-length limit or records why it has none.
The composer, the job request, and the per-target call measure the same text.
A prompt that a provider refuses for length never leaves the server.
The registry is `UiPromptLimits` in `MultiImageClient/Implementation/UiPromptLimits.cs`.

## Incident — 2026-10-12

A production job sent a 34,874-character prompt to several selected models.
OpenAI refused it for GPT Image 2.5 Sunburst and GPT Image 2.5 Flare.
Their requests held 35,090 characters, and the OpenAI limit is 32,000.
The difference is the 214-character default daylight extra text and its 2-character separator.
Ideogram 4.5 refused the same prompt before its call, because its limit is 10,000 characters.
Nothing warned before Generate.
The composer notice then covered only grok-web truncation.
`/api/config` published no other limit, so the browser could not check one.

## Requirements (2026-10-12)

| ID | Requirement | Status |
|---|---|---|
| R1 | Declare a limit, or the reason for none, for every UI target key. | Done |
| R2 | Block Generate while the prompt is too long for a selected model. | Done |
| R3 | Refuse an over-limit job request on the server before it accepts the job. | Done |
| R4 | Refuse the provider call when work reaches the runner without the request check. | Done |
| R5 | Count exactly the text that the provider receives, including endpoint extra text. | Done |
| R6 | Never truncate silently. grok-web keeps its owner-approved truncation. | Done |
| R7 | Fail the test suite when a target key has no declaration. | Done |
| R8 | Show no billing or API-key hint for a prompt-length failure. | Done |

## Limits

| Targets | Limit | Policy | Source |
|---|---|---|---|
| `gpt2`, `gpt25-sunburst`, `gpt25-flare`, `gpt1`, `gpt1-mini` | 32,000 characters | reject | OpenAI Images API reference: "The maximum length is 32000 characters for the GPT image models." The 2026-10-12 production refusal confirms it for GPT Image 2.5. |
| `ideogram-v45` | 10,000 characters | reject | Ideogram 4.5 API reference, 2026-10-08. See [Ideogram 4.5 support](ideogram-45-support.md). |
| `recraft`, `recraft-v41-utility`, `recraft-v41-pro`, `recraft-v41-vector`, `recraft-v4`, `recraft-v4-pro` | 10,000 characters | reject | Recraft API "Limits and sizes" appendix, checked 2026-10-12. |
| `recraft-v3` | 1,000 characters | reject | The same appendix. |
| `grok-web` without an attached image | 8,192 UTF-8 bytes | truncate | Owner decision of 2026-09-09. See [grok-web prompt truncation](grok-web-prompt-limits.md). |
| `grok-api`, `grok-api-pro` | none enforced | — | See [grok-api](#grok-api). |
| `ideogram`, `ideogram-v3`, `ideogram-v2`, all `bfl*` and `krea*` targets | none | — | No published limit, checked 2026-10-12. |
| `grok-web-chat`, `grok-web-video`, and grok-web with an attached image | none | — | grok.com app-chat has no known limit. Only the imagine WebSocket enforces one. |
| `google`, `googlepro`, all `describe-*` targets, `layout-map` | none | — | These models limit input in tokens, far above any composer prompt. Ideogram describe sends no prompt. |
| `local-klein`, `local-zimage`, `meta-web` | none | — | Local text encoders truncate internally. `meta-web` is disabled in the UI. |

Characters are Unicode code points.
An emoji outside the Basic Multilingual Plane counts as one character and four UTF-8 bytes.

### grok-api

xAI publishes `max_prompt_length` per image model at `GET /v1/image-generation-models/{model}`.
The unit is tokens.
The documented example shows 1,024 for `grok-imagine-image` version 1.0.0.
The generation guide for `grok-imagine-image-2.0` (updated 2026-10-08) states no limit.
The server cannot count xAI tokens exactly, so it enforces no limit for these targets.
A longer prompt reaches xAI, and any xAI error stays visible on the result.
Whether xAI refuses or truncates a longer prompt is unverified.
AGENTS.md formerly called 4,096 the official grok-api limit.
The repository records no source for that figure, so this document supersedes it.

## Measurement

The server sends each target a wire prompt.
`UiPromptLimits.ComposeWirePrompt` builds it from the job prompt and that target's extra text.
Nonblank extra text follows the prompt after one blank line (`\n\n`), trimmed.
Extra text includes the global composer directive; see [endpoint extra text](endpoint-extra-text.md).
Every check measures this wire prompt, so the extra text counts toward the limit.

HTML form submission converts each textarea line break to CR LF.
The server and the provider therefore count two characters for each prompt line break.
`prompt-limits.js` applies the same conversion before it counts.
Extra text travels in a JSON field and keeps its LF line breaks.

## Where the checks run

| Stage | Behavior |
|---|---|
| Composer | `prompt-limits.js` measures every available chip whenever the prompt, the selection, an attachment, or appended text changes. |
| `POST /api/jobs` | The server refuses the whole job with HTTP 400 when any selected target refuses the prompt. The message names each such model with its count and limit. |
| Per-target call | `UiJobRunner.RunOneAsync` refuses an over-limit target before it builds the generator. This covers goal-loop renders, which do not pass the job request check. |
| Recraft client | `RecraftClient` refuses an over-limit prompt before it sends it. This also covers CLI and REPL modes. |
| Ideogram 4.5 client | `IdeogramClient` refuses prompts over 10,000 characters before it sends them. |

A refused target fails with a message of this form:
"Prompt too long for GPT Image 2.5 Sunburst: 35,090 characters including its extra text; the limit is 32,000. The request was not sent."
That failure carries no billing or API-key hint, and it costs nothing.
Other targets of the same job run normally.

CLI and REPL modes do not use the registry.
There, other providers return their own errors.

## Composer behavior

A chip turns red, with a tooltip, when the prompt is too long for that model.
This applies whether the chip is selected or not.
Red overrides the amber sketch tint, because a selected red chip blocks Generate.

The notice under the prompt lists each selected model whose limit the prompt passes.
Each row shows the model name, the measured count, and the limit in bold numerals.
A red group lists models that refuse the prompt.
Its **deselect it** or **deselect these N** button clears those selections.
An amber group lists grok-web, which runs with the prompt cut to fit.
Other models still get the full prompt.

Generate stays blocked while a selected model refuses the prompt.
It then shows "Prompt too long for …. Shorten the prompt or deselect these models. Nothing was sent."

With an attached image, grok-web sends the prompt through grok.com app-chat.
The imagine WebSocket limit then does not apply, and the amber notice disappears.
`textToImageOnly` marks this case in the published limit.

## Contract

`GET /api/config` gives each generator a `promptLimit` field.
It is `null` when the target has no declared limit.
Otherwise it has this shape:

```json
{ "max": 32000, "unit": "characters", "policy": "reject", "textToImageOnly": false }
```

`unit` is `characters` or `utf8Bytes`.
`policy` is `reject` or `truncate`.
This field replaced `maxPromptChars` and `maxPromptUtf8Bytes` on 2026-10-12.

## Settled decisions (2026-10-12)

1. **No silent truncation.** The server refuses over-limit prompts.
   The Recraft client formerly cut prompts over 1,000 characters to 990 without notice.
   That code is removed.
   Recraft V4 and newer now accept their published 10,000 characters.
2. **grok-web keeps truncation.** The owner required it on 2026-09-09.
   It applies only to the imagine WebSocket text-to-image path.
3. **Count what the provider receives.** Extra text and CR LF line breaks count.
4. **Refuse at three stages.** The composer, the job request, and the per-target call each refuse.
   The composer gives the earliest notice.
   The request check protects the server from older or modified pages.
   The per-target check covers goal loops.
5. **Unknown keys are errors.** `UiPromptLimits.For` throws for an undeclared key.
   `null` requires a comment that names the reason.
6. **Whole-number status codes.** Billing and key hints match `401` and `402` only as whole numbers.
   Error texts carry lengths and counts.
   A length such as 34020 contains the digits 402.
   The former substring check showed a billing hint for that prompt-length error.
7. **grok-api is not enforced.** Its published limit counts tokens, which the server cannot count exactly.

## Adding a target

Add a `UiPromptLimits.For` entry in the same change that adds the key.
Use the provider's published limit and unit.
Name the source and the check date in a comment.
Use `null` only with a comment that names why no limit applies.
Add a truncating target only after an explicit owner decision.
`PromptLimitTests.EveryUiTargetDeclaresAPromptLimit` fails for an undeclared key.
`PromptLimitTests.RejectingTargetsAreExactlyTheListedOnes` lists every rejecting key.
Update it when a new target rejects.

## Tests

- `MultiImageClient.Tests/PromptLimitTests.cs`: completeness, each limit, the 2026-10-12 prompt, boundary lengths, extra text, CR LF counting, the `/api/config` projection, runner refusal without a provider call, and the Recraft client.
- `MultiImageClient.Tests/ProviderActionHintsTests.cs`: lengths and counts that contain 401 or 402 produce no hint; standalone status codes still do.
- `MultiImageClient/Ui/tests/prompt-limits.test.js`: form serialization, wire measurement, code points and UTF-8 bytes, the 2026-10-12 prompt, boundaries, and the text-to-image-only rule.

## Files

| File | Role |
|---|---|
| `MultiImageClient/Implementation/UiPromptLimits.cs` | Registry, measurement, wire prompt, messages, and `/api/config` projection |
| `MultiImageClient/Workflows/UiWorkflow.cs` | `promptLimit` in `/api/config`; job request refusal |
| `MultiImageClient/Implementation/UiJobs.cs` | Per-target refusal in `RunOneAsync`; extra text through `ComposeWirePrompt` |
| `RecraftAPI/RecraftClient.cs` | Per-model Recraft limits and refusal before sending |
| `MultiImageClient/ImageGenerators/RecraftGenerator.cs` | Former silent truncation removed |
| `IdeogramAPI/IdeogramClient.cs` | Ideogram 4.5 limit |
| `MultiImageClient/GrokWebClient.cs` | grok-web byte budget and truncation |
| `MultiImageClient/Implementation/ProviderActionHints.cs` | Whole-number 401 and 402 matching |
| `MultiImageClient/Ui/wwwroot/prompt-limits.js` | Browser measurement |
| `MultiImageClient/Ui/wwwroot/app.js`, `style.css`, `index.html` | Chip markers, notice, deselect button, and Generate block |
