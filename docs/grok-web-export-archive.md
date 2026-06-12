# Grok Web Export Archive

This document describes the reusable support system for working with `grok.com` data exports and local Grok Imagine archives.

Grok archive instances are user data. Keep them outside the repository. Reusable documentation, scripts, and templates belong in this repository under `docs/` and `tools/`.

## Repository Versus Archive Instance

Repository-owned files:

```text
docs/grok-web-export-archive.md
tools/grok-export/
```

User/archive-owned files:

```text
<ArchiveRoot>/
  archive_browser.html
  archive_index.js
  manifest.jsonl
  prompts.txt
  videos.csv
  video_downloads.jsonl
  video_failures.jsonl
  video_download_events.jsonl
  videos_source_snapshot.csv
  videos_source_snapshot.csv.sha256
  Images/
  Uploads/
  Unmapped/
  Videos/
```

The archive instance may contain personal prompts, images, videos, post IDs, links, and logs. It must not be checked in.

## What Grok Exports Contain

The Grok backend export is not a relational ORM dump. For Imagine media posts, the useful record shape is flat:

```json
{
  "id": "post uuid",
  "user_id": "user uuid",
  "original_prompt": "prompt text",
  "media_type": "image or video",
  "create_time": "ISO timestamp",
  "link": "https://grok.com/imagine/post/..."
}
```

The exports inspected so far do not include an explicit relationship such as `sourceImageId`, `parentImageId`, or `video.source_image_id`. That means exact video-to-source-image matching is not available from the data we have. Prompt grouping and chronological ordering are exact; source-image candidates are heuristic unless a future export includes a real parent/source field.

## Extracted Files

### `manifest.jsonl`

One JSON object per exported image-like asset:

```json
{
  "id": "asset uuid",
  "kind": "image",
  "source": "media_post | file_attachment | unknown",
  "createTime": "ISO timestamp",
  "prompt": "prompt text if known",
  "file": "Images/...",
  "bytes": 123456
}
```

### `videos.csv`

The source list for Grok Imagine video posts:

```csv
create_time,prompt,link
2025-10-20T21:27:53.321708Z,,https://grok.com/imagine/post/<post-id>
```

The browser label `Video row N` means row `N` in `videos.csv`. The stable identifier is the post UUID from `link`.

### Video Logs

`video_downloads.jsonl` is the success manifest. A row should be considered successfully acquired only when it points to an existing non-empty local mp4.

`video_failures.jsonl` is append-only failure history. A failure line can be stale if a later run succeeded.

`video_download_events.jsonl` is the full audit trail for run starts/completions, skipped rows, duplicates, and debugging.

## Local Browser Behavior

The archive browser is static HTML/JS and should load assets from the local archive instance, not from Grok or X.

Expected behavior:

- videos load from local relative paths such as `Videos/<file>.mp4`,
- remote media URLs and Grok links are metadata only,
- suspect videos are hidden from normal browsing,
- one active `<video>` element is used for the selected item,
- volume/mute/playback speed are remembered in browser `localStorage`,
- URL hashes identify the selected local archive item.

Hash URLs look like:

```text
#video/<post-id>?path=Videos%2F<file>.mp4
```

## Suspect Videos

A suspect video is one where the source post ID and resolved remote mp4 ID do not match. In practice, many post pages can accidentally resolve to the same stale/feed/nearby mp4. Those records should not be treated as true acquisitions.

The browser should keep suspect videos out of normal browsing. They may remain in logs and generated indexes for auditing.

## Updating an Archive Instance

Use this process for any new Grok export. Substitute your own local paths.

1. Save the downloaded Grok export zip outside the repo.
2. Extract metadata/assets into an archive root.
3. Download videos into `<ArchiveRoot>/Videos`.
4. Rebuild `<ArchiveRoot>/archive_index.js`.
5. Open `<ArchiveRoot>/archive_browser.html`.

Reusable scripts should live under `tools/grok-export/`. They should take `--archive-root`, `--zip`, or other explicit path arguments rather than hardcoding a personal path.

## Retry Strategy

The downloader should be idempotent. Restarting it should quickly skip rows whose manifest entry points to an existing non-empty file.

For failed rows:

- rerun the downloader over the full CSV with lower concurrency,
- inspect failures that say the page had no `share-videos` URL,
- rebuild `archive_index.js` after any new successes.

Failure logs are append-only. Current state is determined by successful manifest entries and files on disk.

## Data Integrity Checklist

After refreshing an archive instance:

- confirm all `manifest.jsonl` file references exist,
- confirm `videos_source_snapshot.csv.sha256` exists,
- inspect the latest `video_download_events.jsonl` run completion,
- rebuild `archive_index.js`,
- open `archive_browser.html`,
- confirm image/video/downloaded/failed/suspect counts look plausible.

## What Should Not Be Committed

Do not commit:

- downloaded Grok export zips,
- extracted personal backend JSON,
- `manifest.jsonl`,
- `videos.csv`,
- `prompts.txt`,
- images/videos/uploads/unmapped assets,
- `video_downloads.jsonl`, `video_failures.jsonl`, or `video_download_events.jsonl`,
- generated archive indexes containing personal prompts/IDs,
- source pin exports from a personal archive.

Reusable code, templates, and generic documentation belong in the project and should avoid hardcoded personal paths.
