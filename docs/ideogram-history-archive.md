# Ideogram History Archive

Local archive for Ideogram **website** generation history — prompts, settings metadata, and (later) downloaded images.

This is separate from the paid **Ideogram API** client in `IdeogramAPI/`. API-key runs only see API generations, not your full web library.

## Repository vs archive instance

Repository-owned:

```text
tools/ideogram-export/
docs/ideogram-history-archive.md
tools/ideogram-export/config.template.json
```

User/archive-owned (default root `IdeogramHistoryExtractor/`):

```text
IdeogramHistoryExtractor/
  config.json
  ideogram_requests/
  ideogram_data_all.json
  fetch_log.jsonl
  myPrompts/
```

Keep archive data out of git. It contains personal prompts and session-derived fetch logs.

## Workflow

### 1. Configure browser session auth

Copy `tools/ideogram-export/config.template.json` to `IdeogramHistoryExtractor/config.json`.

Fill in:

- `username` — Ideogram profile username (used in `user_id` query param)
- `authorization` — full `Authorization` request header from a logged-in `api/g/u` call
- `cookie` — full `Cookie` request header from the same call

Tokens expire; refresh from DevTools when requests fail.

### 2. Fetch history JSON

```bash
pip install -r tools/ideogram-export/requirements.txt
python tools/ideogram-export/fetch_history.py --archive-root IdeogramHistoryExtractor
```

The fetcher:

- calls `GET https://ideogram.ai/api/g/u` with filters `generations`, `upload`, `edit`, `upscales`
- walks public history, then private
- saves page snapshots under `ideogram_requests/`
- merges into `ideogram_data_all.json` keyed by stable ids
- appends run metadata to `fetch_log.jsonl`

Ideogram re-paginates history over time. Re-running is expected; dedupe keeps the merged file stable.

### 3. Extract prompt corpora

```bash
python tools/ideogram-export/extract_prompts.py --archive-root IdeogramHistoryExtractor
```

Writes deduplicated UTF-8 text files under `myPrompts/`:

- `myPrompts-all.txt` — prompts you typed
- `myGenPrompts-all.txt` — magic-prompt / expanded variants from `responses`

Upload-only and edit-only rows are skipped (same as the original downloader helper).

### 4. Use prompts in MultiImageClient (optional)

Point a prompt source at extracted files, e.g. `IdeogramHistoryExtractor/myPrompts/myPrompts-all.txt`, or wire them through `settings.json` → `PromptFiles`.

## Phase 2: full image archive

Planned follow-up: walk `ideogram_data_all.json`, download image URLs to disk with sidecar metadata, dedupe by generation id. Image URLs may expire; bulk download should happen soon after fetch when possible.

## Origin

Absorbed from [`ernop/ideogramHistoryDownloader`](https://github.com/ernop/ideogramHistoryDownloader) (2024) into `tools/ideogram-export/` with config-file auth, deduping merge, and CLI flags.
