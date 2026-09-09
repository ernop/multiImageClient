# FableBot — Discord bot posting interface

Status: CLI implemented 2026-09-04; UI result posting implemented 2026-09-08.
Local configuration has no bot token or channel ID. Live verification remains pending.

## What it is

`FableBot/` is a standalone console project in `MultiImageClient.sln` that
posts messages — text and/or file attachments — into one Discord channel as a
**bot account**, over the Discord REST API v10 with an
`Authorization: Bot <token>` header. The v1 target is a private channel on
the owner's server ("Ernie server"). No gateway/WebSocket connection exists
in v1; the bot only posts, checks its identity, and looks up the channel.

A bot account was chosen over a second incoming webhook (the existing
`DiscordVibecodersWebhookUrl` path) because a bot account:

- can post into any private channel it is added to, without a per-channel
  webhook,
- has a real member identity (name, avatar, presence) on the server,
- can later be extended to read, reply, and react (gateway or slash
  commands), which a webhook can never do.

The existing vibecoders webhook sender (`DiscordVibecoders.cs`) sends attachments without private links
and remains the `send to vibecoders` button's transport.
The viewer now has a separate `send to Discord` button for FableBot.

### Production privacy correction (2026-09-09)

The initial webhook activation exposed the original tenant's private MIC address in a Discord message.
The link contained result identifiers, not a password or automatic-login token.
An unauthenticated recipient would see the MIC login form.
An existing authenticated session could open the selected result.
The owner removed the posted message.

The owner requires continued image posting without private MIC addresses or personal login links.
This supersedes the earlier decision to include a MIC result link in webhook posts.
The corrected webhook payload includes only the original attachment, sender name, and disabled mention parsing.
It contains no message content, embeds, site URL, or login URL.
Missing or oversized originals fail; URLs never substitute for attachment bytes.
`DiscordVibecodersWebhookUrl` enables the corrected sender independently of `UiPublicBaseUrl`.
`UiPublicBaseUrl` remains available for tenant login-link management and must not enter Discord payloads.

Production sharing was disabled immediately during the correction.
Only `multiimageclient-ui.service` restarted.
An authenticated configuration check confirmed `vibecoders.available: false` while the fix was prepared.
The correction uses an isolated copy of the deployed revision to exclude unrelated workspace changes.
A regression test captures the HTTP payload and rejects private site text, message content, and embeds.
No Discord test message was sent.
Four isolated webhook regression tests passed before publishing.
The production correction uses revision `63f8ac13d5268130c0d2eb01cea0a0d515fe3a22` plus the attachment-only source changes.
Its build identity ends with `discord-attachments-only`.
The documented update helper installed the build after active generations finished.
Authenticated loopback and public checks verified that build and `vibecoders.available: true`.
Image posting is restored without MIC links.
The production baseline still validates the two settings together; its corrected sender never reads or posts `UiPublicBaseUrl`.
The workspace correction separates webhook validation from the tenant base-address setting.
Include this privacy correction in the next normal release; deploying the earlier sender would restore the disclosure.

## Requirements

| # | Requirement | Status |
|---|---|---|
| R1 | Post a text message to one configured Discord channel as a bot account. | Implemented |
| R2 | Attach files (images, video, arbitrary bytes) to a post; bytes travel verbatim. | Implemented |
| R3 | Live in the MultiImageClient solution as its own project (`FableBot/`). | Implemented |
| R4 | Work with either a newly created bot application or an existing one (for example the SocialAI bot); the interface takes whatever token is configured. | Implemented |
| R5 | Configuration through the standard `settings.json` (`FableBotDiscordBotToken`, `FableBotDiscordChannelId`), never committed. | Implemented |
| R6 | Fail closed: missing/partial configuration, non-numeric channel ids, oversized or empty messages, and every Discord rejection are hard errors. No retries, no substitution. | Implemented |
| R7 | `--check` verifies the token and channel visibility without posting. | Implemented |
| R8 | Send the selected successful UI image or MP4 original through FableBot. | Implemented 2026-09-08 |
| R9 | Preserve exact channel, job, generator, and image-index identity across sends. | Implemented 2026-09-08 |
| R10 | Keep durable pending/sent records; block duplicates after uncertain delivery. | Implemented 2026-09-08 |
| R11 | Bound upload memory and concurrency; verify hosted originals against recorded SHA-256. | Implemented 2026-09-08 |

## Settled decisions (2026-09-04)

- **Transport:** raw HTTPS against `https://discord.com/api/v10` with
  `System.Net.Http` + `System.Text.Json`. No Discord NuGet library — v1 needs
  three endpoints (`GET /users/@me`, `GET /channels/{id}`,
  `POST /channels/{id}/messages`), and the repo's other providers are also
  hand-rolled HTTP clients. Revisit only if a gateway listener is added.
- **Identity checks before posting:** every run resolves the bot identity and
  the channel first. This turns a bad token or a channel the bot cannot see
  into a specific error before any message is created, and supplies the
  `guild_id` for the printed jump link (REST message responses omit it).
- **Message limits enforced locally, fail-closed:** content over 2000
  characters, more than 10 files, an empty file, or a file over 10 MiB is
  rejected before the request is sent — never truncated or split.
- **Mentions disabled:** every post sends `allowed_mentions: {"parse": []}`,
  so pasted `@everyone`/user mentions never ping.
- **Content types from magic bytes:** PNG/JPEG/WEBP/GIF/MP4 are detected from
  the file's leading bytes; anything else is sent as
  `application/octet-stream`. Discord accepts any attachment type; the bytes
  are never re-encoded.
- **No retry on 429:** a rate-limit response is a visible failure carrying
  Discord's body (which includes `retry_after`). v1 is a manual, low-volume
  poster; automated retry policy is deferred until an automated caller
  exists.
- **Settings validation is in `Settings.Validate()`:** both keys blank
  disables FableBot; one set without the other, a whitespace/short token, a
  `Bot `-prefixed token, or a non-numeric channel id fails settings load for
  every run mode, matching the vibecoders pair rule.
- **Namespace `FableBot`, single level,** per the repository namespace rule.
- **CLI first:** superseded by the UI implementation below on 2026-09-08.

## UI posting decisions (2026-09-08)

Open a successful image or video in the composer viewer.
Select `send to Discord` to post its original through the configured bot.
The control appears only when both FableBot settings are valid.
The action sends one PNG, JPEG, WEBP, GIF, or MP4 attachment without changing its bytes.
The caption identifies the generator and authenticated sender, or `local` when authentication is disabled.
The message does not include the private site address, login links, or prompt text.
The instance configuration fixes the destination channel; browser requests cannot override it.
Goal-loop and recap pages do not yet have this control.

The server accepts only successful, visible results resolved from recorded job events.
Hidden jobs, hidden images, missing originals, unsupported formats, and oversized originals fail before posting.
Hosted originals use their exact recorded B2 key and must pass the recorded SHA-256 check.
Previews and image URLs never replace attachment bytes.
The attachment limit remains 10 MiB.
One process-wide send slot bounds concurrent downloads and uploads.
Busy requests fail immediately; the server does not accumulate a send queue.
Local reads and hosted response buffers enforce the size limit before materializing oversized originals.

Before posting, the client verifies its bot identity and destination channel.
The client checks the returned channel, attachment count, and attachment byte lengths after posting.
Discord requests disable mentions and never retry automatically.
The API follows the existing instance authentication boundary.
POST additionally requires `X-MIC-FableBot: 1` to reject ordinary cross-site form submissions.

Each send first writes a durable `pending` record under `UiFableBot/` in the instance data root.
The identity includes channel ID, job ID, generator key, and image index.
Success atomically records `sent` and the Discord message link.
Any failure after claiming retains `pending`, including a lost response or process interruption.
This conservative policy can also retain a claim after an explicit Discord rejection.
The UI labels this state `check delivery in Discord` and blocks another send.
An operator must inspect Discord before reconciling a pending record on disk.
No automatic reconciliation or claim-reset API exists.
Failures before claiming permit another attempt after reopening the viewer.

The store reads individual records from disk without loading the entire send history into memory.
Viewer selection checks prevent delayed status responses from changing a different image's controls.
Closing the viewer clears the selected send identity and presentation.

### HTTP API

| Endpoint | Behavior |
|---|---|
| `GET /api/discord/fablebot` | Return availability without credentials or channel details. |
| `GET /api/discord/fablebot?jobId=…&generator=…&imageIndex=…` | Return `ready`, `pending`, or `sent` for the exact visible result. |
| `POST /api/discord/fablebot` | Accept form fields `jobId`, `generator`, and `imageIndex`; attach the selected original. |

Successful POST responses contain `state: sent` and `jumpUrl`.
Rejected identity requests return 400 or 404; competing or duplicate sends return 409.
Preparation or delivery failures return 502 with a visible error.

The implementation uses Discord's [Create Message contract](https://docs.discord.com/developers/resources/message#create-message).
The review checked that contract on 2026-09-08.

## Configuration

In `settings.json` (or the file named by `MULTIIMAGECLIENT_SETTINGS`):

```json
"FableBotDiscordBotToken": "<raw bot token, no Bot prefix>",
"FableBotDiscordChannelId": "<numeric channel id>"
```

Both blank (the default) disables FableBot. The token is a secret; keep it
out of git (settings.json is already gitignored).

## Owner setup checklist

Option A — new bot (recommended so FableBot has its own name/avatar):

1. Open https://discord.com/developers/applications and create an
   application named `FableBot`.
2. On the Bot page: copy the token (Reset Token if needed). Privileged
   intents are NOT needed for posting.
3. Turn off "Public Bot" so only you can install it.
4. Invite it to the server: OAuth2 URL Generator, scope `bot`, permissions
   View Channel + Send Messages + Attach Files (permissions integer
   `35840`), open the generated URL, pick the Ernie server.
5. Because the target channel is private, also grant the bot access there:
   channel settings > Permissions > add the FableBot role or member with
   View Channel + Send Messages + Attach Files.
6. Enable Developer Mode (User Settings > Advanced), right-click the
   channel, Copy Channel ID.
7. Fill in the two settings keys and run the check below.

Option B — reuse the SocialAI bot: take its existing token from the SocialAI
project (`/proj/SocialAI/`), confirm that bot is on the Ernie server, grant
it the private channel per step 5, and use that token. FableBot posts under
that bot's name; the two projects share one identity and one rate-limit
budget. Any later FableBot permission change also affects SocialAI.

## CLI usage

```bash
dotnet run --project FableBot -- --check
dotnet run --project FableBot -- --message "hello from FableBot"
dotnet run --project FableBot -- --message "with a picture" --file saves/example.png
dotnet run --project FableBot -- --message "elsewhere" --channel 123456789012345678
```

Exit codes: 0 posted/verified, 1 usage or configuration error, 2 Discord
rejected the request. A successful post prints the message id and the
`https://discord.com/channels/{guild}/{channel}/{message}` jump link.

## Files

- `FableBot/FableBot.csproj` — console project, references
  `ImageGenerationClasses` for `Settings`.
- `FableBot/FableBotDiscordClient.cs` — `FableBotDiscord` (static limits +
  validation + magic-byte content-type detection), `FableBotDiscordClient`
  (REST calls), records for identity/channel/posted-message.
- `FableBot/Program.cs` — argument parsing, settings resolution (same
  search order as the main app), check/post flows.
- `ImageGenerationClasses/Settings.cs` — the two `FableBot*` keys and their
  fail-closed validation.
- `MultiImageClient.Tests/FableBotTests.cs` — snowflake/token/message-limit/
  content-type/jump-link/settings-validation tests (no network).
- `MultiImageClient.Tests/FableBotDeliveryTests.cs` — mocked delivery, attachment validation, and durable duplicate protection.
- `tools/test-fablebot-viewer.cjs` — delayed selection responses and uncertain-delivery button behavior.
- `MultiImageClient/Workflows/UiWorkflow.FableBot.cs` — authenticated result resolution and FableBot HTTP routes.
- `MultiImageClient/Implementation/UiFableBotStore.cs` — disk-backed pending and sent records.
- `MultiImageClient/Ui/wwwroot/app.js` — viewer button and selection-scoped status.
- `MultiImageClient/Implementation/UiJobs.cs` and `B2StorageClient.cs` — bounded original reads and hosted downloads.
- `MultiImageClient/MultiImageClient.csproj` — reference to the existing FableBot client project.

## Future work (not in v1)

- Add posting controls to goal-loop and recap viewers.
- Read/reply: gateway connection or slash-command interactions.
- Automated posting policy, including a researched 429 retry contract.

## Verification (2026-09-08)

All 16 focused FableBot and vibecoders .NET tests passed.
Both viewer behavior tests passed with `node --test tools/test-fablebot-viewer.cjs`.
The composer JavaScript syntax check passed.
The .NET build reported existing nullable-reference warnings.
Local settings contain neither FableBot configuration value, so no live Discord request or post ran.
