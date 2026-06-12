# Grok Archive Restructure & Relationship Restoration — PRD

Status: implemented (first pass). The authoritative graph has been harvested and a
working lineage visualizer is in use. This document captures the requirements, the
confirmed data model, and the pipeline. See `tools/grok-export/README.md` for commands.

Implemented on the reference instance (`C:\grokArchive\WebExport`):
2,207 liked root posts harvested → 10,836 graph nodes (3,757 images, 7,079 videos),
5,231 authoritative edges (5,073 image→video, 112 extensions, 46 edits), 5,605 lineage
clusters; 7,029 local mp4s + 7,024 posters + all 3,757 source images present locally.

This document captures requirements and research for reconstructing
the relationship graph inside a Grok Imagine archive so the local HTML visualizer
can navigate the real creative lineage (image → video → extension, variants, etc.).

It is a reusable, repository-owned design doc. Archive instances remain user data and
stay outside the repo (see `docs/project-organization.md` and
`docs/grok-web-export-archive.md`).

## 1. Problem

We have a large local Grok Imagine archive at an archive root (instance:
`C:\grokArchive\WebExport`). It was produced by the GDPR-style `grok.com` data export
plus our own download tools under `tools/grok-export/`. Current instance scale:

- ~1,900 image manifest entries (`Images/` 811 files, `Uploads/` 82, `Unmapped/` 1,020).
- ~7,086 video rows in `videos.csv`; ~7,034 mp4 files downloaded (~15.7 GB).
- Each video post resolves to exactly one mp4 (post UUID == `share-videos/<uuid>.mp4`).

Grok Imagine content is **inherently a graph**, but the export we have is **flat**.
Real relationships include:

- **words → image** (text-to-image).
- **image → video** using a *mode/guide* (Normal, Fun, Custom, Spicy) with **no text prompt**.
- **image → video** with a **full text prompt** (and/or a preset template such as `I2I2V`).
- **video → longer video** ("extend"), continuing from the last frame, optionally with its
  own new prompt and a chosen extension duration.
- **one image → many video variants** (e.g. the same source image animated 10+ times).
- **one video → many extension variants**.

None of these parent/child links survive in our current flat data. The visualizer can
only show a time-ordered list plus heuristic guesses. We want to **restore the true graph**
and navigate it.

## 2. Goals / Non-Goals

### Goals

1. Define the Grok Imagine domain model (entities + edge types) precisely.
2. Identify every source from which relationships can be recovered, ranked by fidelity.
3. Specify a normalized graph data model (nodes + typed edges + provenance/confidence)
   that the visualizer consumes.
4. Specify the restoration pipeline (new/updated tools) that produces that graph.
5. Specify visualizer requirements for navigating the graph (root-image clusters,
   variant fans, extension timelines).
6. Preserve all existing downloaded bytes; never re-encode or destroy current assets.

### Non-Goals

- Re-generating any media.
- Building a server backend; the visualizer stays static local HTML/JS.
- Committing any archive instance data.
- Perfect reconstruction where the authoritative data no longer exists — heuristics are
  explicitly allowed and must be labeled as such.

## 3. Domain Model (Grok Imagine)

Confirmed from research (xAI video docs, grok internal REST behavior, community tooling)
and from inspecting our archive.

### Entities

- **Image post** — a generated or uploaded still. Has: id (UUID), prompt (may be empty
  for uploads), create time, pixels, source frame.
- **Video post** — an animated clip. Has: id (UUID), create time, prompt (may be empty
  when only a mode/guide was used), duration, poster/first frame, mp4.
- **Upload** — a user-supplied still used as a source (no generation prompt).

### Generation modes (xAI video API "request modes")

| Mode | Inputs | Meaning |
| --- | --- | --- |
| Text-to-video | prompt only | video from text alone |
| Image-to-video | prompt + image | image is the **starting frame** |
| Reference-to-video | prompt + reference_images | guided by reference image(s) |
| Edit-video | video + prompt | modify an existing video |
| Extend-video | video + prompt (+ duration) | continue from the **last frame**; output = original + extension stitched |

Image→video mode/guide presets in the consumer product: **Normal, Fun, Custom, Spicy**
(Spicy is the less-moderated mode). A video created with a guide and no typed prompt is
the common "empty prompt" case we see (2,360 of 7,086 rows). Preset *templates* observed
embedded in post pages carry a `templateType` such as `I2I` (image-to-image) and `I2I2V`
(image-to-image-to-video).

Extension facts (xAI docs + Replicate/3rd-party mirrors): source video 2–15s; extension
2–10s (default 6s); the result is the original footage **plus** the continuation stitched
into one mp4. This means an extension chain produces progressively longer clips that
share lineage and (usually) prompt.

### Edge types we must reconstruct

- `derivedFrom(video → image)` — image-to-video starting frame. Carries `mode/guide`,
  optional `prompt`, optional `templateType`.
- `extends(video → video)` — extension/continuation. Carries `extensionDuration`,
  optional new `prompt`, and ordering within a chain.
- `referenceOf(video → image[])` — reference-to-video guidance images.
- `editOf(video → video)` — edit-video.
- `variantOf(node → group)` — sibling variants sharing the same parent (image siblings or
  video siblings). Derived, not a stored edge.
- `promptGroup` — items sharing normalized prompt text (already implemented heuristically).

## 4. What We Have vs. What Is Lost

Surviving in the archive instance:

- `manifest.jsonl` — image assets (id, source, createTime, prompt, file, bytes).
- `videos.csv` — video posts (create_time, prompt, link → post UUID).
- `video_downloads.jsonl` / `video_failures.jsonl` / `video_download_events.jsonl` — download audit.
- `prompts.txt` — chronological prompt log (includes chat-image cards w/ model + moderated flag).
- `archive_index.js` + `archive_browser.html` — current generated index and static viewer.
- Downloaded media under `Images/`, `Uploads/`, `Unmapped/`, `Videos/`.
- Top-level `grok_ledger.jsonl` (our own MultiImageClient `GrokImagine` generations via `XAIGrokAPI`,
  source `log-backfill`) — separate provenance, not the web export graph.

Lost / never captured:

- `prod-grok-backend.json` (the raw export) has been deleted from the instance.
- Even when present, the GDPR export is **flat**: it does **not** contain `sourceImageId`,
  `parentPostId`, mode/guide, or duration. The current `videos.csv`/`manifest.jsonl` reflect that.
- The video downloader fetched each post page but discarded everything except the mp4 URL;
  the post HTML did **not** server-render parent/child data anyway (only the prompt via the
  `<meta name="description">` tag, the `og:image` poster, and the `og:video` mp4).

**Conclusion:** the authoritative graph is not in any file we currently hold. It must be
re-harvested from grok's authenticated API, or approximated with heuristics.

## 5. Sources of Truth for Relationships (ranked)

### S1 — Grok internal REST API (PRIMARY, authoritative) — CONFIRMED

Grok's own site loads media through an internal REST endpoint while logged in:

```
POST https://grok.com/rest/media/post/list
body: { "limit": 100, "filter": { "source": "MEDIA_POST_SOURCE_LIKED" }, "cursor": <ms> }
```

It is session-cookie authenticated (runs inside the logged-in browser; no API key).
`filter` is required; `MEDIA_POST_SOURCE_LIKED` is the one source that returns *your own*
posts (other source enums return a public/discover feed). Pagination is by the returned
`nextCursor` (a millisecond timestamp). Top-level results are your liked **images**; their
descendant videos are carried in nested `childPosts[]` (flattened to one level, each child
carrying its own `originalPostId`).

Confirmed per-post fields (verified against the reference account, not just docs):

- `id`, `userId`, `createTime`, `mediaType` (`MEDIA_POST_TYPE_IMAGE|VIDEO`)
- `prompt` / `originalPrompt`, `mediaUrl`, `hdMediaUrl`, `mimeType`, `resolution{width,height}`
- `childPosts[]` — descendants (videos, occasionally edited images)
- `originalPostId` — **immediate parent** (image for image→video; video for an extension)
- `originalRefType` — absent for direct image→video; `ORIGINAL_REF_TYPE_VIDEO_EXTENSION`;
  `ORIGINAL_REF_TYPE_IMAGE_EDIT`; `ORIGINAL_REF_TYPE_MULTI_REF_IMAGE_EDIT`
- `mode` — the guide: `normal`, `custom`, `extremely-spicy-or-crazy`, … (the "spicy/fun" family)
- `videoDuration`, `videoExtensionStartTime` (offset into parent for extensions)
- `thumbnailImageUrl` (first frame), `lastFrameThumbnailImageUrl` (last frame)
- `modelName` (`imagine_x_1`, `imagine-video-gen`, …), `moderated`, `rRated`,
  `inputMediaItems[]` (source image refs, tokenized URLs), `isRootUserUploaded`

Source-image bytes are downloadable while logged in: top-level image `mediaUrl`s are mostly
on the public `imagine-public.x.ai` CDN (fetchable server-side, no auth); the rest are on
`assets.grok.com/users/<uid>/<id>/content` (cookie-authenticated, no token needed).

Implementation: `harvest_relay.py` + a browser-side paginating loop write `rest_posts.jsonl`;
`build_graph.py` turns it into `archive_graph.js`. Related community references:
`ironsniper1/Grok-Imagine-Bulk-Favorites-Downloader`, `uucz/grok-imagine-downloader`.

Risks/constraints: requires an active logged-in session; subject to grok ToS and rate
limits (bulk authenticated bursts get throttled — keep concurrency modest, e.g. ≤6, with
retry); field names are unofficial and may drift; must run client-side in the user's
browser, relaying to a loopback receiver, not from this repo's servers.

### S2 — Poster / first-frame images (SECONDARY, available now)

Every video post exposes a poster frame at `https://grok.com/imagine/post/<id>/image`
(public; we saw `og:image` 832×1248). The first frame of an image-to-video clip is (close
to) the **source image**. This enables perceptual matching even without the API.

### S3 — Perceptual / structural heuristics (FALLBACK, available now, offline)

- **image → video**: perceptual-hash (pHash/dHash) the video's first frame against archived
  images; high similarity + video.createTime ≥ image.createTime ⇒ candidate `derivedFrom`.
- **video → extension**: an extension's first frame ≈ parent's first frame (because output is
  original + continuation), and the extension is longer in duration, later in time, and
  usually shares the prompt ⇒ candidate `extends`. Duration ladders (e.g. 6s → 12s → 18s)
  are a strong signal.
- **prompt grouping**: normalized-prompt equality (already implemented).
- **time + token Jaccard** scoring (already implemented in `build_archive_browser.py`).

Heuristic edges must be stored with `confidence` and `evidence`, never presented as
authoritative.

### S4 — Our own ledger (`grok_ledger.jsonl`)

Authoritative for media **we** generated through MultiImageClient's Grok path, with exact
prompt/model/timestamp/local path. Useful to cross-link archive items to our own runs, but
covers only our generations, not the full web account.

## 6. Target Restructured Data Model

Produce a normalized graph the visualizer can load. Proposed shape (one generated
`archive_graph.js` / `.json`, archive-owned, not committed):

```jsonc
{
  "generatedAt": "ISO",
  "basePath": "<archive root>",
  "nodes": [
    {
      "id": "uuid",
      "type": "image | video | upload",
      "createTime": "ISO",
      "t": 0,                       // unix ms
      "prompt": "",
      "mode": "normal|fun|custom|spicy|null",
      "templateType": "I2I|I2I2V|null",
      "durationSec": 0,             // videos
      "file": "Videos/....mp4",     // local relative, if downloaded
      "posterFile": "Posters/....jpg",
      "bytes": 0,
      "status": "downloaded|missing|failed|suspect",
      "provenance": "rest_api|export|heuristic|ledger"
    }
  ],
  "edges": [
    {
      "from": "childId",
      "to": "parentId",
      "kind": "derivedFrom|extends|referenceOf|editOf",
      "confidence": 1.0,            // 1.0 = authoritative API, <1 = heuristic
      "evidence": ["rest:childPosts", "phash:0.94", "duration:6->12", "time", "prompt-eq"]
    }
  ],
  "clusters": [
    { "rootId": "imageUuid", "memberIds": ["..."], "kind": "image-lineage" }
  ],
  "stats": { "...": 0 }
}
```

Rules:

- Nodes are immutable identity by UUID; merge data from all sources, recording `provenance`.
- Edges are additive; the same parent/child can have both an authoritative and a heuristic
  edge — keep the highest-confidence one for default navigation, expose the rest on demand.
- A **cluster** is a connected lineage rooted at the earliest source image (or upload).
  Clusters drive the visualizer's "show this whole creative thread" view.

## 7. Restoration Pipeline (tools)

All tools live under `tools/grok-export/`, take explicit `--archive-root`, write
archive-owned outputs, and never hardcode personal paths (per project conventions).

1. **`harvest_rest_api`** (S1, primary) — a userscript/CLI that, from a logged-in grok
   session, pages `rest/media/post/list` (and per-post detail), and writes
   `rest_posts.jsonl` with raw `childPosts`/`originalPost`/mode/duration. This is the
   highest-value new capability. Must be idempotent and resumable by cursor.
2. **`fetch_posters`** (S2) — download `…/post/<id>/image` poster frames into `Posters/`
   for every video (cheap, public, enables thumbnails + perceptual matching).
3. **`build_graph`** (S1+S3+S4) — merge `rest_posts.jsonl` (authoritative), `manifest.jsonl`,
   `videos.csv`, download logs, `grok_ledger.jsonl`, and heuristic matchers (pHash on
   posters/frames, duration ladders, prompt groups) into `archive_graph.js`. Supersedes the
   ad-hoc `sourceCandidates` logic in `build_archive_browser.py`.
4. **(optional) `extract_frames`** — sample first/last frames of each mp4 (ffmpeg) to power
   pHash-based `derivedFrom`/`extends` detection when the API is unavailable.

Existing `extract_export.py`, `download_videos.py`, and `build_archive_browser.py` are kept;
`build_graph` extends rather than replaces them, and should reuse their parsing helpers.

## 8. Visualizer Requirements

The static browser (`archive_browser.html`) should navigate the graph, not just a flat list:

- **Lineage cluster view**: select any node → show its whole cluster (root image, sibling
  image variants, the video fan-out per image, and each video's extension chain) as a
  navigable tree/graph.
- **Variant fan**: for a source image, show all child videos as a fan, labeled by mode/guide
  and prompt; indicate count ("10 videos from this image").
- **Extension timeline**: for a video, show its extension chain in order with durations
  (e.g. 6s → 12s → 18s) and per-step prompts.
- **Edge confidence affordance**: authoritative edges render solid; heuristic edges render
  dashed/with a confidence badge and their `evidence`.
- **Local-first media**: play `Videos/<file>.mp4` and show `Posters/...`; remote URLs and
  grok links are metadata only. Keep current behavior: hide suspect videos from normal
  browsing, one active `<video>`, remember volume/mute/speed in `localStorage`, hash URLs
  identify the selected local item.
- **Provenance filter**: filter by `provenance` (api / export / heuristic / ledger) and by
  `status` (downloaded / missing / failed / suspect).
- Follow the repo Visual & Typography policy (never gray text; size/semantic color for hierarchy).

## 9. Data Integrity & Personal-Data Policy

- Never resize/re-encode downloaded bytes; posters and extracted frames are new derived
  files, never overwrites.
- Treat `rest_posts.jsonl`, `archive_graph.js`, posters, frames, and all media as archive
  user data — keep outside git (extend `.gitignore`; these may contain personal NSFW prompts/IDs).
- Restoration must be **idempotent and auditable**: log run starts/completions and per-item
  decisions, mirroring the existing download event log.
- A video is only "acquired" when a non-empty local mp4 exists; keep the suspect-video rule
  (post id ≠ resolved mp4 id) and exclude suspects from default navigation.

## 10. Open Questions / Risks

- Does grok's `rest/media/post/list` expose a stable per-post **parent/source UUID** and
  **mode/duration** directly, or only via nested `childPosts`/`originalPost`? Needs a live
  authenticated probe to lock the exact schema before building `build_graph`'s authoritative path.
- Is there a per-post detail endpoint (e.g. `rest/media/post/<id>`) with richer lineage than
  the list endpoint?
- Rate limits / ToS for bulk REST harvesting of one's own account; run gently, resumable.
- For the portion of the archive where the account/API no longer returns a post (deleted,
  expired), heuristics (S2/S3) are the only option — acceptable with confidence labeling.
- Confirm extension semantics in practice: if Grok stores each extension as a **new full
  stitched mp4**, near-duplicate detection (size/first-frame) is needed to avoid double-counting.

## 11. Status & Remaining Follow-ups

Done: schema confirmed and frozen; full LIKED harvest (`rest_posts.jsonl`); normalized
`archive_graph.js`; first-frame posters; full source-image backfill (`ApiImages/`); and a
working 3-pane lineage visualizer (`archive_graph.html`) verified against the reference
instance.

Remaining / nice-to-have:

- **Coverage beyond "liked":** only `MEDIA_POST_SOURCE_LIKED` returns our own posts via this
  endpoint, so ~1,887 locally-downloaded videos that were never liked appear as orphan
  single-node clusters (no authoritative parent). Options: like-all then re-harvest, find a
  separate "my generations" endpoint, or fall back to the S3 perceptual heuristics for orphans.
- **HD media:** `hdMediaUrl` is recorded but we still play the public-share mp4s we already
  downloaded; optionally re-pull HD for favorites.
- **Last-frame chaining UI:** `lastFrameThumbnailImageUrl` is captured but not yet surfaced in
  the extension timeline.
- **Incremental re-harvest:** make the browser loop resumable by `nextCursor` and only fetch
  new posts since the last run.
