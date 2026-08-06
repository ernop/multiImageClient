# Shared-Host RAM Budget (MultiImageClient production on tpbeta)

The product target is the owner's private image-making site at
`https://multiimageclient.alpha.fuseki.net/<private-path>/`; the path segment
is secret and never belongs in logs or commits. `tpbeta` is only the colocated
physical host. Routine releases update `multiimageclient-ui.service` and do
not touch the host's neighboring sites or services.

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
  scheduler's conservative per-lane defaults (no live override object).

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
