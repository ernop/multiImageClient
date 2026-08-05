# CDN image hosting plan (Bunny.net)

Written 2026-08-04 from planning chat; requirements re-derived with the owner
2026-08-05. **Not implemented yet.**

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
   library/archive must eventually live off-box. End state: everything under
   `saves/` lives in the CDN's storage. That is the separate, follow-on
   "storage fix" project; this plan's dual-write is its first stage, not the
   finished shape.
2. **Bandwidth is a non-goal for now.** The box's uplink is metered but the
   owner accepts current usage; no egress measurement required before v1.
3. **Remote shareability is acceptable** — image URLs that work outside the
   login are a feature, not a leak, per the owner (see access model below).

## Who is Bunny (counterparty, checked 2026-08-05)

bunny.net is operated by BunnyWay d.o.o., a Slovenian LLC (EU/GDPR
jurisdiction, Ljubljana). Founder-controlled: CEO Dejan Grofelnik Pelzel owns
~61%, Runa Capital Fund IV ~20% (sole institutional round, $5.88M Series A,
Oct 2022), co-founders Gregorčič and Dagarin most of the rest. Independent —
no parent company, no acquisition. Risk profile is "small independent vendor"
(could be acquired or fold), not "hyperscaler lock-in"; acceptable while local
disk remains the archive and objects are re-uploadable elsewhere.

## Decision

**Use Bunny.net Storage + CDN.** Skip AWS S3+CloudFront for this job. Skip Cloudflare R2 (explicit no-Cloudflare). Skip Cloudinary/ImageKit (transform billing we do not need). DigitalOcean Spaces is a weaker second choice (more S3-shaped, weaker built-in hotlink UX).

Bunny fits because:

- Credentials are three or four strings from the dashboard (no IAM roles, bucket policies, CloudFront OAC, or signing keypairs).
- Built-in **token authentication** (expiring signed URLs) and **allowed-referrer** hotlink protection.
- Monthly **bandwidth / spend cap** (“overcharge protection”) so a leak cannot surprise-bill.
- Simple HTTP `PUT` upload API (`AccessKey` header = storage zone password).
- Cost: storage $0.01/GB/mo (Standard HDD, 1 region), CDN egress $0.01/GB (EU/NA standard network), **$1/mo minimum**, no API/request fees. See the running-cost comparison below.

## Architecture: per-install storage mode (owner requirement, 2026-08-05)

Raw-byte residency is **per-install configuration**, not a single global rule:

- **Local/dev install:** local disk is fine as the durable home
  (`EnableBunnyImageHosting` may be off entirely, or on with local raws kept).
- **Production (alpha.fuseki.net):** local disk **may not** be the durable
  home of raw images — the box is 77 GB, 91% full, retention is eternal. With
  hosting enabled and local retention off, the local raw is deleted after the
  upload is verified and job finalization (thumbs, contact sheet) no longer
  needs it. On that install, **Bunny storage is the source of truth for raw
  bytes**; `job.json`/`events.jsonl`/`images.json`/thumbs stay local.

The pipeline (all steps in the UI job runner only; CLI/showcase untouched):

```
generate → save Raw to disk (unchanged)
         → upload same bytes to Bunny, verified (checksum)
         → job events / <img> / viewer use CDN URLs exclusively
         → retention=keep  (dev):  raw stays on disk as local archive
           retention=evict (prod): raw deleted after finalization
```

Consequences accepted with the eviction mode: the generation archive's disk
pointers for evicted files dangle (hashes remain valid); full-res local serve
returns 404 for evicted images (never linked — URLs are CDN); and the
upload-failure contract below MUST be hard-fail, since an unuploaded image
would otherwise exist nowhere the UI is willing to serve from.

Option **B** (CDN only for share/export links) is dead — it cannot relieve
disk. The old option-C framing is absorbed: eviction mode IS option C for the
production install, arriving in this project rather than a later one; the
remaining follow-on work is only the backfill of pre-existing history.

## What the owner hands the agent (Bunny checklist)

Register at https://bunny.net, add payment, then in the dashboard (~15–20 minutes):

1. Create a **Storage Zone** (e.g. `mic-images`). Main region: **NY**
   (nearest the DigitalOcean server that does the uploads). Standard tier, no
   extra replica regions in v1.
2. Create a **Pull Zone** connected to that storage (hostname like `mic-images.b-cdn.net`).
3. Set a **monthly bandwidth / spend cap** (e.g. $10–20). This is the primary
   abuse control under the open-URL posture — set it before production
   rollout, not after.
4. Only if posture 2 (signed URLs) is chosen: Pull zone → **Security** →
   enable **Token Authentication**; copy the URL token key. Skip Allowed
   Referrers either way — the app's `no-referrer` policy makes it decorative
   (see access model above).
5. **Prove it works before any coding** — from any machine, with the storage
   zone password from Storage → FTP & API Access:

```bash
PW='the-storage-zone-password'
ZONE=mic-images
EP=ny.storage.bunnycdn.com          # "Hostname" on the same FTP & API page
CDN=https://mic-images.b-cdn.net    # pull zone hostname
KEY="smoke/$(openssl rand -hex 16).png"

# upload any small PNG (expect HTTP 201)
curl -s -o /dev/null -w "PUT %{http_code}\n" -X PUT "https://$EP/$ZONE/$KEY" \
  -H "AccessKey: $PW" -H "Content-Type: image/png" --data-binary @test.png

# fetch through the CDN (expect 200 + content-type: image/png)
curl -sI "$CDN/$KEY" | head -5

# unguessability check: a wrong key must 404
curl -s -o /dev/null -w "wrong-key %{http_code}\n" "$CDN/smoke/$(openssl rand -hex 16).png"

# byte round-trip (expect identical hashes)
curl -s "$CDN/$KEY" | sha256sum; sha256sum test.png

# cleanup (expect 200; note the CDN edge may keep serving its cached copy
# until TTL/purge — that is the revocation caveat from the access model)
curl -s -o /dev/null -w "DELETE %{http_code}\n" -X DELETE "https://$EP/$ZONE/$KEY" -H "AccessKey: $PW"
```

6. Paste into `settings.json` (never commit secrets):

```json
"EnableBunnyImageHosting": true,
"BunnyStorageZone": "mic-images",
"BunnyStoragePassword": "...",
"BunnyStorageEndpoint": "ny.storage.bunnycdn.com",
"BunnyCdnBaseUrl": "https://mic-images.b-cdn.net",
"BunnyKeepLocalRawImages": true
```

(`BunnyKeepLocalRawImages`: `true` on dev installs — local raws kept as a
second archive; `false` on production — local raws evicted after verified
upload, because that box may not host the eternal archive.)

| Setting | Bunny UI location | Used for |
|---------|-------------------|----------|
| `EnableBunnyImageHosting` | App config | Feature flag; if true, all four strings below are required at startup |
| `BunnyStorageZone` | Storage zone name | Object path prefix |
| `BunnyStoragePassword` | Storage → FTP & API Access (`AccessKey`) | Upload/delete only, never serves |
| `BunnyStorageEndpoint` | Same FTP & API page ("Hostname") | Upload PUT target (region-specific) |
| `BunnyCdnBaseUrl` | Pull zone hostname | Emitted URL base |

Posture 2 would additionally need `BunnyTokenAuthKey` (Pull zone → Security →
Token Authentication) and `BunnyUrlTtlSeconds`; not part of the posture-1 v1.

Do **not** require the account master API key unless we need to create zones programmatically. Prefer one-time dashboard setup; the agent only needs the values above.

Also tell the agent which public hostname(s) belong on the allowlist (production UI + optional local test host).

## Access model: CDN URLs are bearer links (decided posture, 2026-08-05)

Today image bytes sit behind three layers (secret nginx path, login cookie,
loopback bind). Any CDN URL — signed or unsigned — is a **bearer link**:
whoever holds it reads the image, outside the login. This is a deliberate
access-model change, accepted by the owner: remote shareability is wanted,
the audience is a handful of trusted friends, and nobody is expected to crawl
or republish the links.

Two coherent postures; pick one at implementation and record it here:

1. **Fully open + opaque keys (owner's lean).** No token auth. Every object
   key embeds a per-image random secret (e.g.
   `ui/{jobId}/{gen}/{n}-{128-bit random}.png` minted at upload, stored in
   `images.json`), no listing enabled, no predictable component. Each URL is
   then a capability equivalent to the app's secret path: unguessable, but
   permanent once shared. Simplest signer-free implementation; links never
   expire (good for sharing, bad for revocation — revoking means deleting the
   object).
2. **Signed URLs (Bunny token auth).** TTL-limited bearer links; a leaked link
   dies at expiry. Costs a signer at every URL-emission site and breaks
   long-lived contexts (an archived page older than the TTL shows dead images
   until re-rendered). Only choose this if revocation-by-time matters more
   than share permanence — which the owner currently says it does not.

Either way the random-key rule is mandatory: keys derived only from
`jobId/gen/n` are enumerable if any listing or pattern leak occurs.

A third shape exists for the follow-on storage project only: keep bytes in
Bunny storage but serve them **through the app** (fetch+stream on
`/api/jobs/...`), preserving all three auth layers with no bearer links at
all. It relieves disk but not the metered uplink, and adds Bunny→box latency
per view; viable if the open-URL posture ever becomes uncomfortable.

**What referrer allowlisting does and does not buy (clarified 2026-08-05).**
A referrer allowlist genuinely blocks *naive* republishing: a blog embedding
`<img src="cdn-url">` sends `Referer: theblog.com` from every visitor's
browser, and Bunny rejects it. But this install must **allow empty referrers**
for two independent reasons: the app itself sends `no-referrer` on every
request (deliberate — protects the secret path), and directly shared links
(URL pasted into an address bar, chat apps, curl) arrive with no Referer at
all — and shareability is a stated requirement. Once empty is allowed, a
republisher defeats the allowlist with one attribute
(`<img referrerpolicy="no-referrer" ...>`), which makes all their visitors'
browsers send nothing. So: a speed bump against casual hotlinking, useless
against anyone who knows one HTML attribute, and "bearer link" remains the
correct description — possession of the URL suffices for direct access.
(The theoretical alternative — per-element `referrerpolicy="strict-origin"`
on our CDN images, sending host-only referrers so "block empty" could be ON —
would also break pasted/shared links, so it contradicts the shareability
requirement and is rejected.)

## Abuse controls (no Cloudflare)

1. **Hard monthly bandwidth cap (primary)** — Bunny overcharge protection; a
   leak costs at most the cap.
2. **Opaque random object keys** — capability URLs as above; no browsable
   listing.
3. **Storage private** — only the CDN pull zone serves bytes; the storage
   password stays server-side (upload/delete only).
4. **Token auth** — only if posture 2 is chosen.

This stops casual hotlinking and leaked links from burning bandwidth often. It is not DRM.

## Full implementation plan (written 2026-08-05, against current code)

Code-mapped hook points: results reach disk via `ImageManager.DoSaveAsync`,
the UI records them with `job.StoreImagePath` → `UiJobStorage.SaveImageReference`
(`images.json`, per-image `UiPersistedImage { Path, ContentType, ContentSha256 }`),
and all frontend URLs originate from a handful of sites in
`Implementation/UiJobs.cs` (`gen-result` images list, `grid`, `gen-partial`)
plus the input-library endpoint in `UiWorkflow.cs`.

### Stage 0 — config, client, no-code-path smoke command

- Settings (all default off/blank): `EnableBunnyImageHosting` (false),
  `BunnyStorageZone`, `BunnyStoragePassword`, `BunnyStorageEndpoint`
  (region host, e.g. `ny.storage.bunnycdn.com`), `BunnyCdnBaseUrl`, and
  `BunnyKeepLocalRawImages` (default **true**; production sets false to evict
  local raws after verified upload — the per-install storage mode).
  If the flag is on and any of the four strings is blank, startup hard-errors
  (fail closed, no partial configuration). `BunnyKeepLocalRawImages: false`
  with hosting disabled is also a startup hard error (eviction without an
  upload destination would discard data).
- `BunnyStorageClient` in `Implementation/`: static long-lived `HttpClient`
  with explicit timeout (the `GptImage2Generator`/`ImageSaving` pattern; the
  repo does not use `IHttpClientFactory`). `PUT https://{endpoint}/{zone}/{key}`
  with `AccessKey` header and Bunny's SHA-256 `Checksum` header so the server
  verifies the bytes (fail closed on mismatch); `DELETE` for cleanup. Uploads
  stream **from the durable disk path**, never from a retained heap buffer
  (shared-site resident rule). Non-2xx anywhere is an exception.
- `--bunny-smoke` CLI one-shot: upload a generated test object, fetch it back
  through `BunnyCdnBaseUrl`, byte-compare, delete, report pass/fail. This is
  the in-app twin of the owner's curl smoke test.

### Stage 1 — write + persistence (new UI results only)

- In `UiJobRunner.RunOneAsync`, immediately after the durable save +
  `StoreImagePath` succeed: upload that file to key
  `ui/{jobId}/{gen}/{n}-{128-bit random hex}.{ext}`
  (`RandomNumberGenerator`-sourced; the random segment is the capability —
  never emit a key derived only from jobId/gen/n).
- **Retry then hard-fail** (decision 1): 3 attempts with short backoff; on
  final failure the image result errors visibly. No local-URL substitution,
  ever.
- Persist `CdnKey` on `UiPersistedImage` in `images.json` (rehydration for
  archive days then comes free through the existing `TryLoad` path).
- Grid/contact-sheet object uploads the same way after composition.
- **Eviction (`BunnyKeepLocalRawImages: false`):** after the job's
  finalization completes (thumbs built, contact sheet composed and uploaded),
  delete the local raw files whose uploads were checksum-verified. Delete
  nothing on any failure path. Thumbs and job metadata always stay local.
- CLI/showcase/batch runs are untouched: the hook lives in the UI job runner
  only.

### Stage 2 — URL emission + frontend

- Event construction sites in `UiJobs.cs` emit the absolute CDN URL
  (`{BunnyCdnBaseUrl}/{CdnKey}`) for any image with a **verified** upload, and
  additionally a `thumbUrl` field carrying the local
  `/api/jobs/{id}/images/{gen}/{n}?thumb=1`. Cards must use `thumbUrl`:
  appending `?thumb=1` to a CDN URL would be silently ignored by Bunny and
  pull full-resolution originals into every card (the exact regression the
  card-image rule exists to prevent).
- `gen-partial` URLs stay local — streamed partials are ephemeral memory and
  are never uploaded.
- Frontend: `apiUrl()` currently prepends the page base unconditionally
  (`app.js` line ~10) and must pass through absolute `http(s)://` URLs
  untouched. Card code switches to `evt.thumbUrl`, falling back to
  `url + "?thumb=1"` only for pre-CDN local URLs. Viewer/anchors/video-source
  keep the main URL as today.
- Input images and the input library stay local-served in v1 (the compare
  viewer builds `api/jobs/{id}/images/input/0` client-side; changing that is
  not worth it until the storage project).
- When hosting is enabled, `gen-result` **never** carries a local full-res
  URL — CDN URL or visible failure (decision 1). Local full-res serving
  remains only for pre-CDN history.
- Events persisted before this feature carry local URLs and keep working
  unchanged; under the open-URL posture, newly persisted CDN URLs never
  expire, so archive replay needs no re-signing machinery (this is a major
  simplification that posture 1 buys).

### Stage 3 — verification before enabling in production

With real credentials on the dev box: run one cheap gpt2 low job; confirm the
`gen-result` URL is a CDN URL and renders; confirm `images.json` has the
`CdnKey`; confirm cards still hit the local thumb; corrupt the password and
confirm the decided upload-failure contract (below); confirm next-day archive
replay serves the CDN URLs.

### Stage 4 — production rollout

Add the four settings + flag to `/etc/multiimageclient/settings.json`,
redeploy. Set the Bunny spend cap before this step, not after.

### Stage 5 — separate follow-on (the storage project)

New images stop consuming production disk once eviction mode is on (Stage 1),
so the follow-on shrinks to: backfill pre-existing history into Bunny storage
and evict those local files, decide video handling, and add a purge-API call
for revocation-by-delete (a deleted object can otherwise persist in CDN cache
until TTL).

### Decisions (settled by owner, 2026-08-05)

1. **Upload-failure contract — DECIDED: retry, then hard-fail.** The upload
   is retried (3 attempts, short backoff) and on final failure the image
   result is a visible error. The local disk copy is retained as archive
   but is **never served to users as a substitute** — the owner explicitly
   rejected the local-URL fallback because it would silently mask upload
   failures and keep the disk-constrained install serving (and retaining)
   local bytes indefinitely. Consequence accepted: a Bunny outage makes
   generation results fail visibly while their bytes sit safely on disk;
   recovery of those images (re-upload + event repair) is manual/later, not
   an automatic fallback.
2. **Storage region — DECIDED: NY** (the server is a DigitalOcean box at
   146.190.x; uploads go server→Bunny). Add replica regions later only if
   remote friends report latency.
3. **v1 scope — DECIDED:** results + grid upload; inputs, thumbs, and videos
   stay local. Thumbs are ≤640px JPEG/PNG previews — small enough that local
   retention does not threaten the disk even on production.
4. **Access posture — DECIDED: posture 1** (fully open, opaque random keys).
5. **Per-install retention — DECIDED:** dev keeps local raws
   (`BunnyKeepLocalRawImages: true`), production evicts them after verified
   upload + finalization (`false`). See the architecture section.

## Why not AWS (if someone asks later)

“AWS’s best” for this is S3 + CloudFront. It works but needs: IAM user with scoped `PutObject`/`GetObject`, bucket policy, CloudFront distribution + OAC, and a CloudFront signing keypair (key-pair id + private PEM). That is the security surface this plan avoids. If forced later, hand the agent: Access Key Id, Secret Access Key, bucket, region, distribution domain, key-pair id, private key PEM — still dual-write to disk.

## Alternatives considered — running costs (verified 2026-08-05)

Rates (running costs only; setup complexity excluded at owner's request):

| Provider | Storage $/GB/mo | Egress $/GB | Request fees |
|----------|-----------------|-------------|--------------|
| Bunny Storage+CDN | 0.010 (HDD, 1 region) | 0.010 (EU/NA) | none |
| Backblaze B2 | 0.006 | free ≤3× stored, then 0.01 | free daily allowance |
| Cloudflare R2 | 0.015 (10 GB free) | free, always | $4.50/M writes, $0.36/M reads |
| AWS S3 + CloudFront | 0.023 | free ≤1 TB/mo (permanent free tier), then 0.085 | PUT $5/M, CF requests ~$1/M |
| DigitalOcean Spaces | $5/mo flat: 250 GB + 1 TB egress incl.; then 0.02 / 0.01 | included | none |
| Cloudinary / ImageKit | plan-based; past free tier jumps to ~$49–99/mo | — | transform-metered |

At this project's scale (storage dominates because retention is eternal;
egress is a handful of friends):

| Monthly bill | 100 GB stored, 20 GB served | 500 GB stored, 50 GB served |
|--------------|------------------------------|------------------------------|
| Backblaze B2 | ~$0.60 | ~$3.00 |
| Bunny | $1.20 | $5.50 |
| Cloudflare R2 | ~$1.35 | ~$7.35 |
| AWS S3+CloudFront | ~$2.40 | ~$11.60 |
| DO Spaces | $5.00 | ~$10.00 |
| Cloudinary/ImageKit | $0 → then $49+ | $49–99+ |

Reading: everything except the image platforms is single-digit dollars.
**Storage rate is the number that compounds** under eternal retention — B2
($0.006) beats Bunny ($0.01) by ~40%, worth ~$2/mo at 500 GB. Bunny stays
chosen because it bundles the CDN + hard spend cap + zero request fees in one
vendor with trivial credentials; B2 alone serves from its own hosts (no edge
cache, weaker abuse caps) and its cheap path to a CDN is Cloudflare, which is
excluded. AWS's egress is effectively free at this volume (permanent 1 TB
CloudFront free tier) — its real running cost is the 2.3× storage rate, and
the original objection (IAM/OAC/signing surface) is setup+operational, not
price. Revisit B2 as the *backfill archive tier* in Stage 5 if the historical
corpus grows into hundreds of GB: hot serving on Bunny + cold bulk on B2 is a
legitimate split, at the cost of a second vendor.

## Open when implementing

All product decisions are settled (see Decisions section). Remaining:

1. Owner completes the Bunny checklist + curl smoke test and drops the secret
   strings into settings (or a non-git secrets file). Coding starts after the
   smoke test passes.
2. Stage 5 backfill of pre-existing history (separate task; consider the B2
   cold-tier split from the cost section if the corpus is large).
