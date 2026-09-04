# FableBot — Discord bot posting interface

Status: v1 implemented 2026-09-04 (post-only). Owner setup pending: pick a
bot token and channel id, fill in settings, run `--check`, then a live post.

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

The existing vibecoders webhook sender (`DiscordVibecoders.cs`) is unchanged
and remains the `--ui` share button's transport.

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
- **Not wired into `--ui` in v1.** The first consumer is the CLI. Posting
  generated results from the web UI is future work and will reuse
  `FableBotDiscordClient`.

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

## Future work (not in v1)

- Post finished `--ui` results (reusing the vibecoders share-claim pattern).
- Read/reply: gateway connection or slash-command interactions.
- Automated posting policy, including a researched 429 retry contract.
