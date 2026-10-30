# Nano Banana 2.1 and Gemini 3.8 Flash (2026-10-30)

Status: **implemented 2026-10-30.**

## Owner decision (2026-10-30)

After the Gemini 4 Argon release, the owner asked:

> wait, i thought google had released a newimage making endpoint/model/etc?

Google released Nano Banana 2.1 on 2026-10-06. The Argon work missed it.
The owner chose two changes:

- Upgrade the existing Nano Banana 2 target to Nano Banana 2.1.
- Move the Gemini Flash describe target, the layout map, and the Flash goal-loop manager to Gemini 3.8 Flash.

## Provider facts (checked 2026-10-30)

Sources:
[Nano Banana 2.1 model page](https://ai.google.dev/gemini-api/docs/models/gemini-nano-banana-2.1),
[Nano Banana 2.1 model card](https://deepmind.google/models/model-cards/nano-banana-2-1/),
[Image generation guide](https://ai.google.dev/gemini-api/docs/image-generation),
[Deprecations](https://ai.google.dev/gemini-api/docs/deprecations),
[Release notes](https://ai.google.dev/gemini-api/docs/changelog),
[Pricing](https://ai.google.dev/gemini-api/docs/pricing),
[Gemini 3.8 Flash model page](https://ai.google.dev/gemini-api/docs/models/gemini-3.8-flash),
[What's new in Gemini 3.8 Flash](https://ai.google.dev/gemini-api/docs/generate-content/whats-new-gemini-3.8).

Nano Banana 2.1:

- The model ID is `gemini-nano-banana-2.1` (stable). Google released it on 2026-10-06.
- The model card states that it is based on Gemini 3.6 Flash. It is not a Gemini 4 model.
- Google calls it an update to Nano Banana 2 (`gemini-3.1-flash-image`).
- Google deprecated `gemini-3.1-flash-image` on the same day. No shutdown date is announced. Google names 2.1 as the replacement.
- Output sizes are 1K, 2K, and 4K. The 512 size of Nano Banana 2 is not available.
- It adds the ratios 1:4, 4:1, 1:8, and 8:1. It accepts up to 14 reference images.
- Thinking levels are `minimal`, `medium` (the default), and `high`.
- `models.list` for our key lists the model with `generateContent`.
- Price: input costs $1.50 per million tokens. Image output costs $30 per million tokens. Thinking and text output cost $7.50 per million tokens.
- The price list gives 1,120 image tokens at 1K, 1,680 at 2K, and 3,780 at 4K. That is $0.0336, $0.0504, and $0.113.
- The image guide's table gives 2,520 tokens at 4K. The app uses the price list, because it governs billing.
- Nano Banana 2 now costs $60 per million image tokens: $0.067 at 1K, $0.101 at 2K, and $0.151 at 4K. The app had estimated it at $30.

Gemini 3.8 Flash:

- Google deprecated `gemini-3.5-flash`. The API sends requests for it to `gemini-3.6-flash`.
- `gemini-3.8-flash` (stable, 2026-09-02) is the newest Flash model. Its output is text only.
- The input limit is 1,048,576 tokens. The output limit is 65,536 tokens.
- Thinking levels are `low`, `medium` (the default), and `high`. `minimal` returns an error.
- Google says to remove `temperature`, `top_p`, and `top_k`. Google Cloud states that the backend ignores them.
- Price: $0.75 input and $3.75 output per million tokens through 2026-12-31. From 2027-01-01: $1.50 and $7.50.
- Gemini 3.6 Flash has the same prices.

## Decisions

### Nano Banana 2.1 replaces Nano Banana 2 in place

- The stable `google` key now sends `gemini-nano-banana-2.1`. Its label is "Nano Banana 2.1".
- The key stays, because jobs, preferences, groups, environment defaults, and archives store it.
- Both production default lists already hold `google`. No configuration change is necessary.
- Result cells and contact sheets take their label from the key. Old Nano Banana 2 images now show "Nano Banana 2.1".
- The provider-call trace still records the real model of each call. The Grok Imagine 2.0 change works the same way.
- The CLI `GoogleNanoBanana` type also sends 2.1.
- The request is unchanged: `generateContent`, `responseModalities: IMAGE`, and `imageConfig` ratio and size.
- UI detail maps to 1K, 2K, or 4K. The app never sends the removed 512 size.
- Source-shape matching keeps the ten standard ratios. It does not use the four new wide ratios.
- The estimate is image tokens at $30 per million, plus 1,000 thinking and text tokens at $7.50 per million.
- The estimate is $0.0411 at 1K, $0.0579 at 2K, and $0.1209 at 4K.
- Nano Banana Pro is unchanged. Its $0.13 estimate is near Google's $0.134 per 1K or 2K image.

### Gemini 3.8 Flash replaces Gemini 3.5 Flash

- `GeminiVisionDescriber.DefaultModel` is `gemini-3.8-flash`. This moves `describe-gemini` and `layout-map`.
- The goal-loop manager keeps the key `manager-gemini-3.5-flash`, because saved goal loops store it.
- That manager sends `gemini-3.8-flash`. Its label is "Gemini 3.8 Flash".
- Gemini 3 and Gemini 4 describe requests send no `temperature`. The other request fields are unchanged.
- The Flash manager now has prices, so goal loops show its cost.
- Its price doubles at 2027-01-01 00:00 Pacific time, which is 08:00 UTC.
- The describe and layout-map estimates use the same schedule. Each assumes 2,000 input tokens and the 1,200-token describe budget.
- Each estimate is $0.006 through 2026 and $0.012 after. The old flat estimate was $0.03.

## Live verification (2026-10-30)

- A UI job on `google` returned a 1376x768 JPEG in 11.3 s. The trace shows `gemini-nano-banana-2.1:generateContent` and HTTP 200.
- A direct call with the same body reported `modelVersion` `gemini-nano-banana-2.1`.
- Its usage was 46 input, 1,120 image, 266 other output, and 629 thinking tokens. The cost was about $0.040.
- A UI job ran `describe-gemini` and `layout-map` on one PNG.
- Describe returned the JSON reply contract in 3.9 s. Layout map found 5 regions in 4.8 s.
- A manager-shaped request to `gemini-3.8-flash` returned JSON in 11.8 s.
- That request used high thinking, returned thought parts, and accepted `MEDIA_RESOLUTION_ULTRA_HIGH` per image.
- Its usage was 2,221 input (2,160 image), 81 answer, and 733 thinking tokens.

## Files

- `MultiImageClient/ImageGenerators/GoogleGenerator.cs`: model, size notes, and cost estimate.
- `MultiImageClient/Implementation/GeneratorPresentation.cs`: labels.
- `MultiImageClient/Workflows/UiWorkflow.cs`: chooser entry.
- `MultiImageClient/Describers/RemoteVisionDescribers.cs`: Flash model and no `temperature`.
- `MultiImageClient/TextLLMs/ManagerChatClients.cs`: Flash manager model, label, and prices.
- `MultiImageClient/Implementation/UiJobs.cs`: describe and layout-map estimates.
- `MultiImageClient.Tests/GoogleModelUpgradeTests.cs`, `GeminiArgonTests.cs`, and `GeneratorPresentationTests.cs`.
- Comments in `ImageGeneratorApiType.cs`, `GoogleImagen4Generator.cs`, and `GeneratorGroups.cs`.
