# Model catalog & configurable prompt assistants — 2026-11-19

Settled 2026-11-19. Updates the image, vision/describe, and text-assistant catalogs to current live-verified provider models and replaces hardcoded prompt-tool models with browser-local settings in portable personal configuration version 6.

## Image generation targets added

| UI key | Label | Provider / endpoint | Notes |
|---|---|---|---|
| `gpt26-sunburst` | `gpt-image-2.6 sunburst` | OpenAI `/v1/images/generations` & `/edits` (`gpt-image-2.6-sunburst`) | Flagship OpenAI image model (released 2026-11-04). Same size envelope, streaming partials, multi-input `/edits` (up to 4 images), `xhigh`/`max` quality tiers, 32,000-char limit, and default anti-murk suffix as GPT Image 2.5. Joins `only-sota` and `text-ok`. |
| `gpt26-flare` | `gpt-image-2.6 flare` | OpenAI `/v1/images/generations` & `/edits` (`gpt-image-2.6-flare`) | Fast everyday GPT Image 2.6 tier. Same API surface and limits as Sunburst. Joins `only-sota` and `text-ok`. |
| `ideogram-v46` | `Ideogram 4.6` | Ideogram `POST /v2/image/generate/ideogram-4-6` & `POST /v2/image/precise-edit/ideogram-4-6` | Released 2026-11-19. Live-verified via `?dry_run=true`: 1K/2K Generate `$0.008` / `$0.03` / `$0.06` / `$0.10` (`very_low`/`low`/`medium`/`high`); native 4K Generate (`detail = max`: `auto_4k`, `4096x4096`, `4800x3200`, `3200x4800`, `5120x2880`, `2880x5120`) `$0.06` / `$0.12` / `$0.20` (`low`/`medium`/`high`); Precise Edit `$0.008` / `$0.03` / `$0.06` / `$0.22`, keeps source size and rejects explicit AR on image jobs. Prompt limit 10,000 chars. Joins `only-sota` and `text-ok`. |
| `bfl-flux3` | `FLUX 3 Image` | BFL `POST /v1/flux-3-image` | Official OpenAPI schema enforces `additionalProperties: false` (no `output_format` or `seed`). Supports up to 10 reference images (`images[]`), `aspect_ratio`, and `resolution` (`1k` `$0.048`, `2k` `$0.100`, `4k` `$0.607`; reference edits cap at `2k` / `match_input`). Joins `only-sota` and `text-ok`. |
| `bfl-flux3-klein` | `FLUX 3 Image Klein` | BFL `POST /v1/flux-3-image-klein` | Fast FLUX 3 tier on the same OpenAPI schema (`1k` `$0.009`, `2k` `$0.014`, `4k` `$0.036`). |
| `recraft-v41-flash` | `Recraft V4.1 Flash V2` | Recraft `recraftv4_1_flash_v2` | Fast 1K raster model (`$0.007`/image, 10,000-char prompt limit, `n` 1–6). Text-to-image only (Recraft does not support image-to-image on Flash V2; excluded from `ImageCapableKeys` and hard-rejected in `RecraftGenerator` if given an input image). Joins `text-ok`. |

### OpenAI `gpt-image-1` retirement

OpenAI retired `gpt-image-1` on 2026-10-23 (confirmed absent from `/v1/models`). `UiJobRunner.KeyGpt1` and `ImageGeneratorApiType.GptImage1` stay defined for historical job replay, `DescribeAvailabilityProblem` marks `gpt1` unavailable with the retirement reason, and `gpt1` was removed from `text-ok`. `gpt-image-1-mini` remains live.

### Krea 3 deferred

`KreaApiKey` is unconfigured locally and on both production instances, and Krea 3's exact API schema was not retrieved. Per the fail-closed policy, no Krea 3 target was added.

## Vision / describe targets updated and added

- `describe-openai`: upgraded from `gpt-5.6-sol` to `gpt-6.2-sol` (`reasoning.effort = low`, no `temperature`).
- `describe-openai-astra` (new): `gpt-6.2-astra` flagship vision at low reasoning.
- `describe-claude`: upgraded from `claude-sonnet-5` to `claude-sonnet-5-5`. Live verification showed `claude-sonnet-5-5` rejects `thinking: { type: "disabled" }` (HTTP 400) and accepts `thinking: { type: "between_tools" }` with no tools for direct text replies.
- `describe-gemini-neon` (new): `gemini-4-neon` (`thinkingLevel = low`, no `temperature`, `maxOutputTokens = 64000`, `$3 / $15` per MTok).
- `describe-gemini-carbon` (new): `gemini-4-carbon` (`thinkingLevel = low`, no `temperature`, `maxOutputTokens = 32768`, `$2 / $10` through 2026-11-30 Pacific, `$4 / $20` from 2026-12-01).
- `describe-gemini-hydrogen` (new): `gemini-4-hydrogen` (`thinkingLevel = low`, no `temperature`, `maxOutputTokens = 64000`, `$1.50 / $7.50` per MTok).
- `describe-grok`: upgraded from `grok-4.6` to `grok-4.7` (`reasoning.effort = low`, `store = false`, `$2 / $6` per MTok).
- `describe-gemini` and `layout-map` stay on `gemini-3.8-flash`; `describe-gemini-argon` stays on `gemini-4-argon`.

## Text LLM / manager / critic catalog additions

`ManagerCatalog.All` adds 9 models while preserving every historical key:
- OpenAI: `manager-gpt-62-astra` (`gpt-6.2-astra`, `$8/$40`, 2x input / 1.5x output above 272K input tokens), `manager-gpt-62-sol` (`gpt-6.2-sol`, `$2/$10`, same >272K tier), `manager-gpt-61-terra` (`gpt-6.1-terra`, `$1/$5`).
- Anthropic: `manager-claude-fable-5-2` (`claude-fable-5-2`, `$8/$40`), `manager-claude-haiku-5-5` (`claude-haiku-5-5`, `$1/$5`).
- Google: `manager-gemini-4-neon` (`gemini-4-neon`, `$3/$15`, 64,000 cap), `manager-gemini-4-carbon` (`gemini-4-carbon`, `$2/$10` -> `$4/$20` on 2026-12-01 Pacific, 32,768 cap), `manager-gemini-4-hydrogen` (`gemini-4-hydrogen`, `$1.50/$7.50`, 64,000 cap).
- xAI: `manager-grok-4-7` (`grok-4.7`, `$2/$6`).

All entries are available in goal loops (managers and critics), character writing (`PromptSubjectSynthesis`), and all prompt assistant tools.

## Configurable prompt assistant models & Personal Configuration v6

Prompt advice, both flesh-out quick buttons, over-limit condensing, prompt splitting, and character writing no longer hardcode Anthropic-only models:

- **Backend**:
  - `DirectedPromptRewrite.ResolveModel` and `PromptSplit.ResolveModel` accept any key or model ID in `ManagerCatalog.All` and fail closed with HTTP 400 on unknown or unconfigured models.
  - `POST /api/prompt/advice`, `POST /api/prompt/condense`, and `POST /api/prompt/split` accept a `model` form field (defaulting to Opus 5.5 for advice/split and Sonnet 5.5 for condense) and route provider error hints via `ProviderActionHints.ForProvider(definition.Provider, failure)`.
  - `DirectedPromptRewrite.RewriteAsync` calls `ManagerCatalog.Build(definition, settings, expectJson: false)` so OpenAI, Anthropic, Google, and xAI models share one transport without forcing JSON output on plain-text rewrites.
  - `UiJobRunner.ResolveCondensedPrompts` and `PromptSplit.ResolveSubmission` accept stored exchanges created by any model in `ManagerCatalog.All`.
  - `/api/config` publishes `promptAssistantModels` and `promptAssistantDefaults` while preserving `claudeAdvice`, `promptRewrites`, `promptCondense`, and `promptSplit`.
- **Frontend & Personal Configuration v6**:
  - Settings (`#settings-panel`) contains a **Prompt assistant models** section with 6 single-line selectors (`advice`, `fleshOutPrimary`, `fleshOutSecondary`, `condense`, `split`, `characterWriter`) plus **reset to defaults**.
  - Empty string (`""`) selects the server default. Selecting a model immediately updates composer button labels, dialog headings, and submit payloads, and persists locally in `personal-config.js` Version 6 under `promptTools.models`.
  - `migrateVersion5` explicitly upgrades Version 5 documents by adding empty `promptTools.models` slots; unknown versions and unknown model keys fail closed on import.

## Architecture consolidation & UI copy cleanup (2026-11-19)

- **Single target catalog (`UiTargetCatalog`)**: `MultiImageClient/Implementation/UiTargetCatalog.cs` holds every UI image, video, describe, and analysis target's display name, contact-sheet provider, scheduler lane, billing account, capability flags, prompt limits, multi-input limit, default extra text, and `/api/config` metadata. `GeneratorPresentation`, `UiTargetScheduler`, `ProviderActionHints`, `UiJobRunner`, `UiWorkflow`, and `app.js` (via `/api/config` `maxInputImages` and `defaultExtraText`) derive from it instead of maintaining parallel switch tables and hardcoded JS arrays.
- **Modularized core files**: split `UiJobs.cs` into `UiJobs.cs` (job model/records), `UiJobStorage.cs` (persistence, card cache, registry), `UiShapeMapping.cs` (geometry/detail mapping), and `UiJobRunner.cs` (execution). Split `UiWorkflow.cs` routes into partial files `UiWorkflow.GoalLoops.cs`, `UiWorkflow.PromptTools.cs`, and `UiWorkflow.Community.cs` alongside existing partials.
- **UI help text**: updated `#opts-help-panel`, `#paste-zone`, and `#multi-input-hint` to reflect GPT Image 2.6, Ideogram 4.6 (2K/4K + Precise Edit), FLUX 3 / Klein (1K/2K/4K + multi-reference), and Recraft V4.1 Flash V2 (text-only), and trimmed redundant explanatory paragraphs across `index.html`, `generator-chooser.js`, and `goal.html`.
