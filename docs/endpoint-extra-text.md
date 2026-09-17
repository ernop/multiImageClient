# Endpoint and global appended text

## Decision — 2026-09-17

Open **Generator configuration → per endpoint → Append to all endpoints** to set persistent general directives.
The field belongs to endpoint configuration, not the per-prompt composer.
Existing saved text remains unchanged when this control moves.
Use this field for general directives shared by every selected endpoint.
An empty field adds nothing.

| Requirement | Behavior |
|---|---|
| Order | Send the base prompt, endpoint extra text, then global text. Separate nonempty sections with two newlines. |
| Scope | Apply to composer image generation, image editing, and describe/analysis endpoints. |
| Goal loops | Preserve manager-owned prompts. Neither composer suffix system applies to goal-loop jobs. |
| Persistence | Save each edit automatically in browser personal configuration, under `promptTools.globalAppendText`. Include configuration export/import. |
| Limits | Limit the global field to 16,000 characters. Limit each combined suffix to 16,000 characters. |
| Aggregate limit | Reject selected suffixes exceeding 64,000 characters in total. Count global text once per selected endpoint. |
| Prompt limits | Include both additions in character and UTF-8 byte notices. Preserve existing provider truncation rules. |
| History | Snapshot the combined suffix in each selected `generatorExtraTexts` entry. Preserve the base composer prompt. |
| Clearing | Clear the global field to disable general directives. Preserve endpoint settings. |

Browser configuration version 3 adds the global field.
Version 2 migration adds an empty field and preserves all existing values.
Version 1 imports retain their explicit GPT Image 2 guidance migration and add an empty global field.
The canonical browser storage key remains unchanged so existing settings remain discoverable.
Malformed, missing, unknown, or oversized imported fields fail validation.
Storage failures appear beside the global field.

## Endpoint review

The shared generator chooser exposes extra text and private notes for each configurable endpoint.
Its catalog includes hidden and unavailable endpoints.
Account preferences and browser preferences retain explicit blank overrides.
The GPT Image family retains its catalog daylight defaults and legacy GPT Image 2 migration.
Private notes remain configuration-only; they never enter job submissions.

`UiWorkflow.ParseGeneratorExtraTexts` validates selected keys, duplicates, types, lengths, and aggregate size.
`UiJobRunner.ApplyEndpointExtraText` applies the recorded suffix to each endpoint's separate prompt copy.
Image generation, edits, and describe paths use that function.
Accepted job events, viewer guidance, and describe comparison sheets use the recorded map.
The global field uses this existing map, so history displays exactly the combined text submitted for that endpoint.
No new server field or independent suffix application path is required.

## Files and verification

- `Ui/wwwroot/index.html`, `style.css`: global field and nearby status.
- `Ui/wwwroot/app.js`: composition, limits, persistence, and import validation.
- `Ui/wwwroot/personal-config.js`: version migration and first-visit defaults.
- `tools/test-global-append-preload.cjs`: browser persistence, import rejection, submission, and viewer regression coverage.
- `Ui/tests/personal-config.test.js`: configuration schema and migration tests.
