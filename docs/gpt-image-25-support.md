# GPT Image 2.5 Support (gpt-image-2.5-sunburst / gpt-image-2.5-flare)

Added 2026-09-08, the day OpenAI released the models.

## What the models are

OpenAI released GPT Image 2.5 on 2026-09-08 as two models on the existing
Images API:

- **`gpt-image-2.5-sunburst`** — the most capable tier: strongest editing
  precision, instruction following, and text rendering.
- **`gpt-image-2.5-flare`** — the fast everyday tier: quicker and cheaper
  with the same API surface.

Both ride the same endpoints as gpt-image-2 (`POST /v1/images/generations`,
`POST /v1/images/edits`), the same multipart edit contract with ordered
`image[]` inputs, the same streaming SSE partials on `/generations` with
`n=1`, the same size envelope (edges multiples of 16, max edge < 3840,
655 360–8 294 400 total pixels, ratio ≤ 3:1), and `moderation` auto/low.
They add two **quality tiers above `high`: `xhigh` and `max`**. Earlier GPT
Image models reject those two values with HTTP 400.

## Settled decisions (2026-09-08)

| Decision | Behavior | Rationale |
|---|---|---|
| One parameterized generator class | `GptImage2Generator` / `GptImage2EditGenerator` take optional `modelId` + `apiType` constructor parameters (defaults preserve gpt-image-2 behavior). No new generator classes. | The wire contract is identical; duplicating the SSE loop and multipart code would fork the OOM-hardened partial reader. |
| UI keys | `gpt25-sunburst`, `gpt25-flare`; labels `gpt-image-2.5 sunburst` / `gpt-image-2.5 flare`. | Matches the existing key pattern (`gpt2`, `gpt1-mini`). |
| CLI short names | `gpt25-sunburst` (alias `sunburst`), `gpt25-flare` (alias `flare`) in `--showcase --gens` and the REPL `:gens` vocabulary. | Same vocabulary across surfaces. |
| Quality tiers | Enum gains `xhigh = 5`, `max = 6`. `GptImage2Generator.ModelSupportsQuality` validates at construction. The UI and REPL map `xhigh`/`max` down to `high` for gpt-image-2 before the call (option-mapping policy 2026-07-20: pre-call input mapping, not a failure fallback). | gpt-image-2 rejects the new values; a selected non-2.5 target must still run. |
| Quality pickers | Composer and goal-loop `quality` selects list low/medium/high/xhigh/max; goal-loop server validation accepts the two new values. | The new tiers must be reachable everywhere quality is chosen. |
| Cost estimates | Token rates match gpt-image-2 ($30/1M output tokens). Per-image ceilings: low $0.02, medium $0.08, high $0.25, and reporting ceilings **xhigh $0.35, max $0.50** pending published per-image token counts. | OpenAI has not published token consumption for the new tiers; ceilings keep the session cost bar honest. |
| API types | `ImageGeneratorApiType` 48–51: `GptImage25Sunburst`, `GptImage25Flare`, `GptImage25SunburstEdit`, `GptImage25FlareEdit`. All gate on `OpenAIApiKey`, ride the `openai` scheduler lane, save `.png`. | Exact identity in archives and traces. |
| Multi-input | Both 2.5 targets receive every attached input (up to 4) through `/edits`, like gpt-image-2. The frontend's `GptImageFamilyKeys` and the server's multi-input logging cover the family. | Same endpoint contract. |
| Anti-murk default | Both 2.5 targets get the same default `append extra text` daylight suffix as gpt-image-2, and a blank value logs the same loud warning. | Same lineage, same drift toward dark cinematic output; see Universal Image Prompt Defaults. |
| Sketch capability | Both 2.5 targets are in `SketchCapableKeys`, assumed from the gpt-image-2 lineage, **pending an owner live validation pass**. | Excluding them would wrongly auto-deselect them in the sketch dialog; the set is owner-validated, so confirm and correct if testing disagrees. |
| Default-on | Neither 2.5 target is default-on for new windows; `gpt2` keeps its slot. | Do not raise default cost exposure; users opt in. |
| Standard groups | The immutable standard selection groups (Thinking ones / can do famous people / text ok) are unchanged. | Group membership is owner-curated from live results; no 2.5 results exist yet. |
| Streaming | 2.5 `/generations` with `n=1` streams partials through the existing pooled `Utf8SseLineReader` path, including UI progression snapshots. | Same SSE framing; the memory-hardened reader must not fork. |
| Billing hints | Both keys map to the existing OpenAI billing/keys URLs in `ProviderActionHints`; `ProviderActionHintsTests` covers them. | Required for every remote UI target. |

## Filename tags

`GetFilenamePart` uses `gpt-25-sunburst` / `gpt-25-flare` (derived from the
model ID; gpt-image-2 keeps its historical `gpt-2` tag).

## Not done yet

- No live provider call has been made from this change (no `OpenAIApiKey`
  in the build environment). First real generation should verify: both model
  IDs are accepted, `xhigh`/`max` are honored, streaming still frames the
  same way, and observed token consumption for the new tiers (then replace
  the $0.35/$0.50 reporting ceilings with derived values).
- Owner live validation of sketch-following for both 2.5 models.
- Goal-loop generator picker inherits the new targets automatically from the
  shared catalog; no separate work was needed.
