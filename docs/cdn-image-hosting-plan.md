# CDN image hosting plan (Bunny.net)

Written 2026-08-04 from planning chat. **Not implemented yet.** Goal: serve durable UI/result images from a cheap external host so browsers stop pulling every full-size PNG through the shared-site process, without AWS IAM complexity and without Cloudflare.

## Decision

**Use Bunny.net Storage + CDN.** Skip AWS S3+CloudFront for this job. Skip Cloudflare R2 (explicit no-Cloudflare). Skip Cloudinary/ImageKit (transform billing we do not need). DigitalOcean Spaces is a weaker second choice (more S3-shaped, weaker built-in hotlink UX).

Bunny fits because:

- Credentials are three or four strings from the dashboard (no IAM roles, bucket policies, CloudFront OAC, or signing keypairs).
- Built-in **token authentication** (expiring signed URLs) and **allowed-referrer** hotlink protection.
- Monthly **bandwidth / spend cap** (“overcharge protection”) so a leak cannot surprise-bill.
- Simple HTTP `PUT` upload API (`AccessKey` header = storage zone password).
- Cost ballpark: storage ~$0.01/GB/mo, CDN egress ~$0.01/GB (EU/NA), **$1/mo minimum**. Example: 50 GB stored + 200 GB served ≈ a few dollars/month.

## Architecture rule (do not violate)

**Disk remains source of truth.** This app’s shared-site resident model (`ImageDownloadBaseFolder`, `UiHistory/`, thumbs on disk, stream via path) stays. CDN is a *serve* path for browsers, not a replacement for local archive.

Recommended product shape (**option A**):

```
generate → save Raw to disk (unchanged)
         → upload same bytes to Bunny (after durable save succeeds)
         → job events / <img> / viewer use signed CDN URLs
         → /api/jobs/{id}/images/... remains fallback / local-only mode
```

Rejected for now:

- **C — CDN-only, drop disk:** redesigns archive + resident memory; out of scope.
- Option **B** (CDN only for share/export links; UI keeps `/api/jobs/...`) is acceptable if A is deferred; same credentials and adapter, narrower URL rewrite.

CLI/showcase can leave hosting off via a settings flag.

## What the owner hands the agent (Bunny checklist)

Register at https://bunny.net, add payment, then in the dashboard (~15–20 minutes):

1. Create a **Storage Zone** (e.g. `mic-images`).
2. Create a **Pull Zone** connected to that storage (hostname like `mic-images.b-cdn.net`).
3. Pull zone → **Security** → enable **Token Authentication**; copy the URL token key.
4. Same Security panel → **Allowed Referrers** = production UI hostname(s). Leave “block empty referrer” **off** initially (breaks some browsers / direct open); tighten later if desired.
5. Set a **monthly bandwidth / spend cap** (e.g. $10–20).
6. Paste into `settings.json` (never commit secrets):

```json
"BunnyStorageZone": "mic-images",
"BunnyStoragePassword": "...",
"BunnyCdnBaseUrl": "https://mic-images.b-cdn.net",
"BunnyTokenAuthKey": "...",
"BunnyUrlTtlSeconds": 86400,
"EnableBunnyImageHosting": true
```

| Setting | Bunny UI location | Used for |
|---------|-------------------|----------|
| `BunnyStorageZone` | Storage zone name | Object path prefix |
| `BunnyStoragePassword` | Storage → FTP & API Access (`AccessKey`) | Upload/delete only |
| `BunnyCdnBaseUrl` | Pull zone hostname | Public/signed URL base |
| `BunnyTokenAuthKey` | Pull zone → Security → Token Authentication | Signing `<img>` URLs |
| `BunnyUrlTtlSeconds` | App config | Expiry on signed URLs (e.g. 1–24h) |
| `EnableBunnyImageHosting` | App config | Feature flag |

Do **not** require the account master API key unless we need to create zones programmatically. Prefer one-time dashboard setup; the agent only needs the values above.

Also tell the agent which public hostname(s) belong on the allowlist (production UI + optional local test host).

## Abuse controls (no Cloudflare)

Layered; none requires AWS:

1. **Token auth (primary)** — Pull zone rejects unsigned requests. App signs URLs when emitting image refs (`?token=…&expires=…`). Stolen direct links die when the TTL expires.
2. **Allowed referrers (secondary)** — Only the UI host(s). Blocks casual embeds on other sites. Referrers alone are forgeable; treat as a cheap extra gate.
3. **Hard monthly bandwidth cap** — Bunny overcharge protection.
4. **Opaque object keys** — e.g. `ui/{jobId}/{gen}/{n}.png` (or content hash); no browsable listing.
5. **Storage private** — only the CDN pull zone serves bytes; storage password stays server-side.

This stops casual hotlinking and leaked links from burning bandwidth often. It is not DRM.

## Implementation sketch (when building)

- Settings fields as above + feature flag default **false** until credentials exist.
- Small uploader: HTTP `PUT` to `https://{region}.storage.bunnycdn.com/{zone}/{path}` with `AccessKey` header (no AWS SDK).
- Basic Bunny token signer when emitting `gen-result` / grid / input URLs (see Bunny docs: token authentication basic/advanced).
- Frontend already uses `evt.url` → `img.src`. Server should emit absolute CDN URLs so `apiUrl()` does not rewrite them.
- Thumbs: keep local `?thumb=1` initially (less upload work), or upload a thumb object later. Full-res / viewer / share → CDN.
- Videos under `Video/`: same dual-write later if bandwidth matters.
- Fail closed on upload if hosting is enabled and required for that path; do not silently leave events pointing at CDN objects that never landed. Local disk save remains the durable archive regardless.
- Preserve shared-site resident rules: do not retain full image bytes in process RAM for CDN; upload from disk path / stream.

## Why not AWS (if someone asks later)

“AWS’s best” for this is S3 + CloudFront. It works but needs: IAM user with scoped `PutObject`/`GetObject`, bucket policy, CloudFront distribution + OAC, and a CloudFront signing keypair (key-pair id + private PEM). That is the security surface this plan avoids. If forced later, hand the agent: Access Key Id, Secret Access Key, bucket, region, distribution domain, key-pair id, private key PEM — still dual-write to disk.

## Alternatives considered

| Option | Verdict |
|--------|---------|
| Bunny Storage + CDN | **Chosen** — simplest non-CF host with token + referrer + spend cap |
| AWS S3 + CloudFront | Works; IAM/signing complexity rejected for now |
| Cloudflare R2 | Best egress economics; rejected (no Cloudflare) |
| DigitalOcean Spaces | S3-ish; weaker hotlink story |
| Cloudinary / ImageKit | Transform metering; overkill for verbatim PNG/JPEG |
| Backblaze B2 alone | Cheap storage; egress hurts without a CDN (often Cloudflare) |

## Open when implementing

1. Confirm option **A** (dual-write + CDN in UI events) vs **B** (CDN only for share/export).
2. Owner completes Bunny checklist and drops the four secret strings into settings (or a non-git secrets file).
3. Allowlist hostnames for referrer protection.
4. Whether thumbs and MP4s upload in v1 or stay local-serve only.
