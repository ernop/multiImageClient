# Ideogram History Export

Reusable scripts for archiving your **Ideogram web UI** generation history (prompts, metadata, and later images).

These tools call the same logged-in browser endpoint as the original [`ernop/ideogramHistoryDownloader`](https://github.com/ernop/ideogramHistoryDownloader) repo, now integrated here under `tools/ideogram-export/`.

Archive outputs contain personal prompts and auth-adjacent metadata. Keep them out of git.

See `docs/ideogram-history-archive.md` for the full workflow.

## Quick start

From the repo root:

```bash
python3 -m venv .venv-ideogram-export
source .venv-ideogram-export/bin/activate
pip install -r tools/ideogram-export/requirements.txt

cp tools/ideogram-export/config.template.json IdeogramHistoryExtractor/config.json
# Edit IdeogramHistoryExtractor/config.json — see below.

python tools/ideogram-export/fetch_history.py --archive-root IdeogramHistoryExtractor
python tools/ideogram-export/extract_prompts.py --archive-root IdeogramHistoryExtractor
```

## Auth config

While logged in at [ideogram.ai](https://ideogram.ai):

1. Open DevTools → **Network**.
2. Scroll your profile / generated gallery so the site calls `ideogram.ai/api/g/u`.
3. Copy from that request:
   - **Authorization** header (full value, starts with `Bearer`)
   - **Cookie** header (full string)
4. Put them in `IdeogramHistoryExtractor/config.json` with your Ideogram username.

Template:

```json
{
  "username": "your_username",
  "authorization": "Bearer …",
  "cookie": "session_cookie=…; cf_clearance=…; …"
}
```

Session tokens expire. Re-copy from the browser when fetches start failing with 401/403.

## What gets written

Default archive root: `IdeogramHistoryExtractor/`

```text
IdeogramHistoryExtractor/
  config.json                 # local secrets (gitignored)
  ideogram_requests/          # per-page JSON snapshots
  ideogram_data_all.json      # merged, deduped history
  fetch_log.jsonl             # audit log of fetch runs
  myPrompts/
    myPrompts-all.txt         # all your typed prompts
    myGenPrompts-all.txt      # magic-prompt / expanded variants
    myPrompts.txt             # public-only (when visibility known)
    myPrompts-private.txt
```

Re-runs merge by stable item id (`request_id`, `id`, etc.) so pagination drift does not duplicate rows in `ideogram_data_all.json`.

## Useful flags

```bash
# Smoke-test one page per pass (public + private)
python tools/ideogram-export/fetch_history.py --archive-root IdeogramHistoryExtractor --max-pages 1

# Public history only
python tools/ideogram-export/fetch_history.py --archive-root IdeogramHistoryExtractor --public-only

# Faster page delay (use carefully — Ideogram may rate-limit)
python tools/ideogram-export/fetch_history.py --archive-root IdeogramHistoryExtractor --page-delay 5

# Re-extract prompts from page snapshots instead of merged file
python tools/ideogram-export/extract_prompts.py --archive-root IdeogramHistoryExtractor --source pages
```

## Phase 2 (images)

Not implemented yet. The JSON includes image URLs/metadata; a separate image downloader will walk `ideogram_data_all.json` and persist files + sidecars. Run prompt export first.

## Personal data policy

Do not commit `config.json`, `ideogram_data_all.json`, `ideogram_requests/`, `myPrompts/`, or `fetch_log.jsonl` unless they are tiny sanitized fixtures for tests.
