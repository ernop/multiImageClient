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
Grok-web alone truncates its prompt, to 8,192 UTF-8 bytes.
That truncation is an owner requirement; see [grok-web-prompt-limits.md](grok-web-prompt-limits.md).
The server does not check targets without a published limit.
Their providers decide, and their rejections remain visible errors.

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
| `grok-web` | 8,192 UTF-8 bytes, truncated | Text-to-image and edit | Owner requirement, 2026-09-09 |

The server publishes and checks no limit for these targets:

- `grok-api-pro` (2.0): xAI publishes no limit.
- `grok-api` edits: the observed 1.0 limit came from text-to-image requests only.
- Ideogram 4.0, 3.0, and 2.0: Ideogram's API pages state no limit.
  A 5,000-character figure for 3.0 appears only on third-party sites.
- BFL FLUX models: BFL states a 32K-token limit for FLUX.2, not a character limit.
- Google models: their token limits exceed every prompt the composer accepts.
- Local generators, grok-web chat and video, and the describe and layout-map targets.

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

## Composer notice

The notice under the prompt lists each affected selected target with its count and limit.
Red lines name targets that will fail and receive nothing.
Amber lines name targets that will receive a shortened prompt.
A line adds "Counts include endpoint extra text." when one of its targets has extra text.
Submission stays available.
The user shortens the prompt or deselects the target.
The notice updates on prompt edits, extra-text edits, target selection, and image attachment changes.
A target uses its image-route limit when it receives the attached image.

## Contract and files

`GET /api/config` publishes three fields per target:

- `maxPromptChars`: the text-to-image limit, or null.
- `maxImagePromptChars`: the limit when the target receives the attached image, or null.
- `maxPromptUtf8Bytes`: grok-web's truncation budget, or null.

A rejected target creates no generation-archive row, because no provider request occurs.
The job's result event and the server log record the error.

- `Implementation/UiJobs.cs`: `UiJobRunner.MaxPromptCharsFor`, `DescribePromptLimitProblem`, and the check in `RunOneAsync`.
- `ImageGenerators/RecraftGenerator.cs`: per-model limits and the generator's own check.
- `ImageGenerators/GptImage2Generator.cs` and `ImageGenerators/GrokImagineGenerator.cs`: limit constants.
- `IdeogramAPI/IdeogramClient.cs`: the Ideogram 4.5 limit and its client check.
- `Workflows/UiWorkflow.cs`: the `/api/config` fields.
- `Ui/wwwroot/prompt-limits.js`: the composer measurement, also loaded by the tests.
- `Ui/wwwroot/app.js`, `style.css`, and `index.html`: the notice.
- `MultiImageClient.Tests/PromptLimitTests.cs` and `Ui/tests/prompt-limits.test.js`: tests.
