# UI describe endpoints

Reviewed 2026-09-03. These are the `--ui` image→text targets plus layout-map.

The composer heading no longer carries an extra “image → text…” note. The
chips and their catalog `detail` strings remain the instruction.

## Current wired models

| UI key | Model / endpoint | Why this ID |
|---|---|---|
| `describe-ideogram` | Ideogram `POST /describe`, `describe_model_version=V_3` | Official caption API. Default was already V_3; we now send it. `POST /v1/ideogram-v4/describe` returns structured *generation* prompts, not this caption path. |
| `describe-openai` | `gpt-5.6-sol` | OpenAI flagship. Official models page: start here. Vision on Responses API. `gpt-4.1` is older. |
| `describe-claude` | `claude-sonnet-5` | Current Sonnet. `claude-sonnet-4-5` is legacy. Keep Sonnet, not Opus 5 / Fable 5. |
| `describe-gemini` | `gemini-3.5-flash` | `gemini-2.5-pro` retires 16 Oct 2026. Gemini 3.5 Flash is GA and led MMMU-Pro (~83.6%) in Sep 2026 aggregator tables. `gemini-3.1-pro-preview` is the Pro successor (~80.5% on the same board). |
| `describe-grok` | `grok-4.6` | xAI flagship. Modalities: text and image → text. `grok-4.3` remains available but is not current. |
| `layout-map` | `gemini-3.5-flash` | Same Gemini call as describe. Layout-map asks for JSON boxes in the prompt. That is not Google’s old image-segmentation API. |

Request-shape notes that are part of the upgrade, not optional polish:

- OpenAI GPT-5.x: `reasoning.effort=low` so the 1200-token cap is not spent on thinking.
- Claude 5: omit `temperature` (non-default sampling is HTTP 400). Send `thinking: {type: disabled}` so adaptive thinking cannot empty the JSON reply.
- Gemini 3.x: `thinkingConfig.thinkingLevel=low`. Do not send `thinkingBudget` (HTTP 400). Skip `thought: true` parts when reading the reply.
- Grok 4.6: `reasoning.effort=low` for the same output-cap reason. Default reasoning on this model is high.

## In-family alternatives not wired

- **OpenAI:** `gpt-5.6-terra` / `gpt-5.6-luna` are cheaper. GPT-5.5 scored ~83.2% MMMU-Pro vs Sol ~83.0% in the same aggregator tables. Official “start here” remains Sol.
- **Anthropic:** Opus 5 is larger and more expensive. Fable 5.1 is a different product class. This chooser stays on Sonnet.
- **Google:** `gemini-3.1-pro-preview` if a Pro-class ID is required. `gemini-3.6-flash` / `gemini-3.8-flash` exist; 3.8 had public regression reports on 2026-09-03 and is not the documented GA “most intelligent Flash”. Do not guess a Flash ID from forum threads.
- **xAI:** none newer than Grok 4.6 as of this review.
- **Ideogram:** keep `/describe` V_3. Do not silently switch to v4 describe.

## Outside the current provider set

These score well on public vision boards. They are **not** drop-in chips.

- **Qwen3.8 Max / Qwen3-VL** — strong MMBench / competitive MMMU-Pro. No DashScope key in this app. Local Qwen describers were removed from `--ui` because no local server exists here.
- **ByteDance Seed 2.1** — high MMMU-Pro. Image generation is in `docs/provider-onboarding.md`; there is no describe target.
- **Kimi K3** — appears on MMMU-Pro. No key, no billing URL, no `ProviderActionHints` row.

Do not add a remote describe target without researched billing/key URLs in `ProviderActionHints` and the rest of the onboarding checklist.

Public MMMU-Pro / MMBench tables are aggregator scores. They are not a live, identity-preserving eval of this app’s JSON caption contract. Treat them as ranking evidence, not as a substitute for a failed provider call.

## Cost ceilings

`DescribeCostEstimate` is still an estimate, not a bill. After the 2026-09-03 model bump: OpenAI $0.04, Claude/Gemini/Grok/layout-map $0.03, Ideogram the published $0.01.
