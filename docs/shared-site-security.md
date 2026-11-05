# Shared-Site Security & Malicious-Use Analysis

Written 2026-08-03, when the `--ui` web app gained the shared-site layer
(usernames, day archive, login gate, nginx kit in `deploy/`). Threat scenario
requested by the owner: **an attacker who has obtained a friend's
username/password (or their logged-in browser).** What can they actually do?

## What the attacker gets

A valid login is full app access. There are no roles: every authenticated
user can create jobs on every configured provider, see all history, and pull
every stored image. That is the product design (shared, no privacy), so a
compromised friend account = a hostile user with complete member powers.

## Damage they CAN do

1. **Spend your API credits** (acknowledged; you monitor this). Per-request
   image counts are bounded, production permits at most 14 aggregate provider
   executions, and the accepted-job queue is bounded at the default 64; total
   volume over time is not — there are **no per-user quotas or spend caps**.
   A script hammering `POST /api/jobs` burns money as fast as the provider
   lanes accept it.
2. **Abuse your consumer accounts — worse than credits.** `grok-web`,
   grok-web video ("Spicy" mode included), and `meta-web` run through *your*
   configured consumer sessions. `meta-web` is absent from production;
   grok-web image/edit/video use browser-free transports but still authenticate
   as your consumer account. A malicious user can generate ToS-violating
   content attributable to that account and risk an account ban.
3. **Fill the disk / exhaust memory (site takedown, not takeover).** Every
   upload and result is archived durably; retention is eternal. Full result
   bytes are not retained in process memory, caches and queues are bounded,
   systemd caps memory, and the 3 GiB free-space guard rejects new jobs before
   the filesystem is exhausted. Sustained flooding can still consume the
   remaining disk allowance, provider capacity, and bounded queue slots.
   nginx's 64 MB body cap and rate limits slow this; they do not stop a patient
   authenticated attacker.
4. **Pollute shared history.** Offensive prompts/images are visible to
   everyone and mixed into everyone's archive. Creator-only hiding removes
   prompts/images from app streams but does not delete underlying local/B2
   bytes; physical deletion still requires server/storage administration.
   Usernames are not authenticated identity — any member can create under any
   name (deliberate: attribution, not privacy).

## Damage they CANNOT do (and why)

- **Read your API keys or cookies.** No endpoint serves settings.json,
  ui-auth.json, or arbitrary files. Static serving is locked to `Ui/wwwroot`;
  image serving reads only paths the server itself recorded in `images.json`.
  `/api/config` reports key *problems* ("OpenAIApiKey is empty"), never values.
  The SQLite archive redacts keys/cookies by design.
- **Escape into the server via inputs (by construction).** Prompts and
  usernames are data end-to-end: no shell interpolation, no SQL string
  concatenation (archive writes are parameterized), no reflection/eval.
  Prompt-derived filenames pass `FilenameGenerator.SanitizeFilename`
  (`[a-zA-Z0-9_-]` only, ≤200 chars) — no path traversal. Usernames are
  server-validated to `[A-Za-z0-9 ._-]{1,32}`. The frontend renders prompts,
  names, and provider errors via `textContent`, so stored XSS has no sink.
- **Ride another member's browser (CSRF/session theft).** The auth cookie is
  HttpOnly (no JS access), SameSite=Lax (cross-site POSTs don't carry it),
  and Secure behind the nginx TLS terminator.
- **Brute-force the login quietly.** Per-IP throttle in the app (10 failures
  / 5 min) plus nginx `limit_req` on the login route; failures are logged
  with IP.

## Realistic paths to actual takeover (ranked)

1. **Native image-codec exploitation — the one to respect.** Uploads and
   provider results are decoded by Skia, a C++ graphics library, through
   SkiaSharp. A crafted file that exploits a fresh codec bug is the most
   plausible RCE route. Mitigations:
   - `Raster` decodes only five formats: PNG, JPEG, WebP, GIF, and BMP. It
     refuses every other format before any pixel work. The refusal covers
     Skia's own ICO, WBMP, HEIF, AVIF, and DNG codecs. Skia has no TIFF codec.
   - `Raster` reads the header first. It refuses an image larger than
     `Raster.MaxPixels` (134,217,728 pixels, 512 MiB at 4 bytes per pixel)
     before decoding. A small file thus cannot expand into a huge bitmap.
   - `SvgRaster` validates every SVG before Svg.Skia parses it (see below).
   - Keep SkiaSharp, its native assets, and Svg.Skia current. The managed and
     native SkiaSharp versions must match.
   - The hardened systemd unit in `deploy/` (`NoNewPrivileges`,
     `ProtectSystem=strict`, unprivileged user) confines successful code
     execution to a low-privilege, mostly read-only jail.

   **Image library replacement (owner decision, 2026-11-04).** The app no
   longer uses SixLabors.ImageSharp, ImageSharp.Drawing, SixLabors.Fonts, or
   Magick.NET. ImageSharp 3.2.0 fixed six advisories in 3.1.12. But 3.2.0
   requires a Six Labors license key in Release builds. The owner declined
   that outside dependency. Magick.NET wraps native ImageMagick, which has a
   long CVE history. The app used it only for SVG and format conversion.
   The replacements are MIT-licensed: SkiaSharp 4.154.0 with its Linux native
   assets, SkiaSharp.HarfBuzz for text shaping, and Svg.Skia 5.2.3 for SVG.
   Three classes in `MultiImageClient/Utils/` own all image work: `Raster`,
   `SvgRaster`, and `TextLayout` (fonts in `FontUtils`).

   - The six ImageSharp 3.1.12 advisories no longer apply: GHSA-v76p-62qx-wwq2,
     GHSA-j3p4-wp97-rph4, GHSA-j9gm-c75j-xc9q, GHSA-jjfr-hcj7-qf5w,
     GHSA-wmxv-xphr-5c9g, and GHSA-gwg2-r3hj-4w44. The 2026-11-03 `TiffGuard`
     workaround is deleted.
   - **SVG contract.** Recraft vector results are SVG. `SvgRaster` refuses a
     document larger than 64 MiB and any DTD. It also refuses processing
     instructions and a root other than `<svg>` in the SVG namespace. It
     refuses `script`, `foreignObject`, `image`, `feImage`, `iframe`,
     `object`, `embed`, `audio`, `video`, `handler`, and `listener` elements.
     It refuses `on*` attributes and `href` values that do not start with `#`.
     It refuses `@import` and `url(` references outside the document. Svg.Skia
     resolves no external entities, images, or elements. The SVG renders at
     its intrinsic size, at most 16,384 px per edge, on white. All 12 local
     Recraft vector SVGs pass and keep the sizes ImageMagick reported
     (checked 2026-11-04).

   **Image behavior changes (2026-11-04).**

   - **Displayed dimensions.** `Raster.Identify` and `Raster.Decode` apply the
     EXIF orientation. A JPEG rotated 90 or 270 degrees thus reports swapped
     dimensions, as browsers show it. ImageSharp reported the stored
     dimensions. All 121 local uploads that carry EXIF have orientation 1.
   - **Upload intake.** PNG, JPEG, and WebP uploads are stored unchanged.
     GIF and BMP uploads become PNG from their first frame. Every other
     format gets HTTP 400.
   - **Resizing.** Shrinking uses Lanczos3 widened by the shrink factor, so
     every source pixel contributes. Enlarging uses Skia's Mitchell cubic.
     Thumbnails and sheet cells decode JPEG and WebP at reduced scale, never
     below twice the target box. Five local images were scored against a
     Lanczos reference. The new thumbnails scored 0.06–0.25 dB closer than
     ImageSharp's. A 640 px thumbnail of a 3216x2144 PNG takes about 140 ms
     (ImageSharp: 221 ms).
   - **Thumbnail metadata.** Thumbnails carry no EXIF block. ImageSharp copied
     the source's EXIF block, including GPS coordinates, into each thumbnail.
     Thumbnails embed the source's color profile, or sRGB when it has none.
   - **Text.** HarfBuzz shapes each line. A character missing from the sheet
     font comes from any installed font that has it. Japanese text and emoji
     thus render where such fonts exist. ImageSharp drew them as boxes.
     Production has IPA Gothic, WenQuanYi Zen Hei, Noto Color Emoji, and
     Unifont (checked 2026-11-04). On 2026-11-05 the deployed build drew
     text under the Vibecoders service account and sandbox. Japanese came
     from WenQuanYi Zen Hei. Every emoji found a font with its glyph, but
     all drew in black and white: DejaVu Sans, or Unifont Upper in a blocky
     pixel style. The host's fontconfig ranks both above Noto Color Emoji.
     This PC draws some emoji, such as 🎨 and 🚀, from Noto Color Emoji in
     color, so the two machines' sheets differ. Line height now
     follows the font's line spacing. Text bands are thus slightly taller:
     contact sheets about 1%, describe sheets about 8%.
   - **No gray text.** The roundtrip "No Instructions" note is now black. The
     annotation metadata is now white on black.
   - **Memory.** Skia allocates each bitmap in native memory and frees it on
     dispose. It keeps no pool, so the ImageSharp allocator settings are gone.
     A loop decoded and resized 300 local images (885 megapixels). Resident
     memory stayed between 220 and 237 MiB.

   **Upload metadata (owner decision, 2026-11-04).** Uploaded PNG, JPEG, and
   WebP files stay unchanged, EXIF included. Members can open them, and
   image-capable targets receive the stored bytes. EXIF can hold GPS
   coordinates, the camera model, and the capture time. Counts on 2026-11-04:
   local, 5 of 313 uploads carry GPS coordinates; AVE, 6 of 289 carry GPS and
   19 name a camera; Vibecoders, none of 39 carry EXIF. GPS-tagged uploads
   reached OpenAI, Google, grok.com, Ideogram, and Recraft. The owner saw
   these counts and kept uploads unchanged. Do not add metadata removal
   without a new owner decision.
2. **Auth-file leak.** `ui-auth.json` holds PBKDF2 password hashes and the
   HMAC secret. A reader of the file can still mint cookies if they also
   capture a live `mic_auth` cookie, and they can attempt offline cracking of
   the hashes; they cannot read the original passphrases. It lives with
   settings.json (same sensitivity as your API keys), `chmod 600` or `0640`,
   never in git. Friends' passphrases should be unique to this site (they're
   pasted once, so hand out random strings). Version-1 plaintext files are
   rejected at startup.
3. **Secret-path leak.** The nginx path hides the site from scanners, not
   from anyone a friend shares the URL with. Browser history, pasted links,
   and screenshots leak it; the in-app `no-referrer` policy stops the Referer
   channel. Leaking it costs you layer 1 of 3 — rotate the path in the nginx
   config if it escapes.
4. **Kestrel/ASP.NET or nginx vulnerabilities.** Standard patching story;
   nothing app-specific. The app itself never listens publicly (loopback
   only). Both production instances use the shared .NET runtime in
   `/home/tparkour/.dotnet`; no other host service uses it. On 2026-11-04 it
   was still 10.0.10 from August 5. The four later releases (10.0.11 through
   10.0.14) list 59 CVEs, including ASP.NET Core remote code execution and
   denial of service. The owner approved the update to runtime 10.0.14 (SDK
   10.0.403) that day. Patch it after each monthly .NET release; see
   `deploy/README.md`. `agent-redeploy.sh` sets
   `DOTNET_CLI_TELEMETRY_OPTOUT=1` since 2026-11-04, because the SDK otherwise
   sends usage data to Microsoft. Before that, production builds ran without
   it.

## Deliberate non-mitigations (fail-closed elsewhere, open by design here)

- No per-user quotas, no delete/moderation API, no username authentication —
  the site is a high-trust shared studio for friends. The revocation lever
  (edit ui-auth.json → cookies die in ~1 s) is the moderation tool: pull an
  account first, clean the disk second.

## Owner's incident playbook

| Situation | Action |
|---|---|
| Friend's account misbehaving | Remove their account or replace their `passwordHash` in `ui-auth.json` (takes effect ≤1 s, no restart) |
| Everything on fire | Stop the systemd unit. `enabled: false` is rejected (fail closed), not an open switch; blank `UiAuthFilePath` is the only open mode |
| Secret path leaked | Change the path segment in nginx config, reload nginx, re-share |
| Suspect stolen HMAC secret / auth file | Rotate `secret` (logs everyone out), replace every `passwordHash`, check server for other compromise |
| Disk filled by flood | Delete offending `saves/<day>` + `UiHistory/<jobId>` folders; restart unit |
