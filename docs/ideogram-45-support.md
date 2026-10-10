# Ideogram 4.5 support

Ideogram released Ideogram 4.5 on 2026-09-30.
This application added it on 2026-10-08 as the UI target `ideogram-v45`.
The display name is **Ideogram 4.5**.
The owner requested default selection in every environment.
On 2026-10-10 the owner kept Ideogram 4.0 selected by default as well.
The owner also chose to add Ideogram 4.5 once to every saved default list.

## Requirements

| ID | Requirement | Status |
|---|---|---|
| R1 | Offer Ideogram 4.5 as a separate image target. Keep `ideogram` as Ideogram 4.0. | Done |
| R2 | Send text-only jobs to Generate and image jobs to Precise Edit. | Done |
| R3 | Select Ideogram 4.5 by default for users without saved preferences. | Done |
| R4 | Add Ideogram 4.5 once to each environment's saved default list. | Release of 2026-10-10 |
| R5 | Show Ideogram 4.5 in the **only SOTA** and **text ok** groups. | Done |
| R6 | Fail closed on every request, reply, and download outside the published contract. | Done |
| R7 | Route payment failures to the Ideogram billing page. | Done |
| R8 | Offer `ideogram-v45` in `--showcase --gens` and the REPL. | Done |
| R9 | Add Ideogram 4.5 once to every saved personal default list. Keep later removals. | Release of 2026-10-10 |
| R10 | Keep Ideogram 4.0 selected by default beside Ideogram 4.5. | Done |

## Endpoints

Both endpoints use multipart form data and the existing `IdeogramApiKey` in the `Api-Key` header.

| Operation | Endpoint |
|---|---|
| Generate | `POST https://api.ideogram.ai/v2/image/generate/ideogram-4-5` |
| Precise Edit | `POST https://api.ideogram.ai/v2/image/precise-edit/ideogram-4-5` |

Generate sends `prompt` and optional `size`, `quality`, `num_images`, and `seed`.
Precise Edit sends `prompt`, one `image`, zero to four `reference_images`, and optional `quality`, `num_images`, and `seed`.
Precise Edit has no size field.
It returns the source image's dimensions.

The client enforces these published limits before it sends a request:

- The prompt holds at most 10,000 Unicode characters.
- `num_images` is 1 to 8.
- `seed` is zero or greater.
- Generate accepts `low`, `medium`, or `high` quality.
- Precise Edit also accepts `very_low` quality.
- Each image is PNG, JPEG, or WEBP, at most 50,000,000 bytes.
- The declared image type must match the file's magic bytes.
- Generate accepts only the published size presets in `IdeogramClient`.
  The 4.0 size 512x1536 is not a 4.5 preset.

Every reply must contain `generation_kind`.
Generate replies must also contain `generation_id`.
Each image entry must contain `resolution`, `is_image_safe`, `prompt`, and `seed`.
The number of image entries must equal the requested count.
A reply outside this contract is a hard error.

The provider returns HTTP 402 for missing credits.
The failure carries the billing link `https://ideogram.ai/manage-api`.
HTTP 422 means the prompt failed safety checks.
HTTP 429 means rate limiting, not billing.

## Prices

Ideogram's `dry_run=true` price quotes are free.
The quotes below came from that mode on 2026-10-07.

| Quality | Generate | Precise Edit |
|---|---|---|
| `very_low` | not accepted | $0.008 |
| `low` | $0.03 | $0.03 |
| `medium` | $0.06 | $0.06 (provider default) |
| `high` | $0.10 (provider default) | $0.22 |

A 1K Generate size costs the same as a 2K size.
A 768x512 source and a 2048x2048 source produced the same Precise Edit quote.
Generate with attached images produced the same quotes as Precise Edit.

## Decisions (2026-10-08)

1. **Separate key.** Ideogram 4.5 uses the new key `ideogram-v45`.
   Stored jobs, groups, preferences, and environment lists keep `ideogram` as Ideogram 4.0.
2. **Routing.** A job without attachments uses Generate.
   A job with attachments uses Precise Edit.
   The first attachment is the edited image.
   Later attachments go as references, up to four.
   Precise Edit copies unchanged source pixels exactly.
   Generate with images costs the same, but it repaints the whole image.
3. **Output size.** Auto omits `size`, and Ideogram chooses a 2K size from the prompt.
   Explicit shapes map to published 2K presets:
   square 2048x2048, landscape 2496x1664, portrait 1664x2496, wide 2560x1440, tall 1440x2560.
   The detail control has no effect, because 1K and 2K cost the same.
4. **Aspect ratio with images.** Precise Edit always keeps the source size.
   The server rejects an image job that pairs Ideogram 4.5 with an explicit shape.
   The composer disables the chip in that case, as it does for Recraft.
5. **Quality.** UI `low`, `medium`, and `high` pass through.
   UI `xhigh` and `max` send `high`.
   Goal-loop `auto` omits the field, so Ideogram applies its own default.
   Any other value is a hard error.
   The composer default `high` costs $0.10 per Generate and $0.22 per Precise Edit.
   The UI does not offer `very_low`.
6. **Count.** The UI `n` control runs up to eight separate one-image requests, as for other targets.
7. **Sketches.** Ideogram 4.5 is not sketch-capable.
   Precise Edit keeps the sketch's pixels instead of painting a new scene over them.
8. **Scheduling.** Ideogram 4.5 shares the `ideogram` lane with Ideogram 4.0, 3.0, and 2.0.
9. **Downloads.** The generator accepts only absolute HTTPS image URLs.
   The served media type must match the file's magic bytes.
   The decoded dimensions must equal the reported `resolution`.
   An image with `is_image_safe: false` is a moderation failure.
   A download failure never carries the generation call's billing or moderation classification.
   The application saves the downloaded bytes without change.
10. **Provider prompt.** Ideogram returns its own structured rewrite of the prompt.
    The job records that text as an `IdeogramRewrite` step.
11. **Prompt limit.** A prompt over 10,000 characters fails at this target with a clear error.
    The application does not truncate it.
    The composer's prompt-limit notice promises truncation, so `/api/config` does not publish this limit there.
12. **Labels.** The composer and job cards show **Ideogram 4.5**.
    Contact sheets show, for example, `Ideogram 4.5 — Ideogram · generate · quality high`.
    The label adds `auto size` only when Ideogram chose the size.
    The sheet already appends the returned pixel size.
13. **CLI.** `--showcase --gens ideogram-v45` and REPL `:gens add ideogram-v45` use Generate at 2048x2048 and `high` quality.

## Default selection

The composer selects a target by default when two conditions hold.
The target is default-on, and it belongs to **only SOTA**.
Ideogram 4.5 now meets both conditions in the built-in catalog.
Ideogram 4.0 stays default-on and in **only SOTA**.
The owner kept both selected on 2026-10-10.

Default-on comes from one of two sources:

- An environment with a saved default list uses that list.
- An instance without such a list uses the built-in set.
  That set is now gpt-image-2, Recraft V4.1, grok-web pro, Ideogram 4.5, Ideogram 4.0, FLUX.2 Pro Preview, and Nano Banana 2.

A user with saved preferences uses their own default list instead.

## One-time addition to saved lists (2026-10-10)

The owner chose to add Ideogram 4.5 once to every saved default list.
This decision overrides the 2026-09-08 rule for this one key.
That rule says changed defaults never overwrite personal choices.

Each addition happens once per list.
A later removal by a user or the owner stays removed.
A list that hides Ideogram 4.5 stays unchanged.
A list that already selects it stays unchanged.
An empty saved list becomes a list with Ideogram 4.5 only.

| Saved list | Location | Writer | Once-only record |
|---|---|---|---|
| Account defaults | `ui_generator_preferences` in each instance's community database | That instance, at startup | `ui_migrations` row `2026-10-10-ideogram-v45-account-defaults` |
| Environment defaults | Environment registry | The original controller, at startup | `ui_migrations` row `2026-10-10-ideogram-v45-environment-defaults` in the controller's database |
| Standalone login-link defaults | Login-link file of an instance without a registry | That instance, at startup | The same environment row in that instance's database |
| Browser settings document | `mic_personal_configuration_v2`, version 3 | The page, when it reads the document | Document version 4 |
| Exported settings file | Versions 1, 2, and 3 | Import | The imported document is version 4 |
| Old browser key | `mic_generator_preferences_v1` | The page, on its last read | The composer replaces it with a version 4 document |

These rules apply:

- The account update and its record commit in one SQLite transaction.
- A malformed saved account list stops startup.
  The message names the account.
  Nothing changes, and no record is written.
- The registry rejects unknown fields.
  Its record therefore lives in the controller's community database.
- A repeated registry write is harmless, because it adds only a missing key.
- Older servers ignore unknown keys in registry lists.
  Vibecoders therefore keeps working between the two instance updates.
- In registry mode the shared login-link list stays unchanged.
  That list has no editor in registry mode.
  Older servers refuse to start when it holds an unknown key.
  The production list is unset (`null`).
- Document version 4 exists only to record this addition.
  The version 3 to version 4 migration changes nothing else.
- A version 2 document migrates to version 3 first.
  Unknown versions still fail.

## Verification

Automated tests in `MultiImageClient.Tests/IdeogramV45ApiTests.cs` cover:

- multipart field names, order, values, file names, and media types for both endpoints;
- omission of unset optional fields;
- rejection of out-of-contract requests before any network call;
- rejection of out-of-contract replies;
- download checks for dimensions, media type, and HTTP failure;
- unsafe-image, payment, and moderation classification;
- prices, quality mapping, configuration checks, size presets, and catalog facts.

`MultiImageClient.Tests/DefaultSelectionMigrationTests.cs` covers the server additions:

- account lists gain the key once, and a later removal survives a restart;
- hidden and present keys stay unchanged, and other preference fields stay exact;
- a malformed account list fails without changes or a record;
- only the controller changes registry lists, and unset lists stay unset;
- registry mode leaves the login-link list unchanged;
- a standalone login-link instance changes its own list once.

`MultiImageClient/Ui/tests/personal-config.test.js` covers the version 2, 3, and 4 document chain.
It checks hidden and present keys, later removals, and malformed version 3 documents.

Live checks on 2026-10-07 called the API directly:

- Generate at `high` returned 2048x2048 in 17.7 s.
- Precise Edit at `high` returned 2048x2048 in 49.8 s.
- 91.8% of that edit's pixels matched the source exactly.

Live checks on 2026-10-08 ran through an isolated local UI instance:

- Generate at `low`, landscape, returned 2496x1664 in 13.5 s for $0.03.
- Precise Edit at `low` changed a bicycle's color.
  It returned the source size, 2496x1664, in 25.4 s for $0.03.
  93.5% of pixels matched the source exactly.
  1.6% differed by more than 8 of 255 levels.
- An image job with an explicit shape returned a clear HTTP 400 before any provider call.
- A browser profile without saved settings opened in **only SOTA** with Ideogram 4.5 selected.
- The image host rejects Python's default user agent with HTTP 403.
  .NET sends no user agent, and its downloads succeeded.

## Files

| File | Role |
|---|---|
| `IdeogramAPI/IdeogramClient.cs` | 4.5 endpoints, limits, size presets, and reply checks |
| `IdeogramAPI/request/IdeogramV45GenerateRequest.cs` | Generate request |
| `IdeogramAPI/request/IdeogramV45PreciseEditRequest.cs` | Precise Edit request |
| `IdeogramAPI/request/options/IdeogramV45Quality.cs` | Quality values |
| `IdeogramAPI/response/V45/IdeogramV45Response.cs` | Reply contract |
| `MultiImageClient/ImageGenerators/IdeogramV45Generator.cs` | Routing, prices, labels, and download checks |
| `MultiImageClient/Implementation/UiJobs.cs` | UI key, capability sets, shape mapping, and generator construction |
| `MultiImageClient/Workflows/UiWorkflow.cs` | Catalog entry, groups, default-on set, aspect-ratio rule, and startup migration calls |
| `MultiImageClient/Implementation/GeneratorPresentation.cs` | Display and contact-sheet names |
| `MultiImageClient/Implementation/ProviderActionHints.cs` | Billing and key links |
| `MultiImageClient/Implementation/UiTargetScheduler.cs` | Shared `ideogram` lane |
| `MultiImageClient/Workflows/GeneratorGroups.cs`, `ReplWorkflow.cs` | CLI and REPL short name |
| `MultiImageClient/Ui/wwwroot/index.html` | Composer options help |
| `MultiImageClient/Workflows/UiWorkflow.DefaultSelectionMigration.cs` | One-time additions to account and environment lists |
| `MultiImageClient/Implementation/UiCommunity.cs` | `ui_migrations` records and the account-list transaction |
| `MultiImageClient/Ui/wwwroot/personal-config.js` | Document version 4 and the version 3 migration |
| `MultiImageClient/Ui/wwwroot/app.js`, `generator-chooser.js` | Import chain and the old browser key |
| `MultiImageClient.Tests/IdeogramV45ApiTests.cs` | Contract and behavior tests |
| `MultiImageClient.Tests/DefaultSelectionMigrationTests.cs` | One-time addition tests |
