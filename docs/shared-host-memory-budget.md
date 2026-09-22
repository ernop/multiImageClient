# Shared-Host RAM Budget (MultiImageClient production on tpbeta)

The product target is the owner's private image-making site at
`https://multiimageclient.alpha.fuseki.net/<private-path>/`; the path segment
is secret and never belongs in logs or commits. `tpbeta` is only the colocated
physical host. Routine releases update `multiimageclient-ui.service` and do
not touch the host's neighboring sites or services.

**Update 2026-09-09:** the owner selected on-demand operation for the original environment.
Its systemd socket remains listening while the application sleeps after 15 idle minutes.
Vibecoders remains resident. See [the environment lifecycle contract](workspaces-prd.md#original-environment-sleeps-on-demand-2026-09-09).
Before this change, the two idle service cgroups measured approximately 79 MiB and 76 MiB respectively.
These are observed idle values, not generation peaks or reserved memory.
The original retains its 2048/2560 MiB limits; Vibecoders uses 1024/1536 MiB limits.
These independent ceilings do not guarantee combined capacity during simultaneous generation.
Sleeping the unused original removes its idle process cost without lowering provider concurrency.

**Update 2026-08-05:** tpdiscord-web/reader were uninstalled and
tpbeta.uwsgi was stopped and disabled (files retained), freeing ~1 GiB and
making this app the box's primary tenant. Remaining neighbors are ~600 MB
steady (fuseki4_ai ~265, postgres ~100, syncthing/nginx/journald/misc ~150)
plus periodic cron spikes to ~700 MB (cgroup peak, partly reclaimable page
cache). The unit was raised to **MemoryHigh=2048M / MemoryMax=2560M**
(from 1200M/1600M): ~600M neighbors + ~500M cron allowance + ~400M
page-cache floor leaves ~2.4 GiB of the 3.8 GiB no-swap box. The GC
50%-of-max cap becomes 1280 MB (62% of high) and the 512 MB high→max gap
covers the largest observed allocation burst (app peaked at 953 MB during
active multi-user generation). The analysis below is the original
2026-08-04 survey and explains the reasoning framework; its numbers predate
this change.

Surveyed 2026-08-04 against the live host. This records why that production
`--ui` deployment (systemd unit `multiimageclient-ui.service` on `tpbeta`) has
the memory limits it has, and what else on the machine consumes RAM. Companion
to
[.cursor/rules/shared-site-resident-memory.mdc](../.cursor/rules/shared-site-resident-memory.mdc)
(the design rule) and [deploy/README.md](../deploy/README.md) (the install
procedure).

## Beta standby approved and applied — 2026-09-20

At 17:52 Pacific, the owner explicitly requested stopping beta without removing its files or database.
The earlier read-only review below is a snapshot before this change.

Verified identity: `machine_name=tpbeta`, beta uWSGI configuration, and nginx routing to `/tmp/parkour2_beta.sock`.
Production TP is a separate host, `tp`, with `machine_name=tparkour` and active `tp.uwsgi.service`.
Beta had zero fresh player heartbeats, zero joins in seven days, and a last recorded join on July 7.
Roblox's public API also reported zero beta players during this check.
The earlier recommendation to consider retirement lacked this identity and activity verification.

`tpbeta.uwsgi.service` is now inactive, PID zero, and disabled at boot.
Its service definition, application files, virtual environment, nginx route, and PostgreSQL database remain installed.
The service consumed 478,154,752 bytes (456 MiB) immediately before stopping.
Host available memory measured approximately 2.68 GiB afterward.
Both image services and Fuseki retained their existing process IDs.

Cause of automatic reactivation: `/etc/logrotate.d/terrainparkour-beta` ran `systemctl restart tpbeta.uwsgi` after rotating logs.
Boot enablement was already disabled; this independent command still started the service.
Changed only that command to `systemctl try-restart tpbeta.uwsgi`.
Validated the configuration with `logrotate --debug`.
Invoked `try-restart` afterward and confirmed beta remained inactive and disabled.

Preserved backups under `/root/maintenance-backups/tp-beta-disable-20260921T005253Z/`.
The installed service definition's checksum remained unchanged.
Recorded the standby requirement in both Terrain Parkour checkouts' `AGENTS.md` and `docs/server-operations.md`.
Starting beta again requires a new explicit owner request.
Its earlier always-active guidance is superseded by this decision.
This authorization does not extend to unrelated services.

Django inventory on the shared image host:

| Application | Address or route | State after maintenance |
|---|---|---|
| Terrain Parkour beta | `tpbeta` / beta host IP, through the beta Unix socket | Stopped; installed and preserved |
| Fuseki (`fuseki4_ai`) | `fuseki.net` and `www.fuseki.net` | Running; approximately 328 MiB |

The `subcreation` nginx site serves static files; it is not another Django process.
MultiImageClient's two instances use .NET, not Django.
Production Terrain Parkour remains on its separate host.

## Production memory review — 2026-09-20

Measured September 20 at 17:34–17:38 Pacific (September 21 at 00:34–00:38 UTC).
This review changed no production services, limits, caches, or stored work.
The server checkout was `bdb71c07ff9e366e0cd7be9a9fbfb46880413df6`.

### Measurements before beta standby

The host has 3916 MiB physical RAM and no swap.
Available RAM rose from 2221 to 2317 MiB during the review.
The 10-, 60-, and 300-second memory-pressure averages were zero at both samples.
File cache accounted for approximately 2060 MiB in the first sample.
Low completely free RAM therefore did not indicate current exhaustion.

| Service | Current group memory | Relevant detail |
|---|---:|---|
| Original image site | 75 MiB | Managed objects about 5 MiB; 2048/2560 MiB high/hard limits |
| Vibecoders | 112–117 MiB | Managed objects about 40 MiB; 1024/1536 MiB high/hard limits |
| Terrain Parkour beta (`tpbeta.uwsgi`) | 454 MiB | About 448 MiB anonymous memory; active despite disabled boot enablement |
| `fuseki4_ai.uwsgi` | 328 MiB | About 315 MiB anonymous memory |
| Journal service | 132 MiB | About 125 MiB file-backed memory |
| nginx | 92 MiB | About 78 MiB file-backed memory |

Group memory includes charged cache and differs from process resident memory, which also counts shared mapped pages.
Do not add both measurements as separate costs.
Both image services had restarted approximately 13–18 minutes before inspection.
Neither had queued or active provider requests. Neither held a warm browser or cached card previews.
These are idle measurements after restart, not representative generation peaks.

The seven-day journal records two Vibecoders memory-guard restarts on September 17.
The guard measured approximately 1067 and 1070 MiB before those exits, above its 1024 MiB threshold.
Its previous service lifetime also reported a 1.0 GiB peak on September 20.
No kernel out-of-memory events appeared in the retained seven-day kernel journal.
Two original-site SIGKILL exits have no established cause from these records.

The beta service became active September 19 at 17:00 Pacific.
Its reactivation supersedes the August statement that it remained stopped.
The initial review did not establish the restart cause or player activity; the follow-up above resolved both.
Do not stop it as part of an image-site release.
Its approximately 454 MiB consumption must count toward the shared host budget.
The two image services' combined 4096 MiB hard limits exceed physical RAM before neighboring services are included.
Those limits constrain individual services; they do not reserve capacity or guarantee simultaneous peak operation.

### Improvements identified, not implemented

1. **Release image buffers after request serialization.**
   `ManagerChatHttp.SendAsync` adds each full `ManagerChatImagePayload` to its `labels` list.
   The list is needed for the redacted copy, but retains every image byte array through that serialization pass.
   It should retain only label, media type, and byte count after writing each image.
   The cards loop's round-nine critic supplied twelve images totaling 35,319,387 bytes (33.7 MiB).
   This is confirmed retained payload data, not a measured estimate of total process savings.
   Preserve exact outgoing bytes, image order, placeholders, and provider limits.
2. **Expire completed live-feed jobs.**
   `UiJobRegistry` chooses today's jobs at startup but never removes job IDs from `_liveFeedJobIds`.
   Archive eviction excludes those IDs, so a resident process retains completed jobs across calendar days.
   Introduce a bounded live window with safe client resynchronization and disk-backed archive access.
   Preserve unfinished jobs, archive records, ownership, and exact image identities.
3. **Reduce loop-history allocation and bound caches by bytes.**
   Idle loops have a 48-loop count cap, without byte or age limits.
   `CloneWithoutWire` serializes and clones wire payloads before discarding them.
   The cards loop contains 2,542,874 wire characters in approximately 5.2 MB of stored entry JSON.
   Multiple tabs repeat these allocations when loading complete histories.
   Omit wire fields before copying; retain exact per-entry wire access from disk.
4. **Reassess the shared budget before increasing concurrency or limits.**
   Beta standby was subsequently authorized and applied as recorded above.
   Add anonymous/file-cache breakdown and recent peaks to image-site memory diagnostics.
   Current idle readings cannot identify which allocation caused the older memory-guard restarts.
   Leave operating-system cache reclamation and existing safety limits in place during diagnosis.

The cards loop reached round nine before a critic reply failed its goal-completion consistency check.
That saved failure identifies a reply-contract error, not a memory failure.

## Grok-web image concurrency (2026-09-09)

Local and original private production settings now override `UiTargetConcurrency["grok-web-ws"]` to `4`.
This queue covers Grok-web text-to-image, direct image editing, and chat editing.
The previous limit was one; a local snapshot showed five waiting requests behind one running request.
Each UI attempt creates its own Grok client, so the per-generator semaphore does not serialize separate jobs.
The application now permits four simultaneous Grok-web image targets, subject to the existing aggregate cap.
A target requesting several images still performs its own attempts sequentially while holding its scheduler slot.

The aggregate caps remain 20 locally and 14 in original production.
Grok API remains at two; Grok-web video remains at one.
Finalization, pending-job limits, RAM ceilings, and other provider limits remain unchanged.
These are explicit installation settings, not a global default change or a measured xAI account limit.
Additional environments retain their existing settings.
Both running servers reported a limit of four after restart.
A local snapshot showed two Grok-web image targets running together with no queued targets.

## Why limits exist at all

The app was designed as a local one-shot CLI and initially behaved like one
under `--ui`: it kept every result image's bytes in process RAM for the life
of the process, hydrated the entire `UiHistory/` archive at startup, and grew
several unbounded caches (card previews, event envelopes). Harmless for a
process that exits after a run; deployed as a long-lived multi-user daemon on
a 3.8 GiB box with **no swap** and four other tenants, it exhausted memory.
No swap means there is no graceful degradation: exceeding physical RAM goes
straight to the kernel OOM killer, which can kill a neighbor instead of us.

The fix was layered, in commit order (2026-07-31 → 2026-08-04):

1. **Fix retention first** (`4f41bf9`, `64283e3`): disk became the source of
   truth. Finished results serve from archived paths, not heap buffers; only
   today's jobs stay resident while archive days hydrate on demand with cold
   eviction; card thumbs live on disk behind a 48 MiB LRU; envelopes/events
   trim or spill to disk after job completion.
2. **Bound compositing spikes** (`ff773e8`, `a47364b`, `14a1e44`):
   ImageSharp's allocator pool is capped at 64 MB and released after every
   job; contact-sheet finalization — the largest transient allocation, since
   it composites many multi-MP originals — is gated by
   `UiMaxConcurrentJobs: 1` in production. That setting gates only
   finalization, so it does not serialize unrelated provider requests.
3. **Bound intake** (`3fa5f31`, `90cef22`): the fair scheduler caps aggregate
   open provider requests (`UiMaxConcurrentGenerators`) with low per-lane
   caps; `UiMaxPendingJobs: 64` returns HTTP 503 instead of accumulating
   unbounded queued work; `UiMinimumFreeDiskBytes` (3 GiB on tpbeta) rejects
   new jobs before reading uploads.
4. **Remove Chromium from the production UI** (`2f9227b`, after idle-release
   proved insufficient): a warm Playwright Chromium is hundreds of MB.
   grok-web stays available only on its browser-free WebSocket; edit/video
   and meta-web are absent from the production UI.
5. **Contain and self-heal** (`94e8847`, `18c81a5`, `4d182ef`): the systemd
   unit plus runtime config enforce ceilings, and `UiLivenessGuard` calls
   `Environment.FailFast` after sustained `memory.current >= memory.high` —
   restarting *before* reclaim-thrash makes HTTP unserviceable, which was the
   observed failure mode (process alive, site dead).
6. **Eliminate streaming churn** (`479f812`): each gpt-image-2 SSE
   partial/final arrived as one multi-MB base64 line materialized ~6x
   (UTF-16 line, Substring, JSON transcode, two GetString calls, base64
   decode); two concurrent streams generated enough LOH garbage to push the
   cgroup past `memory.high` and trip the liveness guard. The stream is now
   read as UTF-8 bytes through a pooled line reader with partials decoded
   straight from the `JsonDocument`.

## Current limits and their reasoning

From `deploy/multiimageclient-ui.service` and
`MultiImageClient/runtimeconfig.template.json`:

- **`MemoryHigh=2048M` / `MemoryMax=2560M`** — verified live 2026-08-06.
  `MemoryHigh` is the operating ceiling; `MemoryMax` is the hard kill line.
  The 512 MB gap covers fast native/image allocation bursts while the
  liveness guard gets time to restart a persistently throttled process.
- **`OOMScoreAdjust=500`** — if the whole box runs out of memory anyway, the
  kernel should sacrifice this service, not the neighbors.
- **`System.GC.HeapHardLimitPercent: 50` + `ConserveMemory: 6`** — with a
  cgroup limit set, .NET sizes its heap off `memory.max`. Pinning 50% caps
  the managed heap around 1280 MB (62.5% of `MemoryHigh`) and leaves about
  768 MB below the operating ceiling for native allocations (ImageSharp/
  Magick buffers, sockets, JIT).
- **`Nice=10`, `CPUQuota=150%`, `IOWeight=25`, `TasksMax=256`** — the same
  politeness principle applied to CPU, disk, and thread/process count.
- **App-level caps** — verified live 2026-08-06: 1 finalizer job, 14 aggregate
  provider requests, default 64 pending jobs, 3 GiB disk reserve, and the
  scheduler's per-lane defaults, except the four-request Grok-web image override added on 2026-09-09.

Steady state observed 2026-08-04 was ~750–950 MB. That observation predates
the raised limits but remains useful as a working-set baseline.

## Other RAM users on the box, and why

This is the **2026-08-04 pre-decommission snapshot**, not the current tenant
list. At that time tpbeta's per-service cgroup usage totaled ~2.3 GiB used,
~1.2 GiB page cache, and ~1.5 GiB available:

| Service | RAM | What it is |
|---|---|---|
| `multiimageclient-ui` | ~750–950 MB | this app |
| `tpbeta.uwsgi` | ~504 MB | Terrain Parkour beta Django site (uWSGI, `parkour2021` venv) |
| `tpdiscord-web` | ~409 MB | tpDiscord web UI — a Django dev `runserver` on port 8018, not uWSGI, which partly explains its size. **Decommissioned 2026-08-05** (see below) |
| `fuseki4_ai.uwsgi` | ~181 MB | subcreation Django app; its cgroup also holds a ~120 MB `goaccess` (log analytics) and a `fuseki4-ai-gene` worker |
| `postgresql@18-main` | ~93 MB | shared database backing the Django sites |
| `nginx` | ~64 MB | the only public listener; routes all vhosts including the secret-path miic vhost |
| `tpdiscord-reader` | ~63 MB | Discord message reader companion to tpdiscord-web. **Decommissioned 2026-08-05** (see below) |
| `syncthing@subcreation` | ~48 MB | file sync |
| `fail2ban` | ~23 MB | SSH/web brute-force banning |
| `php8.3-fpm`, `python-relay` | ~15 MB, ~13 MB | small services |

The neighbors then summed to about 1.4 GiB and motivated the original
1200M/1600M limits. The 2026-08-05 retirements below freed enough capacity
for the current 2048M/2560M limits.

## tpDiscord decommission (2026-08-05)

`tpdiscord-web` + `tpdiscord-reader` were shut down and removed: units
stopped/disabled/deleted, the four `/tpdiscord/` nginx routes removed from
`terrain_nginx_beta.conf` (committed in the terrainParkour repo), the
`tpdiscord` Postgres database and its dedicated role dropped, and the
untracked bulk data (2.5 GB `media/`, venv, messages, logs, `config.json`)
deleted from the server. The git-tracked source stays in the terrainParkour
repo (`services/tpdiscord/`, ~1.8 MB working copy restored). Everything
deleted was archived first to this project's local `tpdiscord-archive/`
(gitignored): full media (3,996 files, SHA/size-verified), `pg_dump` in
custom + plain-SQL formats (hash-verified), `config.json`, `messages/`,
`chatindex/`, `logs/`. The Discord bot token was deliberately NOT revoked.
This freed ~515 MB RAM and ~2.8 GB disk. The memory budget was subsequently
raised to the current 2048M/2560M values documented above.

## Process identity correction (2026-09-10)

Read memory limits only from the current process’s `/proc/self/cgroup` membership.
Missing membership must not select the named production service as a substitute.
That service can belong to another process on a development workstation.
Incorrect limits can also trigger the local liveness guard.
Report unavailable values when exact membership cannot be resolved.
`/api/status` now includes the process identifier, runtime version, and UTC process start time.
Without a group limit, the header displays the server working set instead of the shared desktop group total.
Limited deployments continue to compare group usage with their configured limit.
See [local-ui-server.md](local-ui-server.md) for restart behavior and validation.
