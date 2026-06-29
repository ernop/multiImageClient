# Describe Endpoint Evaluation

This harness generates a fixed two-person walking fixture set, then tests whether describe
endpoints mention the visible facts in their descriptions.

## Fixture Set

The matrix is:

- ethnicity: `south_korean`, `japanese`, `chinese`, `english`, `egyptian`, `african`, `russian`, `polish`
- gender: `man`, `woman`
- age: `18`, `28`, `42`

Total: 48 images.

Each image prompt is exactly this shape:

```text
A natural full-body street photo of two {age}-year-old {ethnicity} {men|women} walking together along a palm-lined street in Florida. They seem relaxed, cheerful, and comfortable with each other, like a casual photo they might post on Instagram. Realistic candid photography, clear full normal daytime lighting, bright exposure, casual outfits, clear head-to-toe view. Do not make the image dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark.
```

Example:

```text
A natural full-body street photo of two 18-year-old South Korean women walking together along a palm-lined street in Florida. They seem relaxed, cheerful, and comfortable with each other, like a casual photo they might post on Instagram. Realistic candid photography, clear full normal daytime lighting, bright exposure, casual outfits, clear head-to-toe view. Do not make the image dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark.
```

Recommended generator: OpenAI `gpt-image-2` with the script defaults
(`1024x1024`, `quality=low`, `moderation=low`). It is the most cost-controlled
fixture generator currently wired into this tool.

## Universal Image Prompt Defaults

When creating fixture prompts for OpenAI image models, including `gpt-image-2`,
include the lighting and clarity preference explicitly. Apply the same default to
other present or future image providers unless a test is specifically about dark,
night, low-key, gloomy, or murky output.

Short reusable addendum:

```text
Clear, bright, full normal daytime lighting by default. Not dim, murky, grimy, muddy, gloomy, shadow-choked, underexposed, dusk-like, night-like, or dark. Prefer readable, coherent, visually organized images with clean composition, clear separation of subjects or groups, concise high-contrast text when text is needed, and attractive balanced color. Favor clarity over murky cinematic drama.
```

Before accepting a generated fixture set, check that images are clearly daytime,
bright enough, easy to understand quickly, and free of accidental stormy,
apocalyptic, muddy, or over-gloomy styling.

## Output Location

Generated images are written as they are produced to:

```text
saves/describe-eval/portrait-fixtures/images
```

Metadata is written to:

```text
saves/describe-eval/portrait-fixtures/manifest.jsonl
```

Example filename:

```text
south_korean__women__age_18__friends__florida_walk.png
```

## Dry Run

```powershell
python tools/describe-eval/describe_eval.py
```

This writes only the manifest. No paid API calls are made.

## Generate A Small Smoke Test

```powershell
python tools/describe-eval/describe_eval.py `
  --limit 2 `
  --generate `
  --generator openai
```

## Describe Existing Fixtures

```powershell
python tools/describe-eval/describe_eval.py `
  --limit 2 `
  --describe `
  --endpoints grok,openai
```

## Generate And Describe

```powershell
python tools/describe-eval/describe_eval.py `
  --limit 2 `
  --generate `
  --describe `
  --generator openai `
  --endpoints grok,openai
```

## Keys

By default the script reads:

```text
MultiImageClient/settings.json
```

It can also use environment variables:

- `OPENAI_API_KEY`
- `XAI_API_KEY`
- `GOOGLE_GEMINI_API_KEY`
- `ANTHROPIC_API_KEY`
- `IDEOGRAM_API_KEY`

## Describe Results

Descriptions are written to:

```text
saves/describe-eval/portrait-fixtures/describe_results.jsonl
saves/describe-eval/portrait-fixtures/describe_summary.csv
```

The default scorer is an LLM text judge, not a keyword matcher. After each
describe endpoint returns free text, a cheap text-only judge model reads that
response and returns parseable JSON for the two-person count, sex, approximate
age, ethnicity/ancestry, clothing, and style/body look. Location and activity
are intentionally not scored.

The judge returns `0`, `0.5`, or `1` for each category, plus the text it
extracted and a short reason. Broad but compatible labels can receive partial
credit: for example, `Asian` receives half credit for a Chinese, Japanese, or
South Korean fixture when the more specific label is omitted. Use
`--score-mode rules` only for debugging the older keyword scorer.

## Evaluate An Existing Provider Sample

After running `--provider-sample-showcase`, use the raw images from one source
provider as the fixture set for describe endpoint evaluation:

```powershell
python tools/describe-eval/evaluate_generated_sample.py `
  --prompt-file saves/2026-06-12-Friday/provider_people_fixture_sample_prompts_20260612113448.txt `
  --image-glob "saves/2026-06-12-Friday/20260612114*_gpt-2_1024x1024_quallow_A_natural_full-body_street_photo*_Raw.png" `
  --out saves/describe-eval/gpt-image-2-sample-20260612113448 `
  --describe `
  --report `
  --overwrite-results
```

This imports the raw source images, calls the describe endpoints, writes
`describe_results.jsonl` / `describe_summary.csv`, then renders PNG reports. The
per-image reports use endpoint rows and score-category columns:

```text
reports/by_image/*.png
reports/by_endpoint/*.png
```
