# Separate workspaces and personal login links

Date: 2026-09-08, America/Los_Angeles.
Status: requirements recorded; architecture proposed; implementation pending.

A workspace is one isolated tenant, also called an image-making studio.
The current production site is the first tenant.
The new idea is a second tenant with its own membership and history.

## Confirmed requirements

| ID | Requirement | Behavior |
|---|---|---|
| R1 | Preserve the existing group. | Keep Ernie, Austin, Victor, their accounts, history, images, and user configurations working in the existing environment. |
| R2 | Add another tenant. | Start the new environment with separate accounts, membership, prompts, images, history, and user configurations. |
| R3 | Let the owner access both. | Give the owner access to both groups without merging their history. |
| R4 | Send personal login links. | Opening a recipient's link selects their workspace, sets their username, and signs them in. |
| R5 | Treat possession as identity. | Anyone holding the link can act as its assigned person. |
| R6 | Require no credential explanation. | The recipient only needs to click their personal link. |
| R7 | Start with entirely new accounts and data. | Do not copy or share existing accounts, history, images, or personal configurations into the new environment. |

These requirements came from the owner during the review.
The link grants the account's actual permissions; it does not merely select a display name.
The intended recipient instruction is: "Click this link to log in."

The owner clarified that the new environment starts independently of the existing group's data.
Ernie can send personal login links to bring other people into that new environment.
Ernie's access to both environments does not merge their accounts or personal configurations.
The existing environment continues working at its current address.
Sharing application code does not imply sharing user data.
Provider credentials and infrastructure configuration remain separate implementation decisions.

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

## Architecture comparison

| Approach | Benefit | Required work |
|---|---|---|
| Separate application instances | Existing single-store code naturally separates each group's data. | Separate services, ports, accounts, configurations, storage roots, and deployment targets. |
| Workspaces inside one process | Shares scheduling and supports one authenticated workspace selector. | Explicit membership checks and workspace scoping across every data surface. |
| Frontend user filters | Small interface change. | Does not provide the required separation; reject this approach. |

Recommendation for the first additional group: use a separate application instance.
This reduces the number of existing history and authorization paths that must change together.
Use the same source project, with explicit releases for each instance.
Give the owner an account in each instance.
Personal login links can provide direct entry to either instance.
Cross-instance single sign-on is not established by this proposal.

A distinct hostname would separate browser storage and host-only cookies.
The exact hostname remains undecided.
Different paths on the same hostname share `localStorage`.
Both current instances would also write a `mic_auth` cookie at `/`.
Therefore, merely adding another nginx path would cause browser-state collisions.
See [MDN Web Storage](https://developer.mozilla.org/en-US/docs/Web/API/Web_Storage_API)
and [MDN Set-Cookie](https://developer.mozilla.org/en-US/docs/Web/HTTP/Reference/Headers/Set-Cookie).

Do not copy the existing service's full resource allowance into a second service without combined load measurements.
Two processes also have independent provider schedulers.
Shared provider credentials would require a combined account-level concurrency budget.
Separate credentials and billing remain undecided.

### Requirements for either architecture

Separate jobs, event polling, archives, input libraries, media routes, goal loops, recaps, and source-image reuse.
Separate profiles, favorites, requests, prompt rewrite history, preferences, deletion state, and user-visible logs.
Preserve exact workspace ownership for every resource lookup and mutation.
Do not infer membership from `CreatedBy`, a display name, or a browser filter.
Treat owner access as explicit authorization.

For a shared process, authorize the resource within its workspace before returning content.
Microsoft describes this distinction in [resource-based authorization](https://learn.microsoft.com/en-us/aspnet/core/security/authorization/resource-based?view=aspnetcore-10.0).
The global `Logger.ReadBuffered` feed needs special attention because it currently includes events across the process.
The static generation archive also prevents simply constructing several independently configured application contexts.

Existing B2 originals use public, unguessable links under the owner's established sharing policy.
Anyone who receives an original image link can retrieve its bytes outside application login.
Separate workspace membership does not revoke previously shared original-image URLs.
Retain this distinction when describing privacy; see [B2 hosting](b2-image-hosting-plan.md).
Stricter private media delivery remains a separate decision.

## Proposed personal-link implementation

This section proposes technical details; it does not claim implementation or owner approval of every detail.

1. Create a random personal token using 32 cryptographically random bytes.
2. Bind its digest to the exact account and permitted workspace.
3. Put the token after `#` in a dedicated login landing URL.
4. Read it in the browser and remove it from the address bar immediately.
5. Submit it to a dedicated login endpoint in a POST body.
6. Validate the token and its current account membership.
7. Issue a secure, HttpOnly session cookie.
8. Load the assigned workspace and the account's stored display name.

The reusable link remains valid until replaced or revoked under this proposal.
Do not add email verification, password entry, or a one-use requirement to the recipient flow.
The server must not accept a token paired with a different submitted username.
An invalid link must never open a default account or workspace.

Store only a token digest in the authentication database or file.
Generate a new token when the owner requests a replacement.
Provide owner controls to issue, replace, and revoke personal links.
Provide the complete link for copying at issuance.
Do not send messages automatically; the owner distributes the link.
Replacement should invalidate sessions derived from the replaced link.
Account removal should invalidate every authentication method for that account.

The token endpoint needs request-body redaction and the existing login throttle pattern.
The landing page needs no third-party scripts or assets.
Use `Cache-Control: no-store` and `Referrer-Policy: no-referrer`.
URL fragments do not enter the initial HTTP request, as documented by [MDN](https://developer.mozilla.org/en-US/docs/Web/URI/Reference/Fragment).
The later token exchange still transmits the secret and must never log it.

Opening another person's link intentionally selects that person's identity.
Tabs sharing that session must reload their identity-dependent presentation before further actions.
Preserve the existing owner's password login while adding this new method.

## Review findings and rollout prerequisites

| Finding | Consequence | Follow-up |
|---|---|---|
| Production has no workspace membership model. | Adding accounts grants access to the existing shared history. | Establish instance isolation or implement full workspace authorization first. |
| Authentication changed during review. | Old browser sessions no longer authenticate. | Use the preserved password; diagnose browser behavior if login still fails. |
| Disk availability is close to the 3 GiB reserve. | New local inputs and metadata can consume the remaining allowance. | Measure storage growth before adding workload. |
| The update helper executes a script in the deploy user's checkout. | Its fixed sudo path does not itself restrict the script's privileged actions. | Review the deploy-user trust boundary before extending deployment access. |
| The installer can invoke the update helper during its smoke check. | Installing the helper can also deploy staged code. | Do not treat helper installation as a read-only check. |
| The update script's HTTP probe does not enforce a successful status. | A completed script alone does not prove the application is usable. | Retain independent health, login, and public-route verification. |
| Local restart uses a broad process-name match. | Several local instances could all stop together. | Scope restart controls before testing multiple local instances. |
| Some security documentation describes obsolete deletion behavior. | It understates the effect of creator deletion. | Reconcile it with the later destructive deletion contract in the B2 document. |
| AGENTS.md still says no test project exists. | The instruction conflicts with `MultiImageClient.Tests` in the solution. | Update the general test guidance in a documentation maintenance change. |

The review covered architecture, deployment scripts, data boundaries, authentication, and relevant feature documentation.
It was not an exhaustive line-by-line audit of every provider or utility.
No paid generation calls ran.
The JavaScript syntax check passed.
The Windows test command could not resolve Anthropic.SDK 4.1.1.
Its generated package manifest referenced `/home/ernie/.nuget/packages/`.
A normal restore returned success but retained that Linux path.
Therefore, this review does not claim a passing .NET test suite.

Before rollout, test two workspaces with owner, member, revoked-link, and unrelated-member identities.
Test guessed resource IDs, direct media URLs, polling, logs, exports, and concurrent tabs.
Test simultaneous generation against the combined memory and provider budgets.
Verify existing users and old history before and after deployment.
Keep the original service and data unchanged when provisioning a separate instance.

## Remaining decisions

- Select separate instances or workspaces within one process.
- Choose the new group's name and members.
- Choose its hostname if using a separate instance.
- Decide whether provider credentials and spending remain shared.
- Decide whether public original-image links remain suitable for the new group.
- Decide whether owner access needs one shared login or direct personal links to each instance.
- Decide per-tenant policy: allowed endpoints, describe/video/sketch, spend and rate caps, moderation, and max output size.

## Implementation file map

- `Implementation/UiAuth.cs`: personal tokens, account binding, and session revocation.
- `Workflows/UiWorkflow.cs`: token exchange and authenticated identity initialization.
- `Ui/wwwroot/`: login landing page and owner link controls.
- `Implementation/UiCommunity.cs`: account display names and any owner management data.
- `deploy/`: explicit instance configuration, service units, and release targets if selected.
- `MultiImageClient.Tests/`: token and authorization tests.
- Browser tests: automatic entry, identity changes, and isolated browser storage.
- `AGENTS.md`: links and requirements summary.

No workspace or personal-link runtime code was added during this review.
