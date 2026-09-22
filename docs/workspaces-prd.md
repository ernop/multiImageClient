# Separate workspaces and personal login links

Date: 2026-09-08, America/Los_Angeles.
Updated: 2026-09-21, America/Los_Angeles.
Status: activated in production on 2026-09-09; both environments verified through the public hostname.

A workspace is one isolated tenant, also called an image-making studio.
The current production site is the first tenant.
The new idea is a second tenant with its own membership and history.

## Confirmed requirements

| ID | Requirement | Behavior |
|---|---|---|
| R1 | Preserve the existing group. | Keep Ernie, Austin, Victor, their accounts, history, images, and user configurations working in the existing environment. |
| R2 | Add another tenant. | Start the new environment with separate accounts, membership, prompts, images, history, and user configurations. |
| R3 | Let the owner access both. | Use the existing owner account across both groups without merging their history. |
| R4 | Send personal login links. | Opening a recipient's link selects their workspace, sets their username, and signs them in. |
| R5 | Treat possession as identity. | Anyone holding the link can act as its assigned person. |
| R6 | Require no credential explanation. | The recipient only needs to click their personal link. |
| R7 | Start with entirely new accounts and data. | Share global identity only; keep history, images, and personal configurations separate. |
| R8 | Match repeated provider lists (2026-09-10). | Use the composer's compact provider controls and grid in environment administration. |
| R9 | Reissue login details for an existing normal account (2026-09-19). | Administration **Issue login details** creates a new password and a new reusable login link, shows both once, and ends the previous password and link. Stored hashes cannot be recovered. |
| R10 | Convert a password-file account to a login-link account (2026-09-20). | Administration **Convert to login-link account** creates a chosen username as the sendable login, mints a new password and reusable login link, shows both once, retires the previous password-file username, and moves that person's membership and stored creator identity to the new login. Production mappings: `victor` → `governorOfThings`, `austin` → `dallasVille`. The previous password cannot be recovered. After conversion, **Issue login details** remains available. |
| R11 | Request an account through Discord (2026-09-20). | Enter an exact Discord username and verify #vibecoders access. Queue the request for owner review. **Test** sends Brouhahaha a harmless copy. **Confirmed, send to user** sends the recipient a reusable link with no expiry (updated 2026-09-21). Recipient confirmation creates a normal Vibecoders member. |
| R12 | Keep login links reusable without expiry (2026-09-21). | Administration reset links already have no time limit. Discord DM links also support repeated logins without expiry. Preserve owner review, exact account identity, credential replacement, revocation, and membership checks. |
| R13 | Separate account creation from issued credentials (2026-09-21). | Start creation with an empty new username and no destination selection. Show creation, conversion, and reissue results in a separate section naming the account, action, and environment. |

These requirements came from the owner during the review.
The link grants the account's actual permissions; it does not merely select a display name.
The intended recipient instruction is: "Click this link to log in."

The owner clarified that the new environment starts independently of the existing group's data.
Ernie can send personal login links to bring other people into that new environment.
Ernie uses one global account. His personal configurations remain scoped to each environment.
The existing environment continues working at its current address.
Sharing application code does not imply sharing user data.
Provider API credentials can be copied into independent settings. Consumer sessions require explicit provisioning opt-in.

The repository search found no earlier workspace design document.
Available recent task summaries did not establish a prior agreement.
This does not establish that earlier conversations never occurred.

## Project map

| Area | Responsibility | Workspace impact |
|---|---|---|
| `MultiImageClient/Program.cs` | Loads settings and selects the run mode. | Initializes one process-wide generation archive. |
| `Workflows/UiWorkflow.cs` | Hosts the production HTTP application. | Constructs one shared collection of stores and routes. |
| `Implementation/UiJobs.cs` | Runs generators and stores job history. | Uses one history root and one event stream. |
| `Implementation/UiTargetScheduler.cs` | Limits provider requests. | Limits apply within one process. |
| `Implementation/UiAuth.cs` | Validates accounts and login cookies. | Has no workspace membership contract. |
| `Implementation/UiCommunity.cs` | Stores profiles, preferences, requests, and prompt exchanges. | Uses one SQLite database. |
| `Implementation/UiGoalLoops.cs` | Runs and persists manager/critic image loops. | Uses one goal-loop root. |
| `Implementation/UiFavorites.cs` and `UiVisibility.cs` | Store favorites and deletion state. | Must follow the same workspace boundary as jobs. |
| `Implementation/GenerationArchive.cs` | Records provider attempts and artifact identities. | Has a static database connection configuration. |
| `Ui/wwwroot/` | Provides composer, viewers, goals, and recaps. | Stores several preferences under browser-wide origin keys. |
| `ImageGenerators/`, provider API projects | Adapt requests to image providers. | Can reuse existing generation behavior. |
| `Describers/`, `TextLLMs/`, prompt transformations | Describe images and transform prompts. | Histories and diagnostics need the workspace boundary. |
| CLI workflows | Support batch, roundtrip, showcase, and REPL use. | Must retain current local behavior. |
| `MultiImageClient.Web/` | Hosts the separate Workflow Lab on port 5127. | This is not the production `--ui` application. |
| `FableBot/` | Posts through a Discord bot account. | Remains separate from workspace login. |
| `djangoManager/` and `tools/` | Provide gallery experiments and standalone utilities. | Do not replace the production deployment. |

Goal loops already support generators, a manager, independent critics, and visual recaps.
Ideation Mode remains proposed in its canonical document.

## Verified production deployment

The product hostname is `multiimageclient.alpha.fuseki.net`.
Its private path must never appear in this document or diagnostic output.
The physical host is the SSH target `tpbeta-root`.

| Item | Verified location or value |
|---|---|
| Server checkout | `/home/tparkour/multiImageClient` |
| Checkout owner | `tparkour` |
| Published application | `/opt/multiimageclient` |
| Runtime account | `multiimageclient` |
| Service | `multiimageclient-ui.service` |
| Settings | `/etc/multiimageclient/settings.json` |
| Authentication | `/etc/multiimageclient/ui-auth.json` |
| Data root | `/var/lib/multiimageclient/saves` |
| Generation archive | `/var/lib/multiimageclient/saves/generation-history.sqlite3` |
| Listener | `127.0.0.1:5960` |
| Memory limits | `MemoryHigh=2048M`, `MemoryMax=2560M` |
| Work limits | One finalizer, 14 aggregate provider requests, default pending queue limit |
| Disk reserve | 3 GiB |
| B2 storage | Enabled; verified uploads permit eviction of local originals |

The normal release command is:

```sh
ssh tpbeta-root \
  'sudo -u tparkour -H bash /home/tparkour/multiImageClient/deploy/agent-redeploy.sh'
```

The script fast-forwards the checkout and publishes a Linux build into the owner's staging directory.
The privileged helper copies that build into `/opt/multiimageclient` and restarts the existing service.
Routine releases do not change nginx, TLS, the hostname, or the private path.
Follow [deploy/README.md](../deploy/README.md) for the full release gate and local restart requirements.

Adding a second service requires an explicit deployment extension.
The current release helper cannot deploy a separately named instance.

### Live observations during this review

The initial checkout commit was `c53274969fa74dc38bfe2064bc28de520ddb8a73`.
Another release occurred while this review was running.
The later checkout commit was `b3e126977ee5323a21ab934868da8d3b9a8406b6`.
The service started at 2026-09-09 05:11:13 UTC, or September 8 at 22:11:13 Pacific.
This review did not deploy that release.

The authentication file changed from the old password format to version 2 hashes.
The owner's `ernieMultiZone` account remained present.
A private comparison against the migration backup proved that its password was preserved.
An owner login through the public site returned HTTP 200.
The resulting authenticated configuration request also returned HTTP 200.
The migration invalidated previously issued cookies.
The owner subsequently confirmed that login worked again.
No credentials or cookies were printed during those checks.

The host reported 3915 MiB total memory and no swap.
The filesystem reported approximately 3.8 GiB available, with 96% used.
These are point-in-time observations, not capacity guarantees.

## Settled design — 2026-09-09

The owner replaced the independent-owner-account proposal with one global account system.
There are exactly two permission levels: admin and normal.
The existing `ernieMultiZone` account is the sole global administrator.
Its existing password and cookie signing secret remain unchanged.
No separate environment administrator role exists.
Normal accounts require explicit environment membership.
The owner assigns membership, with the narrow Discord signup exception below.
A normal account can belong to multiple environments without merging their data.

The first additional environment is **Vibecoders AI Generation**.
Its chosen URL name is `vibecoders-ai-generation`.
The display name supplies the browser page title and appears in the site header.
The administrator can edit display names, membership, default providers, and feature switches.
The administrator can choose and change additional environments' URL names.
The original private URL remains fixed and must never be printed or committed.

| Level | Access |
|---|---|
| Admin | Every environment, global configuration, membership, account creation, activity summaries, and application logs. |
| Normal | Assigned environments, shared images/history/activity, enabled generation features, and personal preferences. |

Normal members cannot access server configuration, raw application logs, administration endpoints, or unassigned environments.
The former browser-only settings button is labelled preferences in managed environments.
Everyone in an environment can see its shared activity.
The owner creates accounts and distributes reusable personal links.
The optional Discord request flow below permits normal account creation for eligible Vibecoders members.
Members cannot create invitation links or grant environment membership.

## Discord account requests (2026-09-20)

The owner requested signup without Discord's browser authorization screen, commonly called OAuth.
An entered username selects a recipient; receiving and using the DM proves control of that account.
The requesting browser receives neither a login link nor an authenticated session.

1. Open a public image page and select **Request account**.
2. Enter the exact Discord username. An initial `@` and uppercase letters are accepted.
3. Select **Request account**. The page shows that Ernie must review the request.
4. Wait for the owner to test and confirm delivery.
5. Open the bot's DM and follow **Continue to Vibecoders**.
6. Select **Continue to Vibecoders** on the confirmation page.

The owner revised delivery on September 20: public requests no longer send DMs automatically.
Open **Administration → Discord account requests** to review pending recipients.
Select **Test** to send a copy only to Brouhahaha's verified permanent Discord ID.
Configure that ID with `DiscordAccountReviewerId` on the original controller.
The copy names the intended recipient and includes the real message body with a harmless public preview link.
That preview cannot create or sign in to any account.
No usable signup secret exists before the owner confirms delivery.
After checking the DM, select **Confirmed, send to user** for that exact request.
The server requires a successful test within the previous 30 minutes, sent to the currently configured reviewer.
The server rechecks the recipient's ID, username, server, and channel access before either send.
The final action creates a reusable secret with no expiry (updated September 21).
A request expires after 24 hours without confirmation.
A failed test remains retryable only after Discord explicitly rejects delivery.
Uncertain test delivery blocks confirmation and further tests for that request.
**Reject request** closes requests that have not reached actual account-link delivery.
The owner list shows at most 100 requests, with unfinished requests first.
Issued links display no expiry; completed activations leave the review list.
Loading or refreshing administration sends no Discord message.
A lost action response forces a state refresh before controls become usable again.
Only the authenticated owner can use these endpoints; each mutation requires `X-Mic-Manage: 1`.

The native form previously combined `no-referrer` with an exact Origin check.
Browsers can send `Origin: null` for that combination, producing an empty denial page.
The request form now uses JavaScript `fetch` with `mode: cors` and `X-Mic-Account: 1`.
This preserves the same-origin check and keeps error messages inside the form.
Keep `no-referrer`; never weaken the origin check to accept `null`.
A GET to the old `/signup/request` error address returns to the signup form without sending anything.
See the [Fetch Origin header algorithm](https://fetch.spec.whatwg.org/#append-a-request-origin-header).

The bot searches the server attached to the real Vibecoders webhook.
The image-sharing **Target** selector does not change signup eligibility.
Require one exact username match; reject display names, nicknames, incomplete results, and ambiguous matches.
Re-fetch the exact member and verify effective channel-view permission, including role and member overrides.
Reject bots and members with incomplete screening.
Check the same Discord account, server, and channel again when redeeming the link.
These checks do not continuously synchronize existing site memberships with Discord.

Use the existing `DiscordVibecodersBotToken` for the DM.
Validate the returned DM channel and its sole recipient before sending the link.
Disable mentions and link embeds.
Blocked DMs show a delivery error with message-privacy guidance.
Uncertain delivery remains pending; never retry automatically or return its token through the website.

Each issued link supports repeated logins without expiry (September 21 supersedes the previous 30-minute, one-use rule).
The secret appears in the URL fragment, which the browser removes immediately.
Opening the page does not sign in; explicit confirmation sends a same-origin POST.
Pages use no external assets, prohibit caching, and send no referrer.
Only the claim endpoint returns the existing secure, HTTP-only global session cookie.
The link remains usable after the browser loses its session.

Create only a normal account in the exact `vibecoders-ai-generation` environment.
Never grant original-environment membership or administrator access.
Use the verified Discord username as the initial site login and display name.
Bind the account to the permanent Discord user ID.
Later Discord username changes retain the same site account and login.
A matching existing site name never proves ownership; reject that collision and direct the person to Ernie.
New accounts receive no generated password or reusable login link through this flow.
The owner can still use **Issue login details** afterward.
An existing Discord-bound account can request another reusable login link without resetting its other credentials.
A new request or DM does not invalidate earlier links.
Revoked accounts and removed Vibecoders memberships remain blocked.

A request from a Vibecoders public page preserves its exact prompt for the composer after login.
A request from an original-environment public page opens the Vibecoders composer without importing private-environment data.
Anonymous visitors can continue reading either published subset without an account.

Only the original controller writes shared accounts, memberships, and signup tickets.
All public image pages direct signup to `/shared/original/signup` on the configured public origin.
This uses the existing public proxy route and exposes no private prefix.
Persist tickets under the controller's data root at `UiDiscordAccountRequests/requests.json`.
Store token digests, recipient IDs, review deadlines, source-share tokens, delivery state, and activation progress.
Issued links use `expiresAt: 0`; older issued timestamps remain readable but no longer impose an expiry.
The review API returns `expiresAt: null` for issued links.
Also store the reviewer ID, successful test time, and owner confirmation time.
Keep older delivered tickets readable; new requests always require owner review.
Never store the raw token or client IP.
Flush state before sending and before returning a session; preserve exact account identity through interrupted activation.
The stored `used` state records completed activation; it permits later logins without creating another account.
An interrupted membership grant never restores membership after removal.
An interrupted grant that cannot be verified requires owner intervention.

Persist limits before searching Discord: 12 requests per IP per hour and 60 requests overall per hour.
Hash IP addresses and discard attempts older than one hour when processing the next request.
Allow at most three tickets per Discord account per hour, separated by five minutes.
An unexpired review or test blocks another request.
Uncertain delivery and unfinished activation also block another request.
A confirmed send blocks duplicate requests for five minutes; later requests still require owner Test and confirmation.
Rejected and explicitly failed real deliveries remain subject to the rate limits.
Prune unsuccessful or abandoned request history 24 hours after its deadline when processing the next request.
Never prune issued links, including completed activations, because of their age.
Cap storage at 2 MiB and 2,048 records.
Reject new requests when storage is full; never evict an issued link to admit another request.
Permit one request, test, confirmation, rejection, or redemption at a time on the controller.
Trust forwarded client addresses only from loopback; use nginx's appended final address.

Administration exposes **Discord account requests** only for Vibecoders AI Generation.
The switch defaults to off; the server rejects enabling it for any other environment.
Disabling it removes public request links and rejects requests, tests, confirmations, rejections, and redemptions.
Existing accounts retain their explicit site memberships.
Deploy compatible code to both approved instances before enabling the switch or creating Discord-bound accounts.
Older versions reject the new shared-store fields. See [the release instructions](../deploy/README.md#discord-account-request-activation-2026-09-20).

Read-only production checks passed on September 20 for bot authentication, member search, server data, and #vibecoders data.
These checks sent no DM and created no live account.
All 501 application tests passed after adding owner review, including 44 Discord account-request cases.
The September 20 review tests covered owner-only endpoints, exact recipients, expired tests, lost responses, and one-use redemption.
The September 21 tests replace the one-use expectation with delayed use and repeated login.
Browser checks verify Origin headers under no-referrer, inline errors, and explicit owner actions at desktop and mobile widths.
The owner authorized production activation on September 20.
Release both the original controller and Vibecoders instance, then enable **Discord account requests** for Vibecoders.
Verify public forms and existing account access without sending a test DM.

Discord references: [member search](https://docs.discord.com/developers/resources/guild#search-guild-members),
[permission calculation](https://docs.discord.com/developers/topics/permissions), and
[DM creation](https://docs.discord.com/developers/resources/user#create-dm).

## Login-link audit and lifetime decision (2026-09-21)

The owner identified the reset link issued for Victor as the original review target.
**Issue login details** already creates a reusable administration link without a time limit.
The owner also requested reusable Discord DM links without expiry.

Administration links stop working after **New link**, **Issue login details**, or account revocation.
Removing environment membership blocks entry to that environment.
Credentials appear once because the server stores hashes; that display rule does not limit link reuse.
Send the complete original link, including its fragment after `#`.
The landing page removes that fragment from the address bar after reading it.
Copying the address bar afterward does not produce a usable login link.
The session cookie requests 3,650 days; losing that cookie does not expire the original login link.

Discord links recheck the exact Discord ID and channel access on every login.
They also require the same site account and current environment membership.
Completed activations never recreate missing accounts or restore removed membership.
Issued links bind to the account's credential digest through `accountTokenHash`.
For existing accounts, confirmed delivery records this binding before sending.
For new accounts, activation records each outstanding link binding before issuing a session.
Replacing administration credentials invalidates bound Discord links as well as the administration link.

Retained historical Discord tickets become reusable, including previously used tickets.
Controller startup binds retained tickets for known Discord accounts to their current credential digest.
Account creation binds all outstanding tickets for that same permanent Discord ID before issuing a session.
Already deleted historical tickets cannot be recovered from their links.
The upgrade does not recreate tickets or infer identities from usernames.
The owner-test deadline remains 30 minutes; the unconfirmed request deadline remains 24 hours.
Neither deadline applies to an issued login link.

The controller ticket format adds `accountTokenHash` and permits zero expiry for issued links.
Older controller builds reject these records; preserve them during rollback planning.
No global account-file or environment-registry format changes are required.
All 510 application tests passed after this change.
Desktop and mobile browser checks passed with mocked Discord delivery.
These checks sent no Discord messages.
Live checks of the owner's supplied Victor link found a valid token and original-environment membership.
The login endpoint returned HTTP 500 before setting a session cookie.
Its separate legacy account requested the display name `victor`.
That name belongs to the existing `governorOfThings` identity through a reserved alias.
The server raised `UiProfileNameConflictException`; time expiry did not cause the failure.
The existing account remains in the password-account file and belongs to both environments.
The duplicate link account belongs only to the original environment.
No live credentials, memberships, aliases, or account identities were changed during this audit.
A later owner-supplied administration link passed two consecutive logins using separate, empty cookie jars.
Both logins returned HTTP 200 and permitted authenticated configuration access.
This verified repeated use of that exact link without replacing its credentials.

Use **Convert to login-link account** on `governorOfThings`, preserving that username, to issue credentials for Victor's existing identity.
Do not issue another link from the duplicate legacy account's row.
The conversion route preserves the account's existing membership and stored identity.
A profile-name conflict now returns HTTP 409 with instructions to obtain a link from the existing account.
It never sets a cookie, takes another account's alias, or silently selects another identity.
Account creation normalizes usernames before checking password accounts, preventing surrounding spaces from bypassing duplicate checks.
The two-server browser regression passed after recreating the conflict.
It verified the visible error, absent login cookie, unchanged original account, and successful account conversion.
The test accepts `MIC_UI_TEST_BINARY` for a selected build and `MIC_UI_TEST_BROWSER_CHANNEL=bundled` for bundled Chromium.

This change is prepared locally; production deployment remains separate.

## Data and authentication boundaries

Each environment uses a separate application process, Linux account, data root, community database, and generation archive.
The new environment imports no old jobs, images, profiles, favorites, prompt histories, or personal configurations.
Shared authentication is the explicit exception to the earlier entirely-separate-accounts requirement.
Authentication proves identity. Each request separately checks membership in the receiving environment.
The owner has global access through the existing authenticated account identity.
Display names cannot grant administrator access.

Managed instances share one canonical password-account file and one reusable-link account file.
These files represent the same global identities, not independent per-environment accounts.
New normal accounts support both an automatically generated password and a reusable personal link.
The chosen username is the sendable login. It is not a `member-` identifier.
Passwords use PBKDF2-SHA256. Personal links store only SHA-256 token digests.
The owner receives new credentials once. Stored password hashes cannot be recovered.
**Issue login details** creates a new password and a new reusable login link and shows both once.
That action ends the previous password and the previous login link.
**New link** still replaces only the reusable link. The existing password remains valid for a later login.
**Convert to login-link account** moves a remaining password-file account onto that same login-link identity.
The owner types the sendable username, for example `governorOfThings` or `dallasVille`.
Conversion retires the previous password-file username immediately.
Membership, job creator login, goal-loop creator login, favorites, activity, and community records that used the previous login move to the new login.
Historical `CreatedBy` display text stays as stored. A profile can change later display.
The application still does not write the password-account file.
The retired names stay ignored for login and cookies until the privileged reconciler removes those rows from the canonical auth file.

Managed instances use the existing root-path `mic_auth` cookie.
A login therefore works across assigned environments without another credential prompt.
Opening another person's link intentionally changes the global browser identity.
Requests from stale tabs carry the previous session marker and are rejected before mutation.
Environment membership removal takes effect on the next request, including an existing session.
Revoking a new account invalidates both its password access and personal-link sessions across environments.
Replacing its link invalidates previous link sessions; its existing password remains valid.
Removing all memberships blocks an existing password account from all normal environments.
The sole owner account cannot be revoked or demoted through account-management controls.

New environments scope browser storage by environment ID and login.
The original environment retains its existing browser-storage keys and preferences.
Same-hostname paths are not independent browser security origins.
Separate server stores and membership checks provide the application data boundary.

Provider credentials can be reused through a provider-only configuration copy.
Default provider selections remain independently configurable for each environment.
Existing personal choices are not overwritten when defaults change.
B2 original images retain the established public capability-URL policy.
A person holding a direct original-image URL can retrieve it independently of application membership.
No old image URLs or image indexes are copied to the new environment.

## Feature switches and activity summaries

Each environment has switches for goal loops, video generation, prompt rewriting, Send to Vibecoders, and night filter.
Disabled controls disappear from the page.
The server also rejects corresponding direct API requests and goal/recap page routes.
Disabling a feature does not delete existing work or cancel an already accepted provider request.
Complete or stop active goal loops before disabling their controls.

The administration page lists every registered environment and its current membership.
It displays recorded first login, last login, last activity, and submitted-job counts per member.
Login tracking starts with this release. Earlier login timestamps remain unknown.
Activity records page visits and successful authenticated mutations, with at most one routine write per minute per account.
Submitted-job counts use the existing lightweight history index without loading image bytes or full job graphs.
Counts include failed submissions; they do not claim a count of successful images.
Unattributed legacy work is not assigned to a guessed account.
The activity record is bounded to 501 identities and a 1 MiB file.

## Shared provider presentation (2026-09-10)

Environment administration uses the same provider controls as the composer and goal setup.
Each control places the checkbox before the catalog name and retains the image-capability icon and provider tooltip.
Selected controls use the same border and background colors.
All three pages share the grid's 175 px minimum columns and 7 px gaps.
The grid adds columns when space permits and wraps on narrow screens.

`generator-toggle.js` owns provider markup and checkbox feedback.
`style.css` owns the shared controls and grid layout.
Administration keeps its existing available-provider catalog, catalog order, environment defaults, and save contract.
Each environment owns its selection independently of personal composer preferences.
Only **Save configuration** persists edited defaults.

Lists of the same items must reuse their existing presentation across pages.
Keep page-specific selection and persistence separate from shared presentation.
This prevents repeated lists from acquiring conflicting styles or unnecessary vertical spacing.
The binding rule also appears in `AGENTS.md` under **Visual & Typography Policy**.

Validation passed for the existing composer and goal-picker browser suite.
A temporary browser fixture verified matching normal, selected, and hover styles across 36 locally available providers.
It also verified keyboard selection, separate environment selections, save payloads, and desktop/mobile layouts.
The fixture intercepted administration saves; it changed no live environment configuration.

## Account creation and credential results (2026-09-21)

The owner reported existing-account values under **Create a normal account**.
The application did not set the owner username as a creation default.
The screenshot's highlighted owner username appears consistent with browser autofill.
The page did place all issued credentials inside the creation section, including results from existing-account actions.

Start **Create a normal account** with an empty **New username** field.
Use a distinct creation-field name and request `autocomplete="off"` on the form and input.
Some browsers or password managers can override autocomplete hints.
Require the owner to select an environment; do not preselect the first registered environment.
Preserve typed creation values when activity refreshes or existing-account actions finish.
Clear both creation fields after successful creation.
Point existing-account operations to their account rows.

Show issued credentials in a separate **Login details for <username>** section near the page status.
Name the completed action and destination environment in that section.
Scroll the result heading below the fixed page header so the account name remains visible on narrow screens.
Replace the heading, context, link, username, and password together before showing a new result.
Creating, converting, and reissuing accounts never populate the new-account username field.
**Dismiss login details** clears the displayed credentials and hides their section.
Dismissal does not revoke the credentials or expire the login link.
Keep the credential contents and API contracts unchanged.

Implementation: `admin.html`, `admin.js`, and `tools/test-global-environments.cjs`.
The browser check covers empty creation fields, explicit destination selection, separate results, correct account labels, and dismissal.
It also covers preserved creation drafts during refresh and reissue, account conversion, and desktop/mobile layouts.
These checks passed with temporary accounts; they changed no live accounts.

## Owner workflow

1. Log in with the existing `ernieMultiZone` username and existing password.
2. Open **administration** from the header.
3. Select **Open environment** to enter a listed environment.
4. Edit an environment's name, features, default providers, or membership and save it.
5. Enter a person's username under **Create a normal account**.
6. Select their environment and create the account.
7. Copy the returned personal login link, username, and password and send them yourself.

The recipient only needs to click their link.
Anyone holding it can act as that account, as explicitly requested.
The link selects its destination environment and initializes the assigned display name.
The fragment token is removed immediately, then exchanged through a POST request.
The landing page uses no external assets, does not cache, and sends no referrer.

Use **Convert to login-link account** on a remaining password-file row.
Type the sendable username, then convert.
Copy the shown link, username, and password, then send them yourself.
The previous password-file username and password stop working.
Use **New link** to replace a normal account's link.
Use **Issue login details** to create a new password and login link for an existing normal account.
Copy the shown link, username, and password, then send them yourself.
The previous password and previous login link stop working.
Use **Revoke account** to revoke that new account across environments without deleting its history.
Edit membership checkboxes to grant or remove access to an individual environment.
Use **Create an environment** to request another named environment.
Provisioning status appears in the dashboard; capacity failures remain visible instead of selecting another destination.

## Production controller

`deploy/environment-controller.py` is installed as root-owned code outside the writable server checkout.
Its one-time `--initialize` operation preserves existing password hashes and the original private route.
It creates the global account registry and seeds the requested Vibecoders environment.
It backs up the original settings before selecting the shared authentication paths.
The existing application's systemd drop-in permits writes only to the new control-state directory.
A root-owned controller service reconciles requested environments through fixed, validated provisioning operations.
Application processes do not receive arbitrary sudo access.

| Path | Purpose |
|---|---|
| `/var/lib/multiimageclient-control/auth.json` | Canonical existing password accounts; root-owned and group-readable. |
| `/var/lib/multiimageclient-control/state/links.json` | New global accounts, hashed passwords, and reusable-link digests. |
| `/var/lib/multiimageclient-control/state/registry.json` | Environment names, URL names, memberships, defaults, and features. |
| `/var/lib/multiimageclient-control/provisioning.json` | Root-written provisioning results. |
| `/usr/local/lib/multiimageclient-control/` | Root-owned controller scripts and settings template. |
| `/etc/multiimageclient-env-<id>/` | One environment's independent settings and deployment manifest. |
| `/var/lib/multiimageclient-env-<id>/` | One environment's independent stored work. |
| `/opt/multiimageclient-env-<id>/` | One environment's published application. |

Only the original controller application writes registry/account state.
Additional application accounts receive read access through the `mic-auth` group.
The shared root directory is root-owned. Its writable state directory is separate from root-written status and authentication files.
The reconciler accepts bounded JSON, fixed path roots, validated identifiers, and validated single-segment URL names.
It never accepts a supplied shell command or arbitrary service path.
It also removes retired password-file usernames from the canonical auth file after conversion.

Additional instances use 1024 MiB MemoryHigh and 1536 MiB MemoryMax.
They copy the original provider request cap, pending capacity, and provider lane limits.
On 2026-09-09, these are 14 aggregate requests, 64 pending jobs, and scheduler-default lane limits.
This supersedes the initial one-request cap and 384/512 MiB memory limits.
The original service retains its existing limits.
The current host permits one additional instance until its capacity budget is increased.
The UI can record further requests, but the controller reports insufficient capacity rather than starting more processes.
Installation requires at least 4 GiB free disk, preserving the existing application's 3 GiB reserve.
During preparation, disposable test/Web build directories were removed. Production histories and images were not removed.

Installation adds an include to the existing named nginx TLS server and reloads nginx after validation.
Both sites-enabled and sites-available layouts are supported because the live enabled file is a regular file.
It never restarts neighboring services or changes the original private location.
The original application receives its code through the normal release helper.
Additional-instance updates use `create-environment.py update --id <id> --publish <verified-publish>`.
An update verifies the selected service and loopback health and retains the previous binary for recovery.

## API and implementation map

| Surface | Contract |
|---|---|
| `POST api/auth/login` | One username/password identity across assigned environments. |
| `POST api/auth/link` | Reusable-token login with destination membership validation. A conflicting profile name returns HTTP 409 without a session. |
| `GET /public/signup` | Anonymous exact-username form on the original controller, when enabled. |
| `POST /public/signup/request` | Same-origin member lookup and review queue; requires `X-Mic-Account: 1`. Returns JSON, never authentication. |
| `GET /public/signup/request` | Recover the previous error address by returning to the form. |
| `GET /public/signup/preview` | Harmless test-link page; never creates or authenticates an account. |
| `GET /api/control/discord-account-requests` | Owner-only request list with current action permissions; no secrets. Issued links have null `expiresAt`. |
| `POST /api/control/discord-account-requests/{id}/{action}` | Owner-only `test`, `send`, or `reject`; requires `X-Mic-Manage: 1`. Confirmed send requires a successful recent test. |
| `GET /public/signup/claim` | Confirmation page; GET never consumes a token. |
| `POST /public/signup/claim` | Same-origin, explicit reusable-token login; recheck membership, persist activation, and issue a session. |
| `GET api/control/state` | Admin-only environment, membership, account, and provisioning list. |
| `POST api/control/environment` | Admin-only configuration and membership update. |
| `POST api/control/environment?create=true` | Request an environment; reject an existing identity. |
| `POST api/control/accounts` | Create a normal account and return credentials once. The chosen username is the login. |
| `POST api/control/password-accounts/convert` | Convert a password-file account to a chosen login-link username; return credentials once. |
| `POST api/control/accounts/{id}/credentials` | Replace the account's password and reusable link; return both once. |
| `POST api/control/accounts/{id}/replace` | Replace the account's reusable link. |
| `POST api/control/accounts/{id}/revoke` | Revoke a new account globally. |
| `GET api/admin/summary` | Admin-only login/activity records and generation-submission summaries. |
| `GET environment.js` | Current title, role, feature visibility, browser-storage scope, and session marker. |

Administration mutations require the exact owner identity and the `X-Mic-Manage` header.
The application does not enable cross-origin administrative requests.
Unknown environments, malformed stores, duplicate routes, and unavailable identities fail closed.

Implementation files:

- `UiEnvironmentRegistry.cs`: bounded environment policy and membership storage.
- `UiLoginLinks.cs` and `UiAuth.cs`: shared identities, credentials, chosen usernames, password-file conversion, link rotation, and revocation.
- `UiAccountActivity.cs`: durable login and activity observations.
- `DiscordAccountRequests.cs`: exact member lookup, effective channel access, and verified-recipient DM transport.
- `UiDiscordAccountRequests.cs`: controller-only tickets, persistent limits, Discord account binding, and reusable login without expiry.
- `UiDiscordAccountReviews.cs`: durable owner testing and confirmed delivery.
- `Ui/wwwroot/admin-discord-requests.js`: owner review controls and recovery from lost responses.
- `tools/test-discord-account-reviews.cjs`: desktop/mobile review behavior with mocked delivery.
- `UiDiscordAccountEndpoints.cs`: public forms, origin checks, explicit confirmation, and authenticated handoff.
- `DiscordAccountRequestTests.cs`: delivery errors, identity collisions, delayed reuse, credential replacement, review expiry, concurrent redemption, interrupted activation, revocation, permissions, and HTTP boundaries.
- `tools/test-discord-account-requests.cjs`: actual rendered forms, fragment removal, explicit confirmation, errors, and desktop/mobile browser checks.
- `UiGlobalAdminEndpoints.cs` and `UiEnvironmentEndpoints.cs`: administration and login APIs.
- `UiWorkflow.cs`: membership, feature enforcement, and activity integration.
- `UiJobs.cs`: account summaries from lightweight history entries.
- `admin.html`, `admin.js`, and `environment-storage.js`: owner controls, branding, and browser identity handling.
- `generator-toggle.js`, `generator-chooser.js`, and `style.css`: shared provider controls and compact list layout.
- `create-environment.py` and `environment-controller.py`: explicit provisioning and root-owned reconciliation.
- `UiEnvironmentRegistryTests.cs`, `UiLoginLinksTests.cs`, and `test_create_environment.py`: unit and provisioning tests.
- `tools/test-global-environments.cjs`: two-server browser tests for global identity, membership, features, activity, credential reissue, and password-file conversion through the admin page (2026-09-20), and duplicate-account login errors (2026-09-21). Password checks call the server directly because an open page reloads on 401 after a credential change.

The earlier `people.html` and isolated-cookie mode remain available for independently configured standalone instances.
They are not the selected production architecture.

## Validation and deployment status

The .NET suite passed with 319 tests during implementation.
The two-server Chrome check passed for global owner login, normal membership, shared activity, and private administrative routes.
It also verified editable titles, disabled feature APIs, persistent login records, and password/link identity.
Four Python tests passed on Linux, including configuration access under the controller service umask.
No paid provider calls were made during these checks.
Production activation completed on 2026-09-09.
Both public configuration endpoints accepted the existing owner identity and reported the admin level.
The canonical account file exactly matched the pre-migration hashes and signing secret.
The original three accounts and 1,317 job records remained intact.
The new environment reported zero jobs at verification.
A normal original member retained original access and received 403 from the new environment and raw logs.
A real administration save succeeded with the shared registry permissions.

The first installation exposed a restrictive-umask issue: the configuration directory became 0700 instead of 0750.
Provisioning now explicitly restores group traversal after directory creation.
Nginx backups live outside sites-enabled to avoid duplicate configuration through wildcard includes.
The controller requires the new route include and a successful loopback health check before reporting ready.
These checks prevent an interrupted installation's early manifest from being mistaken for completed provisioning.

## Member interface and integrations (2026-09-09)

The header shows only the configured environment name. The page title uses the same name.
The composer introduction is exactly: "enter prompt, choose image generators, and click generate".
Remove the logout button and persistent favorite shortcut instructions.
Display-name editing lives in personal preferences, with its existing history-update action.
Normal members do not see build information, RAM statistics, or raw logs. Admins retain these diagnostics.
Shared activity remains available to every member.

An environment selector appears when the account can access more than one environment.
It lists only authorized environments and retains the shared login during navigation.
Normal members receive no global administration URL in their environment configuration response.

Send to Vibecoders is an environment feature switch in administration.
The original environment keeps it enabled when no explicit value exists; new environments default to disabled.
Managed environments reuse the existing configured webhook. No webhook credentials reach the browser.
Disabling the switch hides the send control and rejects its API route.
Enabling it exposes the control only when the webhook is configured. Sending still requires an explicit user action.

Night filter defaults to available, preserving existing preferences.
Disabling its environment switch hides both its header button and preference controls.
Disabled filtering never hides jobs, even when the browser retained an enabled personal preference.
Re-enabling it restores access to the saved preference.

## Original environment sleeps on demand (2026-09-09)

The owner selected on-demand operation for the original environment to reduce idle RAM.
Vibecoders remains continuously available as a resident application process.
The original service sleeps after 15 minutes without browser requests or background work.
An open browser that continues polling keeps its environment awake.
Queued jobs, running jobs, contact-sheet finalization, and running goal loops prevent sleep.
Finishing work starts a fresh idle interval. Health probes do not extend that interval.

`multiimageclient-ui.socket` keeps the original loopback port 5960 open through systemd.
An incoming connection starts `multiimageclient-ui.service` and waits for application startup.
Kestrel accepts the exact inherited listener; there is no extra proxy process or nginx route change.
The server validates the process ID, descriptor count, and IPv4 loopback port before using the socket.
Missing or mismatched activation information aborts startup when sleep mode is configured.
The original URL, passwords, memberships, stored work, and provider limits remain unchanged.
A cold visit waits for startup. Memory limits are ceilings, not reserved allocations.

`UiIdleTimeoutSeconds` defaults to zero, which keeps local and new instances resident.
The original production setting is 900. Valid nonzero values range from 60 through 86400 seconds.
The idle monitor checks every 15 seconds and exits normally when idle.
The service uses `Restart=on-failure`; normal idle exit leaves the socket listening without an application process.
The request gate prevents new accepted work after the shutdown decision.
A request racing with shutdown receives HTTP 503 with Retry-After: 1, rather than starting work during shutdown.

Release the socket-aware application before running `deploy/install-original-on-demand.py` as root.
The installer checks original identity and unfinished work, backs up settings, and enables only the original activation socket.
It disables the original service's independent boot start. The socket starts at boot instead.
Routine releases stop the original activation socket before replacing and restarting its application.
They never kill an unidentified port owner. After release checks, the original sleeps normally again.
Vibecoders remains enabled at boot and has no idle timeout.

Tests: `UiIdleLifetimeTests` verifies request races, health probes, and background-work inhibition.
`tools/test-ui-socket-activation.py` verifies Linux inherited-listener startup, clean idle exit, and reuse by a fresh process.
