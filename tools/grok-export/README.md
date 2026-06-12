# Grok Export Tools

Reusable scripts for working with local `grok.com` data export archives.

These tools operate on a caller-provided archive root. The archive root may contain personal prompts, images, videos, post IDs, and logs, so keep archive outputs outside the repo.

See `docs/grok-web-export-archive.md` for the data model and workflow.

## Extract Export Zip

```powershell
python tools/grok-export/extract_export.py `
  --zip "C:\Path\To\grok-export.zip" `
  --archive-root "C:\Path\To\GrokArchive\WebExport"
```

Writes image assets, `prod-grok-backend.json`, `manifest.jsonl`, `videos.csv`, and `prompts.txt` into the archive root.

## Build Browser Index

```powershell
python tools/grok-export/build_archive_browser.py `
  --archive-root "C:\Path\To\GrokArchive\WebExport" `
  --backend-json "C:\Path\To\prod-grok-backend.json"
```

Writes:

```text
<ArchiveRoot>\archive_index.js
```

The generated index contains prompts, local relative file paths, IDs, and derived relationship data. Treat it as user/archive data, not source code.

## Download Videos

```powershell
python tools/grok-export/download_videos.py `
  --archive-root "C:\Path\To\GrokArchive\WebExport" `
  --workers 3
```

The downloader reads `<ArchiveRoot>\videos.csv`, writes mp4 files under `<ArchiveRoot>\Videos`, and appends JSONL audit files in the archive root.

Use lower worker counts for retries or flaky network behavior.

## Personal Data Policy

Do not commit outputs from these tools unless they are small, sanitized fixtures intentionally created for tests.
