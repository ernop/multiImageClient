# Gemini 4 Argon support — plan (2026-10-29)

Status: **not implemented.** This file records the owner's request and the
research done so far. A local agent completes the work. It has `tpbeta-root`
access for the release.

## Owner request (2026-10-29, verbatim)

> fully support it and also add it as an image maker and also a describe
> target to the system, and also update production (both instances) to
> include it in their current config, and also our local one too, then
> redeploy all

"It" means Gemini 4 Argon. The owner first called it "Gemini a3". The
repository had no reference to Argon or `gemini-4` on 2026-10-29.

## Provider facts (checked 2026-10-29)

Sources:
[Gemini API model page](https://ai.google.dev/gemini-api/docs/models/gemini-4-argon),
[What's new in Gemini 4 Argon](https://ai.google.dev/gemini-api/docs/generate-content/latest-model),
[Google Cloud model page](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/gemini/4-argon),
[DeepMind model card](https://deepmind.google/models/model-cards/gemini-4-argon/).

- The model ID is `gemini-4-argon` (stable). Google released it worldwide on 2026-10-13.
- Input types are text, image, video, audio, and PDF. **Output is text only.**
- Google lists **image generation: not supported**.
- Accepted image types are PNG, JPEG, WEBP, HEIC, and HEIF. The inline limit is 7 MB per file (Cloud page).
- The context window is 1,048,576 tokens and counts input and output together.
- Synchronous output is at most 65,536 tokens. Non-streaming REST calls default to **32,768**.
- Thinking levels are `low`, `medium` (the default), and `high`.
- Google says `thinking_level="MINIMAL"` returns HTTP 400.
- The two Google pages disagree about `thinking_budget`. The Gemini API page says it maps to a level with a warning. The Cloud page says it returns HTTP 400. Never send it.
- Google ignores `temperature`, `top_p`, and `top_k`.
- Explicit context caching (`cachedContents`) returns an error. Implicit caching works.
- Pricing until 2026-11-30 is $2 input and $10 output per million tokens. After that it is $4 and $20.
- Structured outputs are supported, so `responseMimeType: application/json` should work. Verify it live.

## Decision needed: "image maker"

Argon cannot return images, so it cannot be an image-generator target like
Nano Banana. Do not add it to `ImageGeneratorKeys`. A text model listed as
an image generator would fail on every job. Ask the owner which option to
build:

1. **Prompt writer for an image model.** Argon writes or expands the image prompt, and an existing image model renders it. The goal-loop manager already works this way, and the prompt-rewrite area could too. The cost is that the image comes from another model. Prompt rewrite is Anthropic-only by an owner decision of 2026-10-06, so this option also changes that decision.
2. **Skip the image maker.** Ship only the describe target and goal-loop roles.
3. **Wait** for a Gemini 4 image model and add that as a normal image target.

Recommendation: option 2 now, with the goal-loop manager role covering
"Argon directs images". Option 1 only if the owner wants Argon in the
composer's prompt tools.

## Implementation plan (describe target + full text support)

### 1. Describe target `describe-gemini-argon`

- In `UiJobs.cs`, add `KeyDescribeGeminiArgon = "describe-gemini-argon"`. Add it to `DescribeKeys`, the `GoogleGeminiApiKey` gate list (near line 3977), the cost table (near line 5358), and the `RunDescribeOneAsync` dispatch (near line 5389).
- `GeminiVisionDescriber` (`Describers/RemoteVisionDescribers.cs`):
  - `isGemini3` checks `StartsWith("gemini-3")`. For `gemini-4-argon` it is false, so the code sends `thinkingBudget`. Google may reject that with HTTP 400. Change the test to "send `thinkingLevel`" for `gemini-3*` and `gemini-4*`, and keep `thinkingBudget` only for 2.5.
  - Send `thinkingLevel: "low"` (do not send `minimal`).
  - Do not send `temperature` to Argon, because Google ignores it.
  - `maxOutputTokens` is `maxTokens + 1024`. Check that Argon's thinking fits inside this. If it does not, raise the budget for Argon only.
  - Images over 7 MB: check the 7 MB inline limit against the Gemini API (the Cloud page states it). If it applies, conform the input deterministically before the call and log it, as the Recraft and Ideogram paths do, or fail closed with a clear message.
- In `GeneratorPresentation.cs`, set the display name to `Gemini 4 Argon describe` and the provider to `Google`.
- `UiTargetScheduler.cs`: `LaneGoogle`.
- `ProviderActionHints.cs`: add the key to the Google billing and key hint (line 157).
- `UiWorkflow.cs` `/api/config` describe entry (near line 564).
- `PromptLimitTests`: classify the key. Describe targets take an instruction, so give it a limit or a listed reason.
- `ProviderActionHintsTests` must pass with the new key.
- Estimate the cost per call from live token counts, then record the number here.

### 2. Goal-loop manager and critic `manager-gemini-4-argon`

- In `TextLLMs/ManagerChatClients.cs`, add `KeyGemini4Argon`. Use label `Gemini 4 Argon`, provider `google`, model `gemini-4-argon`, `InputUsdPerMTok = 2m`, `OutputUsdPerMTok = 10m`, and `ImageLimits = GeminiLimits`.
- Update the comment at line 223. Argon is generally available, unlike `gemini-3.5-pro`.
- Pricing changes on 2026-12-01. Record the date at the call site, or model the price change explicitly. Never estimate silently with old prices.
- The Gemini manager request (near line 981) sends `maxOutputTokens = ManagerChatHttp.MaxOutputTokens` (64,000) and `thinkingLevel: high`. Google says non-streaming REST defaults to 32,768 output tokens. Live-check whether an explicit 64,000 is accepted without streaming. If Google rejects it, use 32,768 for Argon only, record it here, and update the 64,000-token claim in `docs/goal-loop-prd.md`.
- `GeminiLimits` comes from the 2026-09-04 file-input page (100 MB inline). Check whether Argon has a smaller per-image limit, for example 7 MB.
- Add the model to the manager and critic catalog tests. Verify that `/api/config` `goalLoop` lists it.

### 3. Groups and defaults

- Describers stay shown under **only SOTA** (2026-10-06 rule), so the new describe chip appears for everyone without a migration.
- Do not add it to anyone's saved default selections unless the owner asks. The Ideogram 4.5 change of 2026-10-10 used a one-time migration. Repeat that only on an explicit request.

### 4. Production and local configuration

- Every target needs only `GoogleGeminiApiKey`, which both instances and the local install already use for Nano Banana and `describe-gemini`. Check that each settings file has a non-blank key. Change nothing else.
- Vibecoders defaults: administration manages its provider defaults. Ask the owner whether the describe target should be on by default there. Add nothing to environment registry lists without that answer.
- Never print or commit either instance's secret path or keys.

### 5. Release

Follow `.cursor/rules/production-deploy-target.mdc` and `deploy/README.md`:
the release gate, push `master`, `tools/restart-local-ui.sh` (or the
`multi-image-client.service` unit on hostname `PC`), then
`deploy/agent-redeploy.sh` for the original. Then run
`deploy/update-vibecoders.sh` as root. Status 75 means busy: wait with
`--wait-minutes N` and never bypass it. Verify local `/healthz`.

### 6. Verification

- `dotnet test MultiImageClient.Tests/MultiImageClient.Tests.csproj -c Release`
- `node --test MultiImageClient/Ui/tests/*.test.js`
- Run one live describe call with a small PNG. Confirm that the JSON `{description, comments}` parses and record the token usage.
- Run one live goal-loop manager turn with Argon.
- After the release, confirm that `/api/config` on both instances lists `describe-gemini-argon` and the Argon manager.

### 7. Documents to update in the same change

- This file: change the status, record measured costs and the owner's answers.
- `docs/describe-endpoints.md`: add the new target and model ID.
- `docs/goal-loop-prd.md`: add the manager and critic catalog entry.
- `docs/prompt-length-limits.md`: add the classification.
- `AGENTS.md`: update the `Also Read` entry for this file and the Run Modes describe and goal-loop bullets.
