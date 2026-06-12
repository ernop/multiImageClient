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

## Reconstruct the Relationship Graph (authoritative)

The flat export does not contain lineage (which video came from which image, which
video extends which). That lineage only exists in grok.com's logged-in internal REST
API. The following pipeline recovers it and renders a navigable local UI. See
`docs/grok-archive-restructure-prd.md` for the full design and confirmed schema.

### 1. Harvest the REST graph from a logged-in browser

The authoritative endpoint is `POST https://grok.com/rest/media/post/list` with body
`{ "limit": 100, "filter": { "source": "MEDIA_POST_SOURCE_LIKED" }, "cursor": <ms> }`.
It returns your own posts (images) with nested `childPosts` (videos), each carrying the
real lineage fields:

- `originalPostId` — the immediate parent (image for image→video, video for an extension)
- `originalRefType` — `(absent)` direct image→video, `ORIGINAL_REF_TYPE_VIDEO_EXTENSION`,
  `ORIGINAL_REF_TYPE_IMAGE_EDIT`, `ORIGINAL_REF_TYPE_MULTI_REF_IMAGE_EDIT`
- `mode` — the guide (`normal`, `custom`, `extremely-spicy-or-crazy`, …)
- `videoDuration`, `videoExtensionStartTime`, `thumbnailImageUrl`,
  `lastFrameThumbnailImageUrl`, `mediaUrl`/`hdMediaUrl`, `resolution`, `modelName`

Because this needs your session cookies, harvest from inside the logged-in browser and
relay the bytes to a tiny local receiver:

```powershell
python tools/grok-export/harvest_relay.py --archive-root "C:\Path\To\WebExport" --port 8777
```

Then, in a logged-in grok.com browser devtools console, page the API and POST each page
to `http://127.0.0.1:8777/append` (loopback is a trustworthy origin, so https→localhost
is allowed; the relay sends permissive CORS). The relay writes `rest_posts.jsonl`. The
same relay also accepts source-image bytes at `POST /media?id=<uuid>&ext=<ext>` and serves
a fetch worklist at `GET /needlist` (reads `_need_images.json`).

### 2. Build the normalized graph

```powershell
python tools/grok-export/build_graph.py --archive-root "C:\Path\To\WebExport"
```

Merges `rest_posts.jsonl` (authoritative lineage) with `video_downloads.jsonl`,
`manifest.jsonl`, and `ApiImages/` into `archive_graph.js` (`window.GROK_GRAPH`): nodes
(images/videos with mode, duration, refType, local file, provenance), typed edges
(`derivedFrom`, `extends`, `editOf`; confidence 1.0 = authoritative), and lineage clusters.

### 3. Generate poster thumbnails

```powershell
python tools/grok-export/make_posters.py --archive-root "C:\Path\To\WebExport" --workers 12
```

Extracts a first-frame `Posters/<post_id>.jpg` for every downloaded mp4 (needs `ffmpeg`).

### 4. Open the viewer

Copy `tools/grok-export/archive_graph.html` into the archive root (next to
`archive_graph.js`) and open it. This is now the main viewing surface for the archive:
search/filter lineages or switch to **By day** on the left; the selected lineage's source
image, readable video-variant fan, extension chains, prompt phrase links, and custom video
player are in the center; per-node details and "Copy full prompt" live on the right. Media
loads locally (`Videos/…mp4`, `Posters/…jpg`, `Images/…`, `ApiImages/…`). Opening via
`file://` works in a normal browser; for smoother video seeking serve the folder over http
(`python -m http.server --directory <archive root>`). For existing archives, make
`archive_browser.html` redirect to `archive_graph.html` so the lineage browser is the
default entry point.

## Personal Data Policy

Do not commit outputs from these tools unless they are small, sanitized fixtures
intentionally created for tests. `rest_posts.jsonl`, `archive_graph.js`, `Posters/`,
`ApiImages/`, and all media are archive/user data — keep them outside the repo.
