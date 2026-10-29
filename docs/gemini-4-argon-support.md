# Gemini 4 Argon support (2026-10-29)

Status: **implemented 2026-10-29.** Argon is a describe target and a goal-loop
manager and critic. It is not an image maker, because it cannot output images.
The owner still chooses one image-maker option (see below).

## Owner requests (2026-10-29, verbatim)

To the remote agent that wrote the first plan:

> fully support it and also add it as an image maker and also a describe
> target to the system, and also update production (both instances) to
> include it in their current config, and also our local one too, then
> redeploy all

To the local agent that implemented it:

> a remote agent has made a branch with google a3 image support. add that to
> all 2 current deployment configs, add supporting code, and redeploy both
> prod sites please

"It" and "google a3" mean Gemini 4 Argon. The owner first called it "Gemini a3".

## Provider facts (checked 2026-10-29)

Sources:
[Gemini API model page](https://ai.google.dev/gemini-api/docs/models/gemini-4-argon),
[What's new in Gemini 4 Argon](https://ai.google.dev/gemini-api/docs/generate-content/latest-model),
[Long decode guide](https://ai.google.dev/gemini-api/docs/generate-content/long-decode),
[Google Cloud model page](https://docs.cloud.google.com/gemini-enterprise-agent-platform/models/gemini/4-argon),
[DeepMind model card](https://deepmind.google/models/model-cards/gemini-4-argon/).

- The model ID is `gemini-4-argon` (stable). Google released it worldwide on 2026-10-13.
- Input types are text, image, video, audio, and PDF. **Output is text only.**
- Google lists **image generation: not supported**. Google publishes no Gemini 4 image model.
- Thinking levels are `low`, `medium` (the default), and `high`.
- `thinkingLevel: MINIMAL` returns HTTP 400. Never send `thinkingBudget` to Argon.
- Argon ignores `temperature`, `top_p`, and `top_k`. The app does not send them.
- One non-streaming REST call returns at most 32,768 tokens. Thinking counts against this cap.
- Google states HTTP 400 above 32,768 without a `continuationToken`. A live call with 64,000 returned HTTP 200. The app sends 32,768.
- A reply that uses the whole cap ends with `MAX_TOKENS`. Longer output needs a `continuationToken` loop. The app does not run that loop.
- The server closes a non-streaming call after 10 minutes. Google asks for a 20-minute client timeout.
- Explicit context caching returns an error. Implicit caching works.
- Price: $2 input and $10 output per million tokens through 2026-11-30. From 2026-12-01: $4 and $20.
- The Cloud page states a 7 MB inline limit per image. The Gemini API accepted a 9.7 MB inline PNG live.
- `usageMetadata.candidatesTokenCount` excludes thinking. `thoughtsTokenCount` holds it, and Google bills it as output.
- `usageMetadata` omits token counts that are zero.
- `responseMimeType: application/json` works live.
- A history that replays assistant turns as plain text, with no thought signatures, works live.

## Decisions

### No image maker yet: owner choice open

Argon cannot return images. A text model in `ImageGeneratorKeys` would fail on
every job, so Argon is not an image target. The owner chooses one option:

1. **Prompt writer for an image model.** Argon writes the image prompt, and an existing image model draws it. The goal-loop manager already works this way. A composer prompt tool would change the 2026-10-06 decision that prompt rewrite uses Anthropic only.
2. **No image maker.** The describe target and the goal-loop roles are the Argon features. This is the current state.
3. **Wait** for a Gemini 4 image model, then add it as a normal image target.

Recommendation: option 2. The goal-loop manager already lets Argon direct an image model.

### Describe target `describe-gemini-argon`

- The chip name is `Gemini describe (gemini-4-argon)`, like the other describe chips.
- Provider Google, scheduler lane `google`, key `GoogleGeminiApiKey`, Google billing and key hints.
- Request: `thinkingLevel: low`, no `temperature`, `maxOutputTokens` 32,768, `responseMimeType: application/json`.
- The client timeout is 20 minutes. Other Gemini describe models keep 5 minutes.
- It has no prompt-length limit. Analysis models limit input in tokens, far above any composer prompt.
- The cost estimate is 2,000 input tokens plus 1,200 output tokens at the current price: $0.016, then $0.032 from 2026-12-01.

### Goal-loop manager and critic `manager-gemini-4-argon`

- Label `Gemini 4 Argon`, provider `google`, image limits `GeminiLimits` (100 MB per request).
- Request: `thinkingLevel: high` with thoughts returned, JSON output, images at `MEDIA_RESOLUTION_ULTRA_HIGH`.
- The output cap is 32,768 tokens. `ManagerDefinition.MaxOutputTokens` holds the cap per model. Every other model keeps 64,000.
- `ManagerDefinition.PriceChange` holds the 2026-12-01 price. The change starts at 08:00 UTC, midnight Pacific time.
- Cost estimates use the price when the reply arrives. `/api/config` publishes the price when the page loads.
- The characters feature uses the same catalog, so Argon is also a characters writer model.
- The shared manager client timeout stays 15 minutes. The server closes the call at 10 minutes first.

### Gemini output tokens include thinking

Gemini manager replies count output as `candidatesTokenCount` plus
`thoughtsTokenCount`. This applies to Gemini 3.5 Flash too. Before 2026-10-29
the count left out thinking.

### Production configuration

- Both instances' settings files hold a non-blank `GoogleGeminiApiKey` (checked 2026-10-29). That key enables every Argon feature.
- No settings file changes. The deploy tooling already copies `GoogleGeminiApiKey` into new environments.
- The environment default lists stay unchanged. They pre-select image targets in the only SOTA group, never a describe target.
- No migration adds the describe target to saved selections. A selected describe target makes a paid call on every image attach.
- The describe chip appears for every user, because describe targets ignore the only SOTA view.

## Live verification (2026-10-29)

- Local UI describe job: a 1024x768 synthetic PNG returned a correct `{description, comments}` reply in 7.9 s.
- Describe probe on the same PNG: 1,294 input and 178 output tokens, about $0.0044 at the introductory price.
- Manager call through `GeminiManagerClient`: a two-turn history with one image returned HTTP 200, `STOP`, and valid JSON.
- That call used 2,362 input tokens (2,160 for the image) and 468 output tokens (381 thinking): about $0.0094.

## Files

- `Implementation/UiJobs.cs`: key, describe list, key gate, `ArgonDescribeCostEstimate`, describer construction.
- `Describers/RemoteVisionDescribers.cs`: `GeminiVisionDescriber.GenerationConfig` and the 20-minute client.
- `TextLLMs/ManagerChatClients.cs`: catalog entry, `MaxOutputTokens`, `ManagerPriceChange`, `GeminiManagerClient.OutputTokens`.
- `Implementation/GeneratorPresentation.cs`, `UiTargetScheduler.cs`, `ProviderActionHints.cs`, `Workflows/UiWorkflow.cs`.
- Tests: `GeminiArgonTests`, plus the key lists in `PromptLimitTests`, `ProviderActionHintsTests`, and `GeneratorPresentationTests`.
