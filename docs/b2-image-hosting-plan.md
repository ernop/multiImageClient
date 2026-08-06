# Image hosting plan (Backblaze B2)

First written 2026-08-04 targeting Bunny.net; requirements re-derived with the
owner 2026-08-05 and the provider switched to **Backblaze B2** the same day
(owner decision). Implementation started 2026-08-05.

## Requirements (settled with owner, 2026-08-05)

The original stated goal ("stop pulling full-size PNGs through the process")
was checked against the deployed system and mostly did not survive:

- Cards/library already serve ≤640px thumbs with immutable caching; full-res
  flows only on explicit viewer/anchor/video/set-active actions.
- The HTTP/1.1 socket starvation that motivated "different origin" is fixed at
  its source: the production vhost now enables HTTP/2
  (`deploy/nginx-multiimageclient.conf`, applied 2026-08-05).
- The viewer's slow first image was a frontend scheduling defect (cold-open
  runway preloads competing with the visible image), fixed in `app.js` the
  same day — not a serving-infrastructure problem.
- RAM/CPU serving cost was already solved by the shared-site resident work.

What remains, per the owner:

1. **Primary goal — storage offload.** The production disk was measured at
   91% full (7.6 GB free of 77 GB) with the app's own 3 GiB job-rejection
   guard approaching. Retention is **eternal** — nothing gets pruned — so the
   library/archive must live off-box. This is exactly B2's product: object
   storage first, delivery second.
2. **Bandwidth is a non-goal.** The box's uplink is metered but the owner
   accepts current usage; B2's free egress allowance (3× monthly stored data)
   will never be exhausted by a handful of friends.
3. **Remote shareability is acceptable** — image URLs that work outside the
   login are a feature, not a leak (see access model below).

## Why B2 (provider decision, 2026-08-05)

Running costs verified 2026-08-05 (setup complexity excluded at owner's
request):

| Provider | Storage $/GB/mo | Egress $/GB | Request fees |
|----------|-----------------|-------------|--------------|
| **Backblaze B2 (chosen)** | 0.006 | free ≤3× stored, then 0.01 | free daily allowance |
| Bunny Storage+CDN | 0.010 (HDD, 1 region) | 0.010 (EU/NA) | none |
| Cloudflare R2 | 0.015 (10 GB free) | free, always | $4.50/M writes, $0.36/M reads |
| AWS S3 + CloudFront | 0.023 | free ≤1 TB/mo (permanent free tier), then 0.085 | PUT $5/M, CF ~$1/M |
| DigitalOcean Spaces | $5/mo flat: 250 GB + 1 TB egress incl.; then 0.02 / 0.01 | included | none |
| Cloudinary / ImageKit | plan-based; past free tier ~$49–99/mo | — | transform-metered |

At this project's scale (storage dominates because retention is eternal;
egress is a handful of friends), monthly bills:

| | 100 GB stored, 20 GB served | 500 GB stored, 50 GB served |
|---|---|---|
| Backblaze B2 | ~$0.60 | ~$3.00 |
| Bunny | $1.20 | $5.50 |
| Cloudflare R2 | ~$1.35 | ~$7.35 |
| AWS S3+CloudFront | ~$2.40 | ~$11.60 |

Decision rationale: the primary requirement is eternal archive storage, not
content delivery. B2 has the lowest storage rate ($0.006/GB/mo — the number
that compounds forever), effectively free egress at this audience size, no
monthly minimum (first 10 GB permanently free), account-level daily spend
caps ("Caps & Alerts"), and Backblaze is a NASDAQ-listed public company — a
steadier counterparty than the alternatives' private vendors. What B2 lacks
versus Bunny — an edge CDN — is worthless for a handful of trusted friends;
public buckets serve directly from `f00x.backblazeb2.com`. Cloudflare remains
excluded by owner policy; AWS loses on storage rate (2.3×Bunny's, ~4×B2's)
plus IAM/OAC/signing surface.

Bunny.net was the 2026-08-04 choice and remains the fallback if B2
disappoints: the architecture, access posture, failure contract, and eviction
design below are provider-agnostic; only the uploader client and URL bases
would change.

## Architecture: per-install storage mode (owner requirement, 2026-08-05)

Raw-byte residency is **per-install configuration**, not a single global rule:

- **Local/dev install:** local disk is fine as the durable home
  (`EnableB2ImageHosting` may be off entirely, or on with local raws kept).
- **Private MultiImageClient production site (hosted on `tpbeta`):** local disk
  **may not** be the durable home of raw images — the box is 77 GB, 91% full,
  retention is eternal. With
  hosting enabled and local retention off, the local raw is deleted after the
  upload is verified and job finalization (thumbs, contact sheet) no longer
  needs it. On that install, **B2 is the source of truth for raw bytes**;
  `job.json`/`events.jsonl`/`images.json`/thumbs stay local.

The pipeline (all steps in the UI job runner only; CLI/showcase untouched):

```
generate → save Raw to disk (unchanged)
         → upload same bytes to B2, verified (X-Bz-Content-Sha1, server-checked)
         → job events / <img> / viewer use B2 URLs exclusively
         → retention=keep  (dev):  raw stays on disk as local archive
           retention=evict (prod): raw deleted after finalization
```

Consequences accepted with the eviction mode: the generation archive's disk
pointers for evicted files dangle (hashes remain valid); full-res local serve
returns 404 for evicted images (never linked — URLs are B2); and the
upload-failure contract below MUST be hard-fail, since an unuploaded image
would otherwise exist nowhere the UI is willing to serve from.

## Access model: B2 URLs are bearer links (decided posture, 2026-08-05)

Today image bytes sit behind three layers (secret nginx path, login cookie,
loopback bind). Any hosted-object URL is a **bearer link**: whoever holds it
reads the image, outside the login. This is a deliberate access-model change,
accepted by the owner: remote shareability is wanted, the audience is a
handful of trusted friends, and nobody is expected to crawl or republish the
links.

**Decided: fully open + opaque random keys.** The bucket is `allPublic`;
every object key embeds a per-image random secret
(`ui/{jobId}/{gen}/{n}-{128-bit random hex}.{ext}`, minted at upload, stored
in `images.json`). Each URL is a capability equivalent to the app's secret
path: unguessable, but permanent once shared (revocation = delete the
object). Public B2 buckets allow download-by-name but **not listing**
(`b2_list_file_names` requires an auth token), so opaque keys hold. The
random-key rule is mandatory: keys derived only from `jobId/gen/n` would be
enumerable if any pattern leak occurs.

Referrer-allowlist hotlink protection (which Bunny offered) does not exist on
B2 and was analyzed as decorative anyway: this app deliberately sends
`no-referrer` (protecting the secret path), shared links arrive with no
Referer, and once empty referrers are allowed a republisher bypasses the
check with `<img referrerpolicy="no-referrer">`. Nothing of value was lost in
the switch.

A third shape exists for later if the open posture ever becomes
uncomfortable: keep bytes in B2 but serve them **through the app**
(fetch+stream on `/api/jobs/...`), preserving all three auth layers with no
bearer links at all, at the cost of app-uplink bandwidth and per-view
latency.

## Abuse controls

1. **Caps & Alerts (primary)** — B2 account-level daily caps on download
   bandwidth and transactions; a leaked link costs at most the cap.
2. **Opaque random object keys** — capability URLs as above; listing requires
   auth even on public buckets.
3. **Application key scoped to the one bucket** — the key in settings can
   read/write/delete only this bucket, and it never serves (downloads are
   anonymous via the public URL; the key is upload/delete only in practice).

This stops casual hotlinking and bounds leaked-link damage. It is not DRM.

## What the owner hands the agent (B2 checklist)

Register at https://www.backblaze.com/sign-up/cloud-storage, add payment,
then (~10–15 minutes):

1. Create a **bucket**: name must be globally unique across all of B2 (e.g.
   `mic-images-<something>`), **Files in bucket: Public**, default encryption
   off, object lock off.
2. Note the **bucketId** (bucket details page).
3. On the bucket, set **CORS rules** to "Share everything in this bucket
   with every origin" (downloads only). Required: the web UI's image viewer
   preloads full-resolution results with JavaScript `fetch()`, which the
   browser blocks cross-origin unless the bucket answers CORS preflights.
   `<img>` tags would work without it, but the viewer would break. (curl
   ignores CORS, so the smoke test below cannot verify this step — the
   Stage 3 browser check does.)
4. Create an **application key** restricted to that bucket (read/write —
   the default scoped capabilities include `writeFiles`/`deleteFiles`). Copy
   the `keyID` and `applicationKey` — the secret is shown exactly once.
5. **Caps & Alerts**: set daily caps (download bandwidth, transactions) as
   spend protection. Do this before production rollout, not after.
6. **Prove it works** — from any machine with `jq` installed (or skip the
   curl and run the in-app equivalent, `--b2-smoke`, after step 7):

```bash
KEY_ID='the-keyID'
APP_KEY='the-applicationKey'
BUCKET_ID='the-bucketId'
BUCKET='the-bucket-name'

AUTH=$(curl -s -u "$KEY_ID:$APP_KEY" https://api.backblazeb2.com/b2api/v3/b2_authorize_account)
TOKEN=$(echo "$AUTH" | jq -r .authorizationToken)
API=$(echo "$AUTH"   | jq -r .apiInfo.storageApi.apiUrl)
DL=$(echo "$AUTH"    | jq -r .apiInfo.storageApi.downloadUrl)
echo "download base (goes in settings as B2DownloadBaseUrl): $DL/file/$BUCKET"

UP=$(curl -s -H "Authorization: $TOKEN" -d "{\"bucketId\":\"$BUCKET_ID\"}" "$API/b2api/v3/b2_get_upload_url")
UPURL=$(echo "$UP" | jq -r .uploadUrl)
UPTOK=$(echo "$UP" | jq -r .authorizationToken)

KEY="smoke/$(openssl rand -hex 16).png"
SHA1=$(sha1sum test.png | awk '{print $1}')
RESP=$(curl -s -H "Authorization: $UPTOK" -H "X-Bz-File-Name: $KEY" \
  -H "Content-Type: image/png" -H "X-Bz-Content-Sha1: $SHA1" \
  --data-binary @test.png "$UPURL")
FILE_ID=$(echo "$RESP" | jq -r .fileId)
echo "uploaded fileId: $FILE_ID"

# anonymous fetch through the public bucket (expect 200 + image/png)
curl -sI "$DL/file/$BUCKET/$KEY" | head -5

# byte round-trip (expect identical hashes)
curl -s "$DL/file/$BUCKET/$KEY" | sha256sum; sha256sum test.png

# unguessability check: a wrong key must 404
curl -s -o /dev/null -w "wrong-key %{http_code}\n" "$DL/file/$BUCKET/smoke/$(openssl rand -hex 16).png"

# cleanup
curl -s -H "Authorization: $TOKEN" \
  -d "{\"fileName\":\"$KEY\",\"fileId\":\"$FILE_ID\"}" \
  "$API/b2api/v3/b2_delete_file_version" | jq .
```

7. Paste into `settings.json` (never commit secrets), then run
   `dotnet run --project MultiImageClient/MultiImageClient.csproj -- --b2-smoke`:

```json
"EnableB2ImageHosting": true,
"B2KeyId": "...",
"B2ApplicationKey": "...",
"B2BucketId": "...",
"B2BucketName": "mic-images-xxxx",
"B2DownloadBaseUrl": "https://f004.backblazeb2.com/file/mic-images-xxxx",
"B2KeepLocalRawImages": true
```

| Setting | Where it comes from | Used for |
|---------|---------------------|----------|
| `EnableB2ImageHosting` | App config | Feature flag; if true, all five strings below are required at startup |
| `B2KeyId` / `B2ApplicationKey` | Application key page (secret shown once) | Authorize + upload/delete; never serves |
| `B2BucketId` | Bucket details page | `b2_get_upload_url` target |
| `B2BucketName` | Bucket details page | Public URL path segment |
| `B2DownloadBaseUrl` | `{downloadUrl}/file/{bucket}` from the smoke test | Emitted URL base; client hard-errors if it disagrees with the live authorize response (persisted URLs must be right forever) |
| `B2KeepLocalRawImages` | App config | `true` dev (raws kept as second archive), `false` production (evict after verified upload) |

## Full implementation plan (2026-08-05, against current code)

Code-mapped hook points: results reach disk via `ImageManager.DoSaveAsync`,
the UI records them with `job.StoreImagePath` → `UiJobStorage.SaveImageReference`
(`images.json`, per-image `UiPersistedImage { Path, ContentType, ContentSha256 }`),
and all frontend URLs originate from a handful of sites in
`Implementation/UiJobs.cs` (`gen-result` images list, `grid`, `gen-partial`)
plus the input-library endpoint in `UiWorkflow.cs`.

Stages 0–2 were implemented on 2026-08-05 (build-verified; live verification
is Stage 3 and waits on the owner's B2 signup).

### Stage 0 — config, client, smoke command — IMPLEMENTED

- Settings as above. If the flag is on and any of the five strings is blank,
  startup hard-errors (fail closed, no partial configuration).
  `B2KeepLocalRawImages: false` with hosting disabled is also a startup hard
  error (eviction without an upload destination would discard data).
- `B2StorageClient` in `Implementation/`: static long-lived `HttpClient`
  with explicit timeout (the `GptImage2Generator`/`ImageSaving` pattern; the
  repo does not use `IHttpClientFactory`). Native B2 API v3:
  `b2_authorize_account` (Basic auth; token cached, refreshed on 401 or age),
  `b2_get_upload_url` per upload (or reused until it 50x's, per B2 docs),
  upload `POST` with `X-Bz-File-Name` (URL-encoded), `Content-Type`, and
  `X-Bz-Content-Sha1` computed from the file — the server verifies the hash,
  which is the fail-closed integrity check. Validates at authorize time that
  `B2DownloadBaseUrl == {downloadUrl}/file/{bucketName}`; mismatch is a hard
  error. Uploads stream **from the durable disk path**, never from a retained
  heap buffer (shared-site resident rule). Non-2xx anywhere is an exception.
- `--b2-smoke` CLI one-shot: upload a random test object, fetch it back
  anonymously through `B2DownloadBaseUrl`, byte-compare, verify a
  never-uploaded random key 404s, delete. In-app twin of the owner's curl
  smoke test.

### Stage 1 — write + persistence (new UI results only) — IMPLEMENTED

- In `UiJobRunner.RunOneAsync`, immediately after the durable save +
  `StoreImagePath` succeed: upload that file to key
  `ui/{jobId}/{gen}/{n}-{128-bit random hex}.{ext}`
  (`RandomNumberGenerator`-sourced; the random segment is the capability).
- **Retry then hard-fail** (decision 1): 3 attempts with short backoff
  (fresh upload URL on 50x per B2 protocol); on final failure the image
  result errors visibly. No local-URL substitution, ever.
- Persist `CdnKey` + `CdnFileId` on `UiPersistedImage` in `images.json`
  (recorded only after the checksum-verified upload; fileId kept for future
  deletion/purge; rehydration for archive days comes free through the
  existing load path).
- Grid/contact-sheet object uploads the same way after composition; a final
  grid-upload failure suppresses the grid link (logged) rather than emitting
  a local URL.
- An image that never reached disk (no durable saved file) cannot be hosted
  and hard-fails the result under hosting — the in-memory-bytes serving path
  is a non-hosting-only legacy branch.
- **Eviction (`B2KeepLocalRawImages: false`):** after the job's finalization
  (contact sheet composed and uploaded), `UiJob.EvictHostedLocalRaws` deletes
  local raw result/grid files whose uploads were checksum-verified —
  force-building each durable card thumb first, since thumbs are normally
  built lazily from the original. Inputs are never evicted; nothing is
  deleted on any failure path; the `images.json` record (path, hash, CdnKey)
  stays for provenance.
- CLI/showcase/batch runs are untouched: the hook lives in the UI job runner
  only.

### Stage 2 — URL emission + frontend — IMPLEMENTED

- `gen-result` `images[]` entries are absolute B2 URLs
  (`{B2DownloadBaseUrl}/{CdnKey}`) for verified uploads, plus a parallel
  index-aligned `thumbs[]` array of local
  `/api/jobs/{id}/images/{gen}/{n}?thumb=1` preview URLs (present only when
  hosting is on). Cards must use `thumbs`: appending `?thumb=1` to a B2 URL
  would be ignored and pull full-resolution originals into every card (the
  exact regression the card-image rule exists to prevent).
- `gen-partial` URLs stay local — streamed partials are ephemeral memory and
  are never uploaded. Video/SVG media results (`GeneratedMediaPath` branch)
  also stay local in v1.
- When hosting is enabled, `gen-result` **never** carries a local full-res
  raster URL — B2 URL or visible failure (decision 1). Local full-res
  serving remains only for pre-hosting history.
- Frontend: `apiUrl()` passes absolute `http(s)://` URLs through untouched
  (it used to prepend the proxy-prefix page base unconditionally). Card
  code uses `evt.thumbs[i]` when present, falling back to `url + "?thumb=1"`
  for pre-hosting local URLs. Viewer/anchors/video-source keep the main URL.
- The viewer preloads originals with `fetch()`, so cross-origin B2 downloads
  require the bucket CORS rule from the checklist (share with every origin).
- Input images and the input library stay local-served in v1 (the compare
  viewer builds `api/jobs/{id}/images/input/0` client-side).
- Events persisted before this feature carry local URLs and keep working
  unchanged; open-posture B2 URLs never expire, so archive replay needs no
  re-signing machinery.

### Stage 3 — verification before enabling in production

With real credentials on the dev box: run one cheap gpt2 low job; confirm the
`gen-result` URL is a B2 URL and renders; confirm `images.json` has the
`CdnKey`; confirm cards still hit the local thumb; corrupt the application
key and confirm retry→hard-fail; confirm next-day archive replay serves the
B2 URLs.

### Stage 4 — production rollout

Add the settings to `/etc/multiimageclient/settings.json` with
`B2KeepLocalRawImages: false`, redeploy. Caps & Alerts must already be set.

### Stage 5 — separate follow-on

New images stop consuming production disk once eviction mode is on, so the
follow-on shrinks to: backfill pre-existing history into B2 and evict those
local files, and decide video handling.

## Decisions (settled by owner, 2026-08-05)

1. **Upload-failure contract — DECIDED: retry, then hard-fail.** 3 attempts
   with short backoff; on final failure the image result is a visible error.
   The local disk copy is retained as archive but is **never served to users
   as a substitute** — the owner explicitly rejected the local-URL fallback
   because it would silently mask upload failures and keep the
   disk-constrained install serving (and retaining) local bytes indefinitely.
   Consequence accepted: a B2 outage makes generation results fail visibly
   while their bytes sit safely on disk; recovery of those images (re-upload
   + event repair) is manual/later, not an automatic fallback.
2. **Provider — DECIDED: Backblaze B2** (2026-08-05, superseding Bunny; see
   cost section).
3. **v1 scope — DECIDED:** results + grid upload; inputs, thumbs, and videos
   stay local. Thumbs are ≤640px previews — small enough that local retention
   does not threaten the disk even on production.
4. **Access posture — DECIDED:** fully open `allPublic` bucket, opaque random
   keys.
5. **Per-install retention — DECIDED:** dev keeps local raws
   (`B2KeepLocalRawImages: true`), production evicts them after verified
   upload + finalization (`false`).

## Open when implementing

1. Owner completes the B2 checklist + curl smoke test and drops the secret
   strings into settings (or a non-git secrets file). Production enablement
   waits on that; the code ships behind the default-off flag meanwhile.
2. Stage 5 backfill of pre-existing history (separate task).
