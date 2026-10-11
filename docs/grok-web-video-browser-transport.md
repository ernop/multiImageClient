# Grok Web Video Browser Transport

## Product goals

- Any successful image result can be sent to grok-web video generation from the local UI, regardless of which image provider produced it.
- The user must choose an overall video method: Normal, Funny, Custom, or Spicy.
- The motion prompt is optional for image-to-video. A source image plus a valid method is sufficient.
- Video aspect defaults to the source image, with explicit 1:1, 3:2, 2:3, 16:9, and 9:16 overrides available in the dialog.
- Every successful generated video has a **Redo Grok video** action. It reopens the same dialog with the prior source image, prompt, method, duration, and resolution so the user can edit and iterate quickly.
- Video players start at 50% volume and in exact 1:1 pixel display mode; users can switch back to fit-cell mode.
- Duration, resolution, SSE job updates, local MP4 saving, and the existing video result tile remain part of the normal UI job flow.
- Product decisions and protocol findings belong in Markdown documentation, not only chat transcripts or code comments.

## Live browser finding (2026-07-20)

A controlled test in a real logged-in grok.com browser succeeded:

1. Select a saved image.
2. Choose **Make video** with an empty motion prompt.
3. The web app sends `POST /rest/app-chat/conversations/new`.
4. The request uses `modelName: "imagine-video-gen"`, the source asset in `fileAttachments`, a `videoGenModelConfig`, and an explicit message suffix such as `--mode=normal`.
5. The response is newline-delimited JSON. `streamingVideoGenerationResponse.progress` advances to 100, then returns a relative `videoUrl` ending in `generated_video.mp4`.

The same endpoint returns HTTP 403 ("Request rejected by anti-bot rules") from a standalone `HttpClient`, even with copied fetch-metadata headers and the same cookie export. A direct `fetch` evaluated inside a logged-in Playwright page also returns 403.

The missing condition is Grok's own request initiation path. The web app adds a dynamic `x-statsig-id` integrity header when its real **Make Video** action creates the request. Merely copying cookies or running arbitrary JavaScript in Chromium does not produce that header. On a post-detail page, the working sequence is **Make Video → Quick Animate**; the latter initiates the signed app-chat request.

## Browser-free app-chat signing (2026-08-05/06)

`x-statsig-id` is reproducible without a browser. Grok's frontend uses the
same client-transaction construction as X: a public 48-byte
`grok-site-verification` value, a deployment-specific animation key, a
seconds-since-2023-05-01 counter, SHA-256, a random salt byte, XOR, and
unpadded ordinary base64. `GrokWebStatsigSigner` implements the complete
derivation in C# and validates against fixed independent vectors.

The one-shot `--grok-web-capture-statsig --input-image <path>` operation:

1. uploads the image and creates a Grok media post;
2. opens that post once in Playwright;
3. hooks the signing digest input and clicks the real Edit control;
4. captures `x-statsig-id` but aborts the edit request before it reaches Grok;
5. requires the C# signer to reproduce the captured header byte-for-byte; and
6. writes the verified public deployment pair to
   `GrokWebStatsigVerificationKey` / `GrokWebStatsigAnimationKey` in the exact
   loaded settings file. Since 2026-10-07 this step runs only with
   `--grok-web-statsig-pin`; see
   [Automatic page signing keys](#automatic-page-signing-keys-2026-10-07).

With signing available, image edit and video send a directly signed
`POST /rest/app-chat/conversations/new` with no browser. The token inputs are
the HTTP method and origin-relative path, not the app-chat payload, so the
same material covers `imagine-image-edit` and `imagine-video-gen`. A live
local test on 2026-08-05 completed one `imagine-image-edit` request in about
50 seconds and returned one 1328x784 image. The .NET HTTP transport was
accepted, so this route did not require Chrome TLS impersonation or browser
header ordering in that test.

Capture values are deployment-specific. Missing, incomplete, malformed, or
stale values are hard failures for signed edit/video; they are never replaced
with guessed values. Text-to-image remains available through its separate
WebSocket when signing is unavailable. Updated 2026-10-07: the UI reports
grok-web as image-capable and video-capable when the cookie file exists and
signing is available. Page keys are the default. Only an incomplete or
malformed pinned pair makes signing unavailable.

Browser-free video was enabled on 2026-08-06 because it has the exact same
signed request identity (`POST /rest/app-chat/conversations/new`) as the
live-verified edit route. Its first browser-free video test passed in production on 2026-09-09;
an HTTP rejection or missing exact-post result remains a visible hard failure.

## Production signing refresh and video verification (2026-09-09)

Production already exposed the video action, but its saved signing pair returned HTTP 403 during a video test.
The source-image upload succeeded with the existing production cookie file.
Updating only the two signing settings with the locally verified pair resolved the rejection.
The settings update preserved credentials, permissions, and a backup of the previous configuration.

A browser-free production test generated one six-second, 480p, square video using the Normal method.
The test used a plain blue image and requested slowly moving white clouds.
The application saved a 595,470-byte MP4 with valid file magic and reported one successful result.
This supersedes the earlier pending browser-free video verification status.

After active-work checks, only `multiimageclient-ui.service` was restarted.
The existing activation socket remained active.
Both loopback and the existing private public route reported video available without an error.
The local server was also restarted and passed its health and video-availability checks.
No additional environment or neighboring service was changed.

## Expired signing pair: "This page is out of date" (2026-10-07)

App-chat returns HTTP 403 with this body when grok.com no longer accepts the signing key:
`{"error":{"code":7,"message":"This page is out of date. Reload to continue.","details":[]}}`.
Reloading the MultiImageClient page does not help.
The cookie file is not the cause: upload and media-post creation still succeed before app-chat.

Evidence:

- Controlled comparison on 2026-10-07: the same workstation, cookie file, binary, source image, and prompt were used.
  The previous pair returned this 403. A newly captured pair returned the requested edit.
- The first production pair was captured between 05:53 and 06:12 UTC on 2026-08-06.
  It last succeeded at 6.9 days and failed by 7.0 days.
  grok.com then returned "Request rejected by anti-bot rules." instead of the current message.
- The 2026-08-13 production pair last succeeded 5.1 days after installation and failed by 7.1 days.
- The 2026-09-09 production pair succeeded at 7.0 days and failed by 10.8 days.
- All three intervals fit a lifetime of about seven days.
  The first pair failed by 7.01 days, but the third still worked at 7.03 days.
  Expiry is therefore not one exact age counted from capture. The mechanism remains unknown.
- Unrefreshed production edits failed from 2026-08-20 to 2026-09-09 and from 2026-09-20 to 2026-10-07.
- No public source documents this lifetime or the "out of date" message.

The first response on 2026-10-07 was a manual recapture.
A newly captured pair passed a local edit and a production-host edit.
Production changed only its two signing values.
Only `multiimageclient-ui.service` restarted, after running jobs finished.
The automatic page keys below supersede that manual procedure.

## Automatic page signing keys (2026-10-07)

Owner request, 2026-10-07: fix the expiry durably and add useful logging.
Research the mechanism online before choosing a design.

### How grok.com builds `x-statsig-id`

`x-statsig-id` is the integrity header that grok.com's own code adds to app-chat requests.
It is 70 bytes of unpadded base64.
The first byte is a random salt.
Each later byte is XORed with that salt.
The XORed part holds, in order:

1. the 48-byte verification key;
2. a 4-byte little-endian counter of seconds since 1682924400 (2023-05-01 07:00 UTC);
3. the first 16 bytes of SHA-256 over `METHOD!path!counter`, the text `obfiowerehiring`, and the animation key;
4. the byte 3.

Each grok.com page load mints a new verification key in a meta tag.
The tag name is `grok-site―verification`.
It uses U+2015 (horizontal bar) instead of a hyphen.
A search for the hyphenated name misses the tag.
For that reason, an earlier note wrongly said the key was absent from grok.com HTML.

The animation key comes from a CSS animation that Chromium evaluates:

- The page embeds a public frame table of four sets with 16 rows each.
  Each row holds a start color, an end color, a rotation, and four cubic-bezier values.
- One key byte selects the frame set, modulo the set count.
- One key byte, modulo 16, selects the row.
- Three key bytes, each modulo 16, multiply to a frame time in milliseconds.
  JavaScript `Math.round` rounds that time to tens, with halves up.
- Chromium samples the animation at that time.
  The key text is the eased color plus the rotation matrix, both in hexadecimal.
- The signer script chunk holds the byte positions.
  Each grok.com deployment can change them.

X (Twitter) uses the same scheme for its `x-client-transaction-id` header.
Public implementations read the same ingredients:
[XClientTransaction](https://github.com/iSarabjitDhiman/XClientTransaction),
[x-client-transaction-id](https://github.com/Lqm1/x-client-transaction-id), and
[the Agentic-X transaction-ID notes](https://github.com/tjdwls101010/Agentic-X/blob/main/docs/wiki/Transaction-ID.md).
The Agentic-X notes fetch the page once per session.
For grok.com, [Grok-Api](https://github.com/realasfngl/Grok-Api) parses the meta tag and the animation frames.
[grok2api pull request 903](https://github.com/chenyme/grok2api/pull/903), merged 2026-08-13, fetches the meta tag from the page.
[grok-web-api](https://github.com/imjustprism/grok-web-api) pins one captured header.
It asks users to recapture after each Grok build, which is the failure mode of a pinned pair.

### Decision

Fetch signing keys from the grok.com page and derive each animation key in C#.
No browser runs.

Rejected alternatives:

- Weekly manual recapture. It failed unnoticed twice, for 20 days and for 17 days.
- Scheduled Playwright capture on the production host.
  Chromium needs hundreds of MiB on the shared 4 GiB host.
  The resident-memory rule keeps Chromium out of `--ui`.
- Automatic replay of a rejected request with a new key.
  A rejected request fails visibly instead; the next request uses a new key.

### Behavior

- `GrokWebStatsigSource` (`MultiImageClient/GrokWebStatsigSource.cs`) supplies signers.
  The UI server, CLI grok-web modes, and the chat probe share it.
- Page keys are the default.
  They apply when `GrokWebStatsigVerificationKey` and `GrokWebStatsigAnimationKey` are both blank.
- The first signed request fetches `https://grok.com/` with the cookie file.
  The source reads the meta key, the frame table, and the script list.
- Discovery follows the page's Turbopack scripts.
  The chunk that exports `botoxSign` loads one module.
  The loader map names the chunk of that module, the signer chunk.
  The signer chunk gives the four key-byte positions, the frame-set selector, and the counter epoch.
- Script chunks download from `cdn.grok.com` without cookies.
  Chunk names are content hashes.
  The source keeps parsed facts for the current page's scripts only.
  A refresh within one deployment downloads no scripts.
- One key signs every request until it is 60 min old.
  grok.com rejection or a changed cookie file also replaces it.
- Concurrent requests share one refresh.
- Each download has a 30 s deadline.
  The page may be at most 8 MiB and each script at most 16 MiB.
  Up to four scripts download in parallel.
- Each parser requires exactly one unambiguous match.
  A changed page or chunk layout fails with "grok.com page format changed: ...".
  The source never guesses a signing input.
- The frame-set count in the page must equal the selector modulus in the signer chunk.
- A refresh failure fails that request.
  For the next 30 s, requests fail at once with the same error.
  After that, the next request retries.
- A rejection marks the key as rejected.
  "This page is out of date" marks any key.
  "Rejected by anti-bot rules" marks a page key, because a derivation defect can cause it.
  The job fails with the error, and the next signed request fetches a new key.
  The source never replays the rejected request.

### Pinned pairs (diagnostic only)

- `--grok-web-capture-statsig --grok-web-statsig-pin` writes a captured pair to the loaded settings file.
  It also writes `GrokWebStatsigCapturedAtUtc`.
- A pinned pair replaces page keys.
  grok.com stops accepting it after about seven days.
- After a rejection, a pinned pair fails at once.
  Clear it, or pin a new pair, then restart.
- An incomplete or malformed pinned pair disables signed edit, the chat image path, and video.
  So does a capture time without a pair.
  The error names the setting.

### Logging

Every line starts with `Grok web signing`.

- Startup states the mode.
  In page mode it states the page address and the three replacement rules.
  In pinned mode it states the capture time and age of the pair.
- Each key fetch logs the key fingerprint, the duration, and the reason.
  Reasons are first request, age, rejection, and cookie change.
  The line also names the signer chunk and its byte positions.
  It gives the key's frame set, row, and frame time, and any script downloads.
  A new signer chunk adds "Signer chunk changed from <name>".
- Each refresh failure logs the duration, the reason, and the exact HTTP or parser error.
- Each rejection logs the operation and the next step.
  It also logs the key's origin, fingerprint, and age.
- The generation archive records `statsig: {origin, key, ageSeconds}` with each app-chat request.

A fingerprint is the first 4 bytes of the key's SHA-256, in hexadecimal.
Logs never contain keys.

These lines answer three questions:

- A rejection at a short key age indicates a derivation or anti-bot problem, not expiry.
- A changed signer chunk dates a grok.com deployment.
- The frame set, row, and frame time let an operator reproduce a rejected derivation offline.

### Derivation fidelity

- First build of 2026-10-07: signer chunk `2q93j7gpfz705.js`.
  It read row byte 11, frame-time bytes 11, 45, and 1, and set byte 5.
  The C# derivation matched Chromium for 38 of 38 logged-in page loads.
  Those loads made 550 signed requests, with one animation key per load.
- That work fixed three defects in the earlier C# model.
  It used the raw frame-time product.
  It rounded colors half to even.
  It solved the bezier curve to only 1e-5.
- Later that day grok.com deployed signer chunk `05bqa45_66jbs.js`.
  It reads row byte 5, frame-time bytes 15, 8, and 2, and set byte 5.
  Discovery found the new positions with no code change.
- On that build, 23 of 25 browser page loads matched at first.
  Both misses used frame set 0, row 12, at 60 ms and 240 ms.
  That row's easing curve dips below 0, so red and green rose above 255.
  Chromium clamps each channel to 0–255.
  The C# model clamped only at 0.
  With the upper clamp, all 25 loads matched.
- 39 of the 64 rows dip below 0 within the reachable frame times, 0–3,380 ms.
  A channel above 255 also needs a bright start color.
  None of the first build's 38 samples hit that case.
- Unit tests hold browser vectors from both builds, including both clamp cases.
  `--grok-web-statsig-self-test` also checks both clamp cases.
- It is unknown whether grok.com checks the animation part of the token.
  A live page-key edit succeeded, but its frame time was 0.
  At frame time 0 the frame-time bytes and the easing curve do not affect the key.

### Capture tool

- `--grok-web-capture-statsig --input-image <path>` verifies only and changes no settings by default.
  It first checks that the C# signer reproduces the browser's header.
  It then derives the animation key for the browser's verification key from a fresh page fetch.
  That key must equal the browser's animation key.
  Any failure prints a FAIL line and exits with code 1.
- Each run uploads the image and creates one Grok post on the account.
- On 2026-10-07 grok.com showed an announcement dialog ("Your Bots are in Grok") over the post page.
  A consent banner also covered it.
  The dialog can open after the page loads, while a click waits.
- The browser client registers Playwright locator handlers for both overlays.
  The handlers close a dialog with its Close button.
  They dismiss the banner with Reject All or its dismiss button.
  The client also closes both before each forced click, because forced clicks skip handlers.
  It never accepts optional cookies.
  A dialog without a Close button fails with the dialog's text.

### Verification (2026-10-07)

- `MultiImageClient.Tests/GrokWebStatsigTests.cs` covers derivation vectors, parser failures, and key reuse and replacement.
  It also covers rejection, cookie change, concurrency, cooldown, pinned mode, and rejection classification.
- A live CLI edit with a page key fetched its key in 1,391 ms.
  That fetch downloaded 85 chunks (10,332 KB) to find the signer.
  The edit finished in 10.9 s with the requested change.
  The archive recorded `origin: page`.
- After the clamp and overlay fixes, the capture tool reported PASS.

### Verification (2026-10-11)

- grok.com had deployed signer chunk `13x5-4zevaqjo.js`.
  It reads row byte 9, frame-time bytes 12, 34, and 12, and set byte 5.
  Discovery found these positions with no code change.
- A live CLI edit fetched its page key in 1,579 ms.
  That fetch downloaded 92 chunks (10,498 KB) to find the signer.
  The edit finished in 10.9 s and turned a red circle into a blue square.
- That key used frame time 700 ms, so the easing curve affected it.
  grok.com accepted it.
  This still does not show whether grok.com checks the animation part.
- The production host fetched `https://grok.com/` without a cookie.
  It received HTTP 200 with no Cloudflare challenge.
  The page held the verification meta tag, the frame table, and 92 script tags.

### Operations

- Leave both signing keys blank in local and production settings.
- Releasing this change removes the pinned keys from both production settings files.
  Neither file stored a capture time.
- `deploy/create-environment.py` no longer copies signing keys into new environments.
- The Vibecoders environment has no grok-web cookie file, so it never signs.

## Implemented transport split

- Text-to-image generation continues to use `wss://grok.com/ws/imagine/listen`.
- Image editing does **not** use that WebSocket. `properties.image_uri` is accepted but ignored by the consumer transport (observed 2026-07-31: outputs invented from the prompt alone). Live edit uses `POST /rest/app-chat/conversations/new` with `modelName: "imagine-image-edit"` and `mediaGenInput.imageToImage.inputAssets: [assetId]`. With signing available (page keys by default since 2026-10-07) this is direct browser-free HTTP; otherwise CLI edit retains the real-control Playwright transport.
- Image upload, asset lookup, media-post creation, polling, and downloads continue to use `GrokWebClient` HTTP calls.
- Video generation uses the same direct signed app-chat HTTP request when signing is available. The UI has no Playwright video fallback: a missing cookie file or an unusable pinned pair makes video unavailable. Explicit CLI video modes retain the prior real-control Playwright path only when signing is unavailable.
- The app first uploads the source and creates its normal Grok image post, then submits the selected method, optional motion prompt, duration, resolution, and aspect ratio through app-chat.
- CLI Playwright fallback operations are serialized and idle-release their Chromium process.
- Relative `streamingVideoGenerationResponse.videoUrl` values are normalized to `https://assets.grok.com/...` before download.
- HTTP 200 and a model message saying that a video was generated are not success. The job succeeds only after an MP4 URL appears for the exact source post and the downloaded bytes have MP4 file magic.

## Silent rejection evidence (2026-07-20)

Archived failures show that app-chat can return HTTP 200, a request trace id, and a model message claiming success while the exact source post never receives a child video. Repeated media-post responses exposed neither a video nor an explicit terminal moderation flag. This occurred on prompts likely to trigger moderation; the evidence does not show a gpt-image-2-specific file incompatibility.

The client now treats this as a bounded provider failure instead of leaving the UI/archive attempt running:

- app-chat error, stream-error, blocked, rejected, moderated, failed, and cancelled fields are detected recursively when Grok supplies them;
- an app-chat body left open after its headers no longer blocks for the full browser timeout;
- media polling matches the exact source post id, never a potentially stale prompt match;
- no video after `GrokWebVideoPollTimeoutSeconds` fails with a silent rejection/stall explanation;
- uploaded image type comes from file magic rather than its extension;
- HTTP 200 media downloads are rejected unless their bytes are actually MP4;
- video jobs persist and log source job/generator/index provenance, including whether the source was `gpt2`.

## End-to-end validation (2026-07-20)

The local UI completed an image-to-video job through the browser-backed transport with:

- empty motion prompt;
- `normal` method;
- 16:9, 480p, 6 seconds;
- successful MP4 download and normal SSE `gen-result`;
- 2.8 MB saved video in about 20 seconds.

This validates the required-method/optional-prompt product contract and the integrity-preserving UI-trigger approach. A future grok.com control rename can still break the unofficial browser automation; failure diagnostics record the visible controls and a screenshot without cookie values.

## Aspect-ratio evidence (2026-07-20)

The generation archive and raw media show that explicit ratios are honored:

- grok-web image 1:1 request → 960×960 raw JPEG;
- grok-web image 3:2 request → 1152×768 raw JPEG;
- grok-web video 1:1 request → 544×544 MP4;
- grok-web video 3:2 request → 672×448 MP4;
- grok-web video 2:3 request → 448×672 MP4.

Repeated 2:3 output came from `auto` image jobs, where grok-web's consumer transport uses 2:3 as its native default, and from video jobs inheriting 2:3 source images. `auto` does not infer an aspect ratio from prompt wording. The UI now labels this behavior directly and the video dialog exposes an explicit aspect override.

## Local signing setup repair (2026-09-09)

The local WSL server lacked both signing settings, so its viewer hid the video action.
The existing local cookie file remained valid.
Capture now dismisses Grok's cookie notice immediately before clicking Edit.
The click uses normal obstruction checks; a forced click previously hit the notice instead.
Superseded 2026-10-07: locator handlers close dialogs and the banner; see [Capture tool](#capture-tool).
A capture timeout saves a uniquely named local screenshot and reports its path.
Capture still aborts the generation request and requires exact header reproduction before saving either setting.

Local capture passed after installing Chromium's missing Ubuntu runtime libraries:
`libnspr4`, `libnss3`, `libatk1.0-0`, `libatk-bridge2.0-0`,
`libxcomposite1`, `libxdamage1`, and `libatspi2.0-0`.
The verified pair was saved in the existing untracked local settings file.
After the local restart, `/healthz` returned `ok` and `/api/config` reported video generation available without an error.
This setup check did not generate a video.
No credentials or signing values belong in this document.

Implementation: `MultiImageClient/GrokWebBrowserClient.cs`.

## Configuration

- `GrokWebCookiePath`: existing complete grok.com cookie export; `sso` and `sso-rw` are required.
- `GrokWebStatsigVerificationKey` and `GrokWebStatsigAnimationKey`: leave both blank (the default) to sign with page keys. Set both only to pin a captured pair for diagnosis. Updated 2026-10-07; a pair is no longer required.
- `GrokWebStatsigCapturedAtUtc`: capture time that `--grok-web-statsig-pin` writes with a pinned pair. Logs and errors report the pair's age from it.
- `--grok-web-capture-statsig --input-image <path>`: verify-only signing check; add `--grok-web-statsig-pin` to pin the captured pair.
- `--grok-web-statsig-self-test`: offline signer test; it needs no settings, cookies, or network.
- `GrokWebBrowserExecutablePath`: optional existing Chrome/Chromium executable for capture and CLI fallback.
- `GrokWebBrowserHeaded`: optional troubleshooting window for capture and CLI fallback.
- `GrokWebVideoTimeoutSeconds`: app-chat request timeout, default 900.
- `GrokWebVideoPollTimeoutSeconds`: media-post polling limit after app-chat returns without a video URL, default 180.
- `--playwright-install`: installs Playwright Chromium when no executable path is configured.
- `--grok-web-video-method normal|fun|custom|spicy`: CLI method selection.
- `--grok-web-headed`: shows the CLI Grok browser when capture/fallback requires it.

Cookies are credentials. Never log their values, commit the cookie file, or include it in captures.

## UI contract

`POST /api/video-jobs` keeps the existing multipart fields:

- source job/generator/index;
- optional `prompt`;
- required `mode`;
- `aspectRatio` (`source` or an explicit supported ratio);
- duration;
- resolution.

The server normalizes and rejects a missing/unknown method. An empty prompt remains valid.

`sourceGenerator` may identify any image-producing generator in the source job. For a redo it is `input`, which reuses the video job's archived source image rather than trying to use the generated MP4 as image input. The server verifies that the selected source bytes have an `image/*` content type.

## Operational caveats

- This is an unofficial consumer-web integration and can break when grok.com changes its page or anti-automation behavior.
- UI image and video jobs do not start Chromium. Playwright remains an explicit capture/CLI-fallback dependency.
- A rejected signing key or other provider rejection affects only that target and is surfaced through the normal job error result. The next signed request fetches a new page key.
- A grok.com page or chunk layout change stops signed edit and video with "grok.com page format changed" until the parser is updated.
