# Public prompt pages and Discord confirmation

Decision date: 2026-09-17.

## Requirements

| Requirement | Behavior |
|---|---|
| Public subset | Publish one completed prompt, its recorded appended text, inputs, successful outputs, descriptions, and contact sheet. |
| Anonymous reading | Anyone possessing the random link can read that page without logging in. |
| Stable page | Every share from one exact prompt instance uses the same public page address. |
| Selected output | Each Discord **View prompt** link opens and highlights the selected output on that page. |
| Private boundary | Do not expose the private site prefix, reusable account links, other jobs, raw events, logs, provider requests, or filesystem paths. One-use account tokens go only to verified recipients through the separate DM flow. |
| Explicit publication | Clicking **send to vibecoders** prepares a private preview. It does not publish or post. |
| Concrete preview | Show the selected original, exact link labels, destination server/channel, and publication notice. |
| Full scope | Include an expandable preview of the complete public page before confirmation. |
| Final action | **Make public & send** publishes the page and sends the selected original. Already public prompts show **Send to thread**. |
| Compact caption | Send one line: **View prompt · Make your own**. Both phrases are masked Markdown links. |
| Reuse | **Make your own** requires login and environment membership, then loads the prompt and inputs into the composer. |
| New accounts | When enabled, show **Request account** beside the prompt and each output. Otherwise direct visitors to Ernie in Discord. Signup grants only Vibecoders membership; see the account contract below. |
| Publishing permission | Require the prompt creator or existing owner override. Ungated local instances retain local-owner semantics. |
| Stable consent | Bind consent to the exact snapshot, selected attachment, sender, public address, and Discord destination. |
| Changed data | Reject expired previews, changed destinations, deleted results, or changed snapshots. Require a new preview. |
| Duplicate protection | Persist a claim before dispatch. Keep it after uncertain delivery; never retry automatically. |

The notice reads: **Anyone with this link can see the prompt, inputs, all outputs, and contact sheet.**
The preview also names the destination server and channel.
The confirming person authorizes publication of the entire displayed prompt subset, not only the selected image.
Cancel and Escape discard the dialog without publication or posting.
Confirmation remains disabled until the attachment preview loads.

## Scope and content

This changes the Vibecoders webhook action. The separate FableBot action retains its existing attachment-only contract.
Do not republish historical Discord posts or create public pages automatically.
Only completed jobs can be shared. Require a contact sheet for image posts.
Video jobs show their recorded inputs and outputs; they have no image contact sheet unless one exists.
Reject a prompt containing deleted results, because its existing contact sheet could contain those deleted images.
Reject missing originals before presenting the confirmation.
Provider failures and diagnostic details remain private.

Snapshot public text and the exact asset allowlist on preparation.
Store internal job and generator identities only in server-side records.
Expose assets by integer slots under the random public token.
Serve the exact original from its durable local file or recorded B2 object.
Build thumbnails through the existing bounded thumbnail pipeline.
Keep original media bytes out of persistent process memory.
HTML-escape all prompt, generator, and returned text.
Do not include external scripts, analytics, private navigation, or account information.

Deleting the prompt or any result disables the associated public page and its asset routes.
Already downloaded copies and Discord attachments remain outside that access control.
The page sends `no-store`, `no-referrer`, `noindex`, and a restrictive content security policy.
The link is a bearer capability, not an authentication credential.
It grants read access to that fixed subset only.

## Publication and delivery states

`UiPublicShares/<256-bit-random-token>.json` stores page records and separate preview/delivery records.
Preview records expire after 20 minutes. Later preparations remove expired preview records.
Reserved page records remain private until confirmation and retain their stable address after preview cancellation or expiry.
Public requests return 404 for unpublished pages, preview/delivery tokens, unknown tokens, or deleted source content.
Historical published records retain anonymous access at their existing addresses.
Preview routes require the same authenticated sender who prepared the draft.

On confirmation, recheck ownership, source visibility, snapshot equality, and destination identity.
Fetch the selected original under the existing 10 MiB attachment limit.
Claim the exact result in the durable Vibecoders send store.
Then publish the stable page and persist the separate pending delivery record before calling Discord.
Success marks the delivery record and the result claim sent.
A lost response leaves the page public and delivery pending.
The UI reports uncertainty and blocks repeat sends.
Failures before first publication keep the reserved page private and release any unused claim.
An operator must check Discord before reconciling uncertain delivery records.

One process-wide operation slot bounds preparation and sending.
Public page records and prompt-to-page references load individually from disk, without a permanent in-memory share index.
Asset allowlists cap at 128 entries; additions cap at 64; returned text entries cap at 128.
Existing sent records retain their historical sent status.

## One page per prompt instance — 2026-09-20

All new shares from the same completed job use one stable public page.
Different jobs retain separate pages, even when their prompt text matches.
Each image still receives its own Discord message and confirmation.
When the page is already public, the preview states this and uses **Send to thread** for confirmation.
Daily thread routing and duplicate-image protection remain unchanged.

Preparation reserves the page address without publishing it.
Each preview has a separate random token, sender, attachment identity, destination, snapshot, and expiry.
Concurrent previews share the same reserved page, regardless of their confirmation order.
Cancelling a later preview does not revoke an already published page.
An uncertain delivery blocks that exact image; another image can still use the same page.

The first confirmed share publishes every successful output in the recorded run, including images not individually posted to Discord.
Subsequent shares must match that exact snapshot. Reject changes instead of expanding or replacing the public content.
Deleting the prompt or any result disables the page and its assets.

**View prompt** links end in `#output-<asset-slot>`.
The browser scrolls to that output and shows a border and **Selected output** label.
Each output provides **View prompt** and **All outputs** navigation.
The page also links to the contact sheet and states the output count.
Reserved preview dimensions prevent earlier image loads from moving the selected output.
Images retain thumbnail previews and original-file links. No scripts or account are required for reading.
**Make your own** retains the unfragmented page's `/reuse` route and existing login requirements.
The confirmation's complete-page preview uses the same selected-output fragment.

`UiPublicShares/prompts/<sha256-job-id>.json` stores the exact job-to-page reference.
Page records use `IsPage=true` and transition from `draft` to `public`.
Preview/delivery records use `PageToken` to identify their stable page.
Their `Token` identifies consent and delivery only; it never becomes another anonymous page.
Persist page records before their references. Preserve unpublished reservations when pruning expired previews.
Existing references must resolve exactly; missing records or conflicting snapshots stop sharing.

For an existing job, adopt its earliest historical publication with the same exact snapshot.
Break equal creation timestamps by token order. Reject conflicting historical snapshots for that job.
Keep every historical public URL working without editing Discord messages.
Require a new preview for unconfirmed records created before this change.

Verify with `PublicShareTests`, `DiscordVibecodersTests`, and `DiscordDailyThreadTests`.
Set `MIC_PUBLIC_SHARE_HTML_FIXTURE` to a temporary HTML path while running the C# tests.
Then run `tools/test-public-share-page.cjs` with the same variable for desktop and mobile browser checks.
Run `tools/test-public-share-dialog.cjs` for caption, confirmation, cancellation, and uncertain-delivery checks.

## Discord payload

Resolve the webhook's `guild_id` and `channel_id` during preparation and immediately before dispatch.
Reject a changed destination or configuration.
Resolve server/channel names with the configured bot and verify them again on confirmation.
The former manual display-name settings are superseded.
Webhook URLs with query/thread overrides cannot use this flow.
Attach the original bytes, disable mentions, and suppress link embeds.
Use `wait=true&thread_id=<daily-thread-id>`, then verify the returned thread, caption, and attachment count.
Never substitute a URL for an unavailable or oversized attachment.

Source: Discord's [webhook API](https://github.com/discord/discord-api-docs/blob/main/developers/resources/webhook.mdx), checked 2026-09-17.

## Daily image threads — 2026-09-17

Every confirmed Vibecoders image or video goes inside one daily public thread under the configured text channel.
The first image is also inside that thread. Do not attach a copy in the parent channel.
Discord may display its normal thread-creation notice in the parent channel.
Create nothing on startup, at midnight, on preview, on cancellation, or on days without confirmed sends.
A confirmed first send creates the thread immediately before publication and attachment dispatch.

Use the IANA time zone `America/Los_Angeles`, including daylight-saving changes.
The key is the local calendar date, not a rolling 24-hour interval or the server's time zone.
Example name: **Daily Thursday, September 17, 2026 image thread**.
Show that exact name and “Pacific time” in the confirmation destination.
Reject a preview that crosses Pacific midnight. Require a new preview for the new day.
Check the date again after thread preparation, before publishing.
Midnight changes routing for new app posts. It does not prohibit people from discussing older threads.
Request automatic archival after 1,440 minutes of inactivity; a same-day webhook send can reopen an unlocked archived thread.

The webhook posts attachments, but a bot token is required to create ordinary text-channel threads.
Set `DiscordVibecodersBotToken` securely in each selected instance's settings.
Give that bot access to the guild/channel and permission to create public threads.
Grant thread messaging permissions if that bot will also participate in the threads.
This token is separate from the optional FableBot posting action and is never returned by configuration APIs.
Bot lookups supply verified server/channel names; the user need not enter these names manually.
Reject forum, announcement, or non-text parent channels in this version.

All instances targeting the same Discord channel must use the **same** `DiscordVibecodersThreadStorePath`.
Provision a shared directory and group accessible to those service accounts.
The installer uses `/var/lib/multiimageclient-discord-threads`, a shared service group, and per-service writable-directory overrides.
Registry and lease files explicitly grant group read/write access despite restrictive service umasks.
Add that exact directory to each selected systemd service's writable paths before enabling this feature.
Do not copy thread records into separate instance stores.
Records use guild/channel/date keys. File leases serialize creation across processes.
Persist a pending creation record before calling Discord, and persist the exact returned thread ID afterward.
Validate the guild, parent, name, type, ID, and unlocked state before reusing a thread.
Never choose a similarly named thread or post to the parent channel after an error.

An uncertain creation result leaves a pending record and blocks another creation attempt for that day.
An operator must reconcile its exact Discord identity before unblocking it.
This favors zero duplicate threads over automatic recovery from uncertain network failures.
A failed first send can leave an empty thread because creation and message posting are separate Discord operations.
No retry or replacement thread is created automatically.

Sources: Discord's [thread creation API](https://github.com/discord/discord-api-docs/blob/main/developers/resources/channel.mdx#start-thread-without-message)
and [webhook thread routing](https://github.com/discord/discord-api-docs/blob/main/developers/resources/webhook.mdx#execute-webhook).

## Production rollout correction — 2026-09-17

The original service received the confirmation release, but the Vibecoders instance still ran older direct-send code.
A 19:57 UTC send record appeared in the Vibecoders instance's store.
The original instance's latest record remained September 9.
Checking only the original service did not verify the user's exercised production action.
For this correction, update both original and Vibecoders app instances and inspect each deployed send implementation.
Preserve each instance's settings, accounts, history, and routes.
Do not report public sharing operational until its public route, bot access, and shared thread directory are configured.
Do not send live test images as part of release verification.
The owner subsequently approved the Vibecoders service update; both instances now have the confirmation implementation.
Browser checks exercised both deployed clients while intercepting all writes locally.
The old SocialAI credential returned HTTP 401. The owner replaced it on September 18.
The production image sender uses a working webhook credential, which cannot create ordinary text-channel threads.
Discord permits webhook-created threads only in forum/media channels. Keep the requested text channel and use the verified bot credential.

## Public routing and login

Configure these instance settings:

```json
{
  "UiPublicShareBaseUrl": "https://multiimageclient.alpha.fuseki.net/shared/original",
  "DiscordVibecodersBotToken": "<bot token; secret>",
  "DiscordVibecodersThreadStorePath": "<absolute shared writable directory>"
}
```

The public base must use HTTPS and `/shared/<environment-name>` outside the private prefix.
Do not derive public links from `UiPublicBaseUrl`; that setting remains the private authenticated destination.
The original production proxy maps `/shared/original/` to loopback `/public/` on port 5960.
The hostname, TLS configuration, private prefix, and neighboring service routes remain unchanged.

`deploy/install-public-sharing.py --environment original` configures the original route.
Use `--environment vibecoders-ai-generation` for the separately approved Vibecoders instance.
Both instances use the same thread registry. Public media and job stores remain separate.
Route/storage provisioning does not require a bot token and publishes no existing content.
Actual sharing still rejects missing or invalid bot credentials.
It preserves the existing vhost, creates private backups, checks nginx, and reloads nginx.
It never restarts neighboring services.
Install the route/settings and then use the normal `deploy/agent-redeploy.sh` release procedure.
Only these two approved instances are accepted by the installer.

Public sharing GET routes allow only the page, listed asset slots, and reuse handoff.
The sharing POST route accepts only login for that exact published share.
The separate signup routes below also use the explicit public allowlist.
Require same-origin login forms, existing credential validation/throttling, and environment membership.
Return the private composer address only after successful authentication and membership checks.
The composer receives a share token, resolves its published prompt and inputs through authenticated routes, and removes the query.
Loading a shared prompt does not generate anything automatically.
The recipient can edit it and choose their own endpoints before submitting.
Their existing endpoint/global additions remain their personal settings.

### Account requests from public images (2026-09-20)

Follow [the Discord account contract](workspaces-prd.md#discord-account-requests-2026-09-20).
The optional **Request account** action accepts an exact Discord username without an authorization screen.
The existing bot checks that member's access to #vibecoders and sends that account a one-use, 30-minute link.
The recipient confirms account creation or login on a public page.
The form sender receives no login link or session.
New accounts belong only to Vibecoders AI Generation.
Preserve the selected public prompt when signup began from that environment.
Original-environment public pages instead lead to the Vibecoders composer without granting original membership.
Reading published prompts remains anonymous.

The owner-authorized exception permits one-use account tokens only in these verified-recipient DMs.
Never put them in channel posts, image captions, reusable account links, public HTML, or application logs.
Private site prefixes remain prohibited everywhere in Discord.
The setting defaults to off and requires compatible code in both approved instances before activation.

## API

| Method and route | Contract |
|---|---|
| `POST /api/discord/vibecoders/prepare` | Exact job/generator/image index; returns preview token, stable `publicUrl`, selected `viewUrl`, `pagePublished`, and presentation data. Requires `X-MIC-Share: 1`. |
| `GET /api/discord/vibecoders/preview/{token}/` | Same publisher's HTML preview. |
| `GET /api/discord/vibecoders/preview/{token}/asset/{slot}` | Exact preview asset. |
| `POST /api/discord/vibecoders` | Requires preview token, `confirmed=true`, and `X-MIC-Share: 1`. Old direct-send requests fail closed. |
| `GET /public/{token}/` | Anonymous stable published page, with optional browser-only `#output-<asset-slot>` selection. |
| `GET /public/{token}/asset/{slot}` | Anonymous allowlisted original or `?thumb=1` preview. |
| `GET /public/{token}/reuse` | Authenticated composer redirect or compact login form. |
| `POST /public/{token}/reuse` | Validate login and membership, then redirect to the composer. |
| `GET /public/signup`, `GET /public/signup/claim` | Original controller's optional account-request and explicit-confirmation forms. |
| `POST /public/signup/request`, `POST /public/signup/claim` | Limited DM delivery and one-use account redemption. See the global-account contract. |
| `GET /api/public-shares/{token}/reuse` | Authenticated prompt and exact input URLs. |
| `GET /api/public-shares/{token}/asset/{slot}` | Authenticated reuse input access. |

## Files and verification

- `Implementation/UiPublicShares.cs`: snapshot, disk store, URL policy, HTML, and caption.
- `Workflows/UiWorkflow.PublicShares.cs`: preparation, consent, publication, assets, and login handoff.
- `Workflows/UiWorkflow.cs`: narrow anonymous exception and route registration.
- `Implementation/UiDiscordAccountEndpoints.cs`, `UiDiscordAccountRequests.cs`, and `DiscordAccountRequests.cs`: optional public signup through verified-recipient DMs.
- `Implementation/DiscordDailyThreads.cs`: Pacific calendar dates, shared file leases, and durable thread identities.
- `MultiImageClient.Tests/DiscordDailyThreadTests.cs`: midnight, daylight saving, concurrency, restarts, and uncertain creation.
- `Implementation/DiscordVibecoders.cs`: destination lookup and compact linked payload.
- `Implementation/UiDiscordVibecodersStore.cs`: pending/sent records.
- `Ui/wwwroot/public-share-dialog.js`, `index.html`, `style.css`, `app.js`: confirmation and composer reuse.
- `ImageGenerationClasses/Settings.cs`: public base, bot token, and shared thread-store location.
- `deploy/install-public-sharing.py`: explicit original-environment public route migration.
- `MultiImageClient.Tests/PublicShareTests.cs`: privacy, URL rejection, unpublished drafts, confirmation, successful and uncertain delivery, login, and membership revocation.
- `tools/test-public-share-dialog.cjs`: visual preview, cancellation, exact confirmation, and blocked retries.
- `tools/test-public-share-page.cjs`: actual C# HTML, selected output, delayed images, prompt navigation, and desktop/mobile layout.

Live verification must not post to Discord or publish a real private prompt without a separate explicit posting instruction.

## Target selection and activation — 2026-09-18

Both production instances now have the renewed SocialAI bot credential.
Read-only checks verified access to Ernie server and #vibecoders from both instances.

Each environment has a saved **Target** selector in Administration beside **Send to Vibecoders**.
Choose **Vibecoders** or **Bot testing**. Existing environments default to Vibecoders.
The standalone local instance exposes **Settings → Discord Target** and stores its selection on disk.
Local sharing still requires a configured public HTTPS address and bot credentials.

Bot testing targets the private #mic-bot-testing channel (1550532551846662165) in Ernie server (675090558867472424).
Only Ernie and social-ai were explicitly allowed. Discord administrators retain access.
Configure DiscordBotTestingGuildId and DiscordBotTestingChannelId in each participating instance.
Testing uploads use bot authentication; Vibecoders uploads retain the existing webhook.
Missing test configuration fails without sending to another channel.
Changing Target invalidates earlier previews. Every confirmation displays the exact server, channel, and daily thread.
The same public-page disclosure applies to testing. A private Discord channel does not make the published page private.
Daily records include the parent channel identity, so each target has its own California-date thread.
Duplicate-result protection remains per instance across targets; selecting another target does not clear uncertain or completed sends.

The owner approved View Channels, Send Messages, Create Public Threads, Send Messages in Threads, and Manage Threads.
The owner also approved Attach Files, Embed Links, Read Message History, and Add Reactions.
Administrator, moderation, role management, webhook management, and Mention Everyone were not requested.

A live test used synthetic content in the private test channel only.
The application's daily registry created one thread and reused it for a second message.
An 800×480 test card subsequently verified a visible image attachment.
No private prompt was published during these checks.
