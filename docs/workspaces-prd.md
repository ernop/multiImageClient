# Separate workspaces and personal login links

Date: 2026-09-08, America/Los_Angeles.
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
Normal accounts require explicit environment membership assigned by the owner.
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
No public registration or member-controlled invitations exist.
Only the owner creates accounts and distributes their personal links.

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
Passwords use PBKDF2-SHA256. Personal links store only SHA-256 token digests.
The owner receives new credentials once. Later link retrieval requires replacement.
Existing legacy accounts retain their existing password hashes.

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

## Owner workflow

1. Log in with the existing `ernieMultiZone` username and existing password.
2. Open **administration** from the header.
3. Select **Open environment** to enter a listed environment.
4. Edit an environment's name, features, default providers, or membership and save it.
5. Enter a person's name under **Create a normal account**.
6. Select their environment and create the account.
7. Copy the returned personal login link and send it yourself.

The recipient only needs to click their link.
Anyone holding it can act as that account, as explicitly requested.
The link selects its destination environment and initializes the assigned display name.
The fragment token is removed immediately, then exchanged through a POST request.
The landing page uses no external assets, does not cache, and sends no referrer.

Use **New link** to replace a normal account's link.
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
| `POST api/auth/link` | Reusable-token login with destination membership validation. |
| `GET api/control/state` | Admin-only environment, membership, account, and provisioning list. |
| `POST api/control/environment` | Admin-only configuration and membership update. |
| `POST api/control/environment?create=true` | Request an environment; reject an existing identity. |
| `POST api/control/accounts` | Create a normal account and return credentials once. |
| `POST api/control/accounts/{id}/replace` | Replace the account's reusable link. |
| `POST api/control/accounts/{id}/revoke` | Revoke a new account globally. |
| `GET api/admin/summary` | Admin-only login/activity records and generation-submission summaries. |
| `GET environment.js` | Current title, role, feature visibility, browser-storage scope, and session marker. |

Management mutations require the exact owner identity and the `X-Mic-Manage` header.
The application does not enable cross-origin administrative requests.
Unknown environments, malformed stores, duplicate routes, and unavailable identities fail closed.

Implementation files:

- `UiEnvironmentRegistry.cs`: bounded environment policy and membership storage.
- `UiLoginLinks.cs` and `UiAuth.cs`: shared identities, credentials, link rotation, and revocation.
- `UiAccountActivity.cs`: durable login and activity observations.
- `UiGlobalAdminEndpoints.cs` and `UiEnvironmentEndpoints.cs`: administration and login APIs.
- `UiWorkflow.cs`: membership, feature enforcement, and activity integration.
- `UiJobs.cs`: account summaries from lightweight history entries.
- `admin.html`, `admin.js`, and `environment-storage.js`: owner controls, branding, and browser identity handling.
- `create-environment.py` and `environment-controller.py`: explicit provisioning and root-owned reconciliation.
- `UiEnvironmentRegistryTests.cs`, `UiLoginLinksTests.cs`, and `test_create_environment.py`: unit and provisioning tests.
- `tools/test-global-environments.cjs`: two-server browser tests for global identity, membership, features, and activity.

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
