# BFL / FLUX provider errors

Settled 2026-09-03 after FLUX1.1 Pro returned a FastAPI 422 that echoed the
whole request, including `image_prompt` base64, into the job-card error, and
FLUX1.1 Pro Ultra failed with a raw `EnsureSuccessStatusCode` 429.

## User-facing error text

`BFLHttpError` formats every non-success BFL HTTP response (submit and poll).

- Take `detail[].msg` / `detail` / `message` from the JSON body.
- Never copy `detail[].input` or any other field that holds the request,
  `image_prompt`, `input_image`, or base64 PNG bytes.
- Cap the message at 480 characters.
- HTTP 422: `BFL rejected this request (HTTP 422).` plus the validation text.
- HTTP 429: `Black Forest Labs rate-limited this request (HTTP 429).` plus
  any provider text and `Retry-After` when the header is present. This is a
  rate limit, not a billing failure. HTTP 402 remains the billing path.
- HTTP 402: keep the words `payment` and `402` so `ProviderActionHints` still
  links to https://dashboard.bfl.ai.

Do not dump `HttpResponseMessage.EnsureSuccessStatusCode()` text such as
`Response status code does not indicate success: 429 (Too Many Requests).`

## FLUX 1.1 image remix size

`image_prompt` on `flux-pro-1.1`, `flux-pro-1.1-ultra`, and `flux-dev` must
be at least 256×256 pixels. BFL rejects smaller images with HTTP 422.

`BFLGenerator.RequireImagePromptDimensions` checks that size before the HTTP
call and reports the exact attached `WxH`. Undersized inputs are a hard
error. The bytes are not upscaled. That matches Recraft image-to-image:
downscale is allowed for oversized inputs; upscale would invent detail.

Kontext and FLUX.2 `input_image` keep the HTTP formatter only. Do not invent
a 256×256 floor there without a provider error that states it.
