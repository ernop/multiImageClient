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

## Browser-free image-edit signing (2026-08-05)

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
   loaded settings file.

With that pair configured, image edit sends a directly signed
`POST /rest/app-chat/conversations/new` with no browser. A live local test on
2026-08-05 completed one `imagine-image-edit` request in about 50 seconds and
returned one 1328x784 image. The .NET HTTP transport was accepted, so this
route did not require Chrome TLS impersonation or browser header ordering in
that test.

Capture values are deployment-specific. Missing, incomplete, malformed, or
stale values are hard failures for signed edit; they are never replaced with
guessed values. Text-to-image remains available through its separate
WebSocket when signing material is absent. The UI reports grok-web as
image-capable only while a complete validated pair is configured.

Browser-free video has not been live-verified and remains on the existing
Playwright real-control path. Sharing app-chat is not sufficient evidence that
video has the same accepted anti-bot contract.

## Implemented transport split

- Text-to-image generation continues to use `wss://grok.com/ws/imagine/listen`.
- Image editing does **not** use that WebSocket. `properties.image_uri` is accepted but ignored by the consumer transport (observed 2026-07-31: outputs invented from the prompt alone). Live edit uses `POST /rest/app-chat/conversations/new` with `modelName: "imagine-image-edit"` and `mediaGenInput.imageToImage.inputAssets: [assetId]`. With current captured signing material this is direct browser-free HTTP; otherwise CLI edit retains the real-control Playwright transport.
- Image upload, asset lookup, media-post creation, polling, and downloads continue to use `GrokWebClient` HTTP calls.
- Video generation's app-chat POST runs through `GrokWebBrowserClient`, a shared Playwright Chromium context with the `GrokWebCookiePath` cookies injected. Image edit uses it only when no verified browser-free signing pair is configured.
- The app first uploads the source and creates its normal Grok image post. The browser client opens that post, verifies the session through `/rest/media/imagine/quota_info`, clicks Grok's real **Make Video → Quick Animate** controls, and intercepts only the outgoing request body. Grok's generated integrity headers remain untouched while the body receives the selected method, optional motion prompt, duration, resolution, and aspect ratio.
- Browser app-chat operations are serialized. The UI owns one browser client for the server lifetime; the CLI owns one for the workflow lifetime.
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

## Configuration

- `GrokWebCookiePath`: existing complete grok.com cookie export; `sso` and `sso-rw` are required.
- `GrokWebBrowserExecutablePath`: optional existing Chrome/Chromium executable.
- `GrokWebBrowserHeaded`: optional troubleshooting window.
- `GrokWebVideoTimeoutSeconds`: browser request timeout, default 900.
- `GrokWebVideoPollTimeoutSeconds`: media-post polling limit after app-chat returns without a video URL, default 180.
- `--playwright-install`: installs Playwright Chromium when no executable path is configured.
- `--grok-web-video-method normal|fun|custom|spicy`: CLI method selection.
- `--grok-web-headed`: shows the CLI/UI-owned Grok video browser.

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
- Chromium startup is lazy: ordinary grok-web image jobs do not require Playwright.
- A browser/session failure affects the video job and is surfaced through the existing SSE error result.
