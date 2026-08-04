# Provider Onboarding — New Image API Targets (researched 2026-07-30)

Signup/setup steps for each candidate provider, ready to execute. Each section
lists: how to get a key, the endpoint + request shape, pricing, known quirks
(especially prompt-length caps — wire these into the `maxPromptChars` config
plumbing added 2026-07-30), and the planned settings key. All of these are
hosted APIs; none involve running weights ourselves.

Recommended implementation order: **Seedream → Reve → Luma → Qwen → MiniMax →
Runway**. Seedream and Reve are the easiest integrations (Seedream is
near-OpenAI-shaped; Reve is synchronous base64-in-response).

---

## 1. Seedream (ByteDance) — BytePlus ModelArk

- **Signup:** create an account at <https://console.byteplus.com> (email + OTP).
  In **ModelArk → Foundation models**, open the Seedream model page, click
  "Deploy and use" → **Activate model**. Then **API Key Management → Create API
  Key** (one key covers all activated models).
- **Endpoint:** `POST https://ark.ap-southeast.bytepluses.com/api/v3/images/generations`
  (Malaysia; EU alternative: `https://ark.eu-west.bytepluses.com/api/v3`).
  Auth: `Authorization: Bearer $ARK_API_KEY`.
- **Models:** `seedream-5-0-pro`, `seedream-5-0-lite`, legacy `seedream-4-5`,
  `seedream-4-0`.
- **Request shape (OpenAI-images-like):**

```json
{
  "model": "seedream-5-0-pro",
  "prompt": "...",
  "size": "2048x2048",
  "response_format": "b64_json",
  "watermark": false
}
```

- **Pricing:** ~$0.03–0.09/image depending on model + resolution.
- **Quirks:** prompt should stay under ~600 English words (treat as a
  `maxPromptChars` cap; verify the exact char behavior empirically on
  integration). 5.0 Pro does NOT support `sequential_image_generation` or
  `stream` (Lite supports both). Set `watermark: false` explicitly. Also
  accepts `image` (URL or base64) for image-to-image/edit.
- **Settings key:** `SeedreamApiKey`. UI target name suggestion: `seedream`.

## 2. Reve

- **Signup:** <https://api.reve.com> console → agree to the API License
  Agreement → purchase credits → receive access keys.
- **Endpoints (all synchronous, base64 PNG in the JSON response — no polling):**
  - `POST https://api.reve.com/v1/image/create` — `{ prompt, aspect_ratio, version }`
  - `POST https://api.reve.com/v1/image/edit` — `{ edit_instruction, reference_image (b64), ... }`
  - `POST https://api.reve.com/v1/image/remix` — `{ prompt, reference_images: [b64 x1-6], ... }`
  - Auth: `Authorization: Bearer $REVE_API_KEY`.
- **Machine-readable spec:** <https://api.reve.com/llms.txt> (full parameter
  docs; interactive docs at <https://api.reve.com/console/docs>).
- **Response:** `{ image: "<base64 png>", version, content_violation,
  request_id, credits_used, credits_remaining }`. Non-200 carries `error_code`
  (e.g. `PROMPT_TOO_LONG`, `CONTENT_POLICY_VIOLATION`).
- **Pricing:** credit-based, ~$0.032/image reported; `credits_used` comes back
  on every response so real cost is directly observable.
- **Quirks:** **prompt max 2,560 chars** (hard cap → `maxPromptChars`).
  Aspect ratios: 16:9, 9:16, 3:2 (default), 2:3, 4:3, 3:4, 1:1. Optional
  `postprocessing` (upscale 2x/3x/4x, remove_background, fit_image) and
  `test_time_scaling` (1–15, more compute for better output, >5 rarely helps).
  Prompts are auto-enhanced by the model. Native 4K. The v2 layout endpoints
  (`create_layout` / `extract_layout` / `render_layout`) are experimental —
  ignore for the first integration.
- **Settings key:** `ReveApiKey`. UI target: `reve`.

## 3. Luma (Photon / uni-1)

- **Signup:** key from <https://platform.lumalabs.ai>.
- **Endpoint (create-then-poll, like our BFL client):**
  - `POST https://api.lumalabs.ai/dream-machine/v1/generations/image` —
    `{ prompt, aspect_ratio, model }` → `{ id }`
  - `GET https://api.lumalabs.ai/dream-machine/v1/generations/{id}` — poll
    until `state == "completed"` (or `failed` with `failure_reason`); image URL
    at `generation.assets.image`.
  - Auth: `Authorization: Bearer $LUMA_API_KEY`.
- **Models:** `photon-1` (default), `photon-flash-1` (faster/cheaper). The
  newer `uni-1` / `uni-1-max` live on a second API
  (`POST https://agents.lumalabs.ai/v1/generations`, same simple shape,
  image edit with up to 8 refs) — evaluate both at integration time.
- **Pricing:** ~$0.015/1080p image (photon-1), ~$0.002 (photon-flash-1).
- **Quirks:** image reference requires a public URL (no inline base64 on the
  legacy API; the Agents API accepts base64/file uploads). Poll interval ~2s.
- **Settings key:** `LumaApiKey`. UI targets: `luma` (photon-1), maybe
  `luma-flash`.

## 4. Qwen-Image (Alibaba) — Model Studio / DashScope

- **Accessible today.** Only Qwen-Image-**3.0**-Pro is invite-only; the 2.0
  series is self-serve.
- **Signup:** <https://modelstudio.console.alibabacloud.com> — plain Alibaba
  Cloud account. **Select the Singapore region** (keys are region-locked;
  Singapore has the intl endpoint + free quota). Dashboard → API Keys →
  Create API Key. New accounts: 1M free tokens per eligible model, 90 days,
  Singapore only.
- **Endpoint:** `POST https://dashscope-intl.aliyuncs.com/api/v1/services/aigc/multimodal-generation/generation`
  with `Authorization: Bearer $DASHSCOPE_API_KEY`.
- **Request shape (DashScope message format, not OpenAI):**

```json
{
  "model": "qwen-image-2.0-pro",
  "input": { "messages": [{ "role": "user", "content": [{ "text": "..." }] }] },
  "parameters": { "size": "2048*2048", "n": 1, "negative_prompt": "", "watermark": false, "prompt_extend": true }
}
```

- **Models:** `qwen-image-2.0-pro` (recommended; n=1–6, free WxH, total pixels
  512×512..2048×2048), `qwen-image-max` (higher realism, n=1 fixed),
  `qwen-image-plus`.
- **Quirks:** size uses `*` not `x`. Prompt cap: 1,300 tokens (2.0 series) /
  800 tokens (others) — the API silently TRUNCATES excess itself, so decide
  whether to surface a UI warning anyway. `prompt_extend` (auto prompt
  rewrite) defaults on. Watch region-locked keys.
- **Settings key:** `QwenApiKey` (or `DashScopeApiKey`). UI target: `qwen`.

## 5. MiniMax image-01

- **Signup:** <https://platform.minimax.io> → Console → sign up → add payment
  method → **API Keys → Create new secret key**. (Use pay-as-you-go API keys,
  NOT the `sk-cp-` Token Plan subscription keys — different product.)
- **Endpoint:** `POST https://api.minimax.io/v1/image_generation`,
  `Authorization: Bearer $MINIMAX_API_KEY`.
- **Request shape:**

```json
{
  "model": "image-01",
  "prompt": "...",
  "aspect_ratio": "16:9",
  "n": 1,
  "response_format": "base64",
  "prompt_optimizer": false
}
```

- **Pricing:** ~$0.0035/image — cheapest metered API in the market.
- **Quirks:** **prompt max 1,500 chars** (→ `maxPromptChars`). n=1–9. Aspect
  ratios 1:1/16:9/4:3/3:2/2:3/3:4/9:16/21:9 or custom WxH (512–2048,
  divisible by 8; aspect_ratio wins if both sent). URL responses expire in
  24h — use `response_format: "base64"`. `subject_reference` (single image)
  is character-reference i2i, not a general edit.
- **Settings key:** `MiniMaxApiKey`. UI target: `minimax`.

## 6. Runway Gen-4 Image

- **Signup:** <https://dev.runwayml.com> (separate from the consumer app;
  separate credit pool). Sign up → create an "organization" → create API key →
  billing tab → add credits, minimum $10 at $0.01/credit.
- **Endpoint (async task-poll):**
  - `POST https://api.dev.runwayml.com/v1/text_to_image` —
    `{ model: "gen4_image", promptText, ratio }` → `{ id }`
  - `GET https://api.dev.runwayml.com/v1/tasks/{id}` — poll until `SUCCEEDED`.
  - Headers: `Authorization: Bearer $RUNWAYML_API_SECRET` AND the mandatory
    `X-Runway-Version: 2024-11-06`.
- **Pricing:** images 5 credits at 720p / 8 at 1080p (~$0.05–0.08/image).
- **Quirks:** `promptText` max 1,000 chars (→ `maxPromptChars`). Ratios are
  exact enums (`1280:720`, `1584:672`, `1104:832`, `720:1280`, `832:1104`,
  `960:960`). `referenceImages` with @mention tags is the distinctive feature
  (identity/style consistency). Official Python/Node SDKs exist but plain
  REST is fine.
- **Settings key:** `RunwayApiKey`. UI target: `runway`.

## 7. Adobe Firefly — SKIP (enterprise-gated)

Confirmed 2026-07-30: Firefly API access requires an Adobe **enterprise**
agreement; individual Creative Cloud accounts cannot add the API in Developer
Console at all, and Adobe support/community confirm "enterprise customers
only". No self-serve tier exists. Revisit only if an enterprise Adobe
relationship materializes; some aggregators proxy it in the meantime.

## 8. Midjourney — no official API; two viable unofficial routes

Ecosystem status (2026-07-30): the two major bring-your-own-Discord-token
shims are DEAD — useapi.net discontinued Midjourney support 2026-06-24, and
PiAPI shut its Midjourney API (redirects to LegNext.ai). Discord-user-token
automation is a collapsing category; midjourney.com (web app, Google sign-in,
V8.1 default) is the primary surface and Discord is legacy.

- **Route A — hosted pool shims** (Apiframe, ImaginePro, kie.ai, Sharpii,
  LegNext, mjapi.io): plain REST + their API key; you provide NO Discord
  creds and need no MJ subscription; ~$0.02–0.08 per imagine (4-image grid).
  Provider absorbs all ban risk; our exposure is prepaid credits + their
  uptime. Before committing, evaluate: pricing, uptime reputation, response
  shape (task-poll vs webhook), and whether they return 4 separate images or
  one grid PNG.
- **Route B — `midjourney-web`** Playwright transport in the grok-web/meta-web
  mold against midjourney.com. No Discord involvement; still violates MJ's
  anti-automation TOS and MJ enforces it, so it MUST run on a dedicated
  MJ account + subscription we can afford to lose, never a personal one.
  Reuse the meta-web architecture: persistent Chromium profile, type into the
  real composer, let their JS do the signed calls, download from CDN through
  the browser context.
- **SocialAI note:** Discord bots cannot invoke another bot's slash commands,
  so SocialAI is capture-only (which is TOS-clean). Manual submit + SocialAI
  capture remains the zero-risk configuration.

## 9. Krea 2 — implemented 2026-08-04

Krea 2 is Krea's own foundation image model, trained from scratch. Krea's API
also aggregates third-party models, but these endpoints are Krea-owned:

- `POST /generate/image/krea/krea-2/medium-turbo` — $0.015/image
- `POST /generate/image/krea/krea-2/medium` — $0.03/image
- `POST /generate/image/krea/krea-2/large` — $0.06/image
- Poll exact `job_id` at `GET /jobs/{job_id}` until completed, failed, or
  cancelled. Completed output is `result.urls`.
- Auth is `Authorization: Bearer $KREA_API_TOKEN`; create tokens at
  <https://www.krea.ai/app/api/tokens>. API billing is separate from Krea app
  subscriptions.
- All three variants accept eight aspect ratios, seed, creativity, K2 sliders,
  image-to-image, and up to ten style references. The current OpenAPI accepts
  only `resolution: "1K"`.
- MultiImageClient exposes `krea`, `krea-turbo`, and `krea-large`. UI
  attachments are passed as 0.6-strength style references, not image-to-image
  sources, because style transfer is Krea 2's distinguishing workflow.
- Settings key: `KreaApiKey`. Scheduler lane: `krea`.

---

## Cross-cutting integration notes

- Every provider above gets: a settings key (lazy-validated by its generator,
  like existing providers), one `IImageGenerator` adapter, a
  `GeneratorGroups.BuildByShortName` entry, a UI catalog entry in
  `UiWorkflow` `/api/config`, and archive coverage for requests/responses.
- Prompt caps to wire into `maxPromptChars` when integrating: Reve 2,560
  chars; MiniMax 1,500 chars; Runway 1,000 chars; Seedream ~600 words
  (verify); Qwen truncates server-side (decide whether to warn anyway).
  grok-web's 8,192 is already wired.
- Multi-image responses (MiniMax n≤9, Qwen n≤6, Seedream Lite sequential)
  should follow the existing pattern: download in-generator, return
  `Base64ImageDatas`.
- Providers returning URLs with short expiry (MiniMax 24h, Luma) must be
  downloaded immediately in-generator — never store the URL as the artifact.
