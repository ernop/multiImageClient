# Private Admin-Only Lifetime Prompt Archive

Date: 2026-11-20

## Goal

Load the owner's complete historical image-generation prompt archive (2022–2026) into MultiImageClient behind strict admin-only access, with instant random recall (**View 10 random prompts**), full-text search, era/source filtering, and one-click loading into the composer to test older prompts on current image models.

## Settled Requirements and Privacy Contract

| Requirement | Settled Behavior |
|---|---|
| Admin-only isolation | Accessible only to the authenticated owner (`ernieMultiZone`) on authenticated instances, and in local open mode when `UiAuth` is not enforced. Normal authenticated members receive HTTP 403 on `/prompt-archive.html` and every `/api/admin/prompt-archive/*` endpoint, `promptArchive: null` in `/api/config`, and hidden `#prompt-archive-link`. |
| Owner-only ingestion | Never ingest prompts authored by other accounts (such as `dallasVille` or `governorOfThings`) or private correspondence tables. On Vibecoders (`allowEmptyCreator = false`), only `CreatorLogin == "ernieMultiZone"` is imported. On AVE and local (`allowEmptyCreator = true`), `ernieMultiZone` and legacy pre-login empty `CreatorLogin` records are imported; non-owner accounts are excluded. |
| Disk-backed storage (resident memory rule) | Prompt data stays in SQLite (`prompt-archive.sqlite3`, mode `0600`) with FTS5 indexing. Zero prompt lists are cached in process RAM; every query hits SQLite directly and caps page sizes (`random` max 50, `search` max 100). |
| No personal data in git | `**/prompt-archive.sqlite3*` and `**/saves/**` are gitignored. The repository holds only code, schema, builder script (`tools/build-prompt-archive.py`), tests, and documentation. |
| Not part of portable personal configuration | Prompt archive contents are historical data, not user-controlled preferences, and remain excluded from `personal-config.js`. |
| Fail-closed validation | Wrong `schema_version`, corrupt SQLite files, out-of-range limits, invalid sorts, invalid years, or invalid IDs fail closed with explicit errors. A missing database file reports `exists: false` cleanly without fabricating empty results or crashing startup. |
| Live capture & sync | New owner-authored image/video jobs submitted on an instance are recorded into `prompt-archive.sqlite3` if the file exists. `POST /api/admin/prompt-archive/sync` (requiring `X-Mic-Manage: 1`) idempotently imports owner jobs from `UiHistory/` and goal loops from `UiGoalLoops/`. |
| Composer handoff | **Try in composer** and **New tab** open `./?archivePrompt=<id>`, fetch `GET /api/admin/prompt-archive/items/{id}`, load `promptText` into `#prompt` via `applyViewedPromptToComposer`, strip `?archivePrompt=` via `history.replaceState`, and focus `#prompt`. |

## Initial Loaded Archive Inventory (2026-11-20)

Built by `tools/build-prompt-archive.py` into `saves/prompt-archive.sqlite3` (18,879 unique prompts, 28,539 occurrences, 9 sources, ~31.4 MiB):

- **Midjourney History (2022–2023)** (`gdocs-midjourney-all`): Midjourney spreadsheet and TSV exports (Aug 2022 – Feb 2023)
- **Early AI Image Prompts (2022)** (`gdocs-early-2022`): early 2022 undated prompt document
- **Hard AI Prompts (2023)** (`gdocs-hard-prompts`): May 2023 benchmark prompts
- **Midjourney Physiognomy Study (2022)** (`gdocs-midjourney-physiognomy`): Dec 2022 portrait study
- **Ideogram Lifetime Archive (2024–2026)** (`ideogram-archive`): May 2024 – Jul 2026 (`IdeogramHistoryExtractor/`)
- **ChatGPT Image Generation (2023–2026)** (`chatgpt-images`): Oct 2023 – Oct 2026 (`dalle.text2im` + assistant image-gen tool prompts)
- **MultiImageClient Local / AVE Production / Vibecoders Production** (`mic-local`, `mic-ave`, `mic-vibecoders`): owner-only `UiHistory`, `UiGoalLoops`, non-UI `generation-history.sqlite3`, and `grok_ledger.jsonl`

`UiJobRunner.DefaultDescribeInstruction` and pure URL strings are excluded during normalization.

## Storage Path and Schema

- Default path: `Path.Combine(settings.ImageDownloadBaseFolder, "prompt-archive.sqlite3")`, overridable via `Settings.UiPromptArchiveDbPath`.
- Production locations (inside each service's existing `ReadWritePaths`, permissions `0600`):
  - AVE: `/var/lib/multiimageclient/saves/prompt-archive.sqlite3` (owner `multiimageclient:multiimageclient`)
  - Vibecoders: `/var/lib/multiimageclient-env-vibecoders-ai-generation/saves/prompt-archive.sqlite3` (owner `mic-vibecoders-ai-generation:mic-vibecoders-ai-generation`)
- Schema version `1` (`PRAGMA journal_mode = DELETE`):
  - `meta(key, value)` — includes `schema_version = '1'`
  - `sources(source_id, label, category, origin_hint, total_occurrences, unique_prompts, earliest_utc, latest_utc)`
  - `prompts(id, norm_hash, prompt_text, kind, char_len, word_count, first_seen_utc, last_seen_utc, first_year, last_year, occurrence_count, primary_source, sources_json, models_json, negative_prompt, extra_meta_json, origin_instance, origin_job_id)`
  - `prompt_sources(prompt_id, source_id)`
  - `prompt_occurrences(id, prompt_id, source_id, source_key, occurred_at_utc, model, origin_instance, origin_job_id, details_json)`
  - `prompts_fts` (FTS5 virtual table synchronized via SQLite triggers)

## HTTP API (Admin-Only, `Cache-Control: no-store`)

- `GET /api/admin/prompt-archive/stats` — total unique prompts, total runs, per-source breakdown, per-year counts, per-kind counts.
- `GET /api/admin/prompt-archive/random` — uniform partial Fisher-Yates sample using `RandomNumberGenerator.GetInt32`. Parameters: `count` (1–50, default 10), `q`, `source` (single or comma-separated source IDs, max 20), `year` (`0`/`undated` or 4-digit year), `kind` (default `image`), `minChars`, `maxChars`, `exclude` (comma-separated seen IDs, max 200).
- `GET /api/admin/prompt-archive/search` — paginated FTS5 + filter search. Parameters: `q`, `source`, `year`, `kind`, `minChars`, `maxChars`, `sort` (`relevance`, `newest`, `oldest`, `most_used`, `longest`), `page` (≥1), `pageSize` (1–100, default 25).
- `GET /api/admin/prompt-archive/items/{id}` — prompt metadata plus up to 50 occurrences ordered newest-first; `canOpenJob` is true only when `originInstance` matches the running instance.
- `POST /api/admin/prompt-archive/sync` — requires `X-Mic-Manage: 1`; scans the running instance's `UiHistory/` and `UiGoalLoops/` for new owner entries.

## Files

- `ImageGenerationClasses/Settings.cs` (`UiPromptArchiveDbPath`)
- `MultiImageClient/Implementation/UiPromptArchive.cs`
- `MultiImageClient/Workflows/UiWorkflow.PromptArchive.cs`
- `MultiImageClient/Workflows/UiWorkflow.cs`
- `MultiImageClient/Ui/wwwroot/prompt-archive.html`
- `MultiImageClient/Ui/wwwroot/prompt-archive.js`
- `MultiImageClient/Ui/wwwroot/admin.html`
- `MultiImageClient/Ui/wwwroot/admin-prompt-archive.js`
- `MultiImageClient/Ui/wwwroot/index.html`, `app.js`, `style.css`, `environment-storage.js`
- `tools/build-prompt-archive.py`
- `MultiImageClient.Tests/UiPromptArchiveTests.cs`
- `tools/test-prompt-archive-browser.cjs`
