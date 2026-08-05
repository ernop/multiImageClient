# Shared-Host RAM Budget (miic-alpha.fuseki / tpbeta)

Surveyed 2026-08-04 against the live host. This records why the production
`--ui` deployment (`multiimageclient.alpha.fuseki.net`, systemd unit
`multiimageclient-ui.service` on tpbeta) has the memory limits it has, and
what else on the machine consumes RAM. Companion to
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

## The specific numbers and their reasoning

From `deploy/multiimageclient-ui.service` and
`MultiImageClient/runtimeconfig.template.json`:

- **`MemoryHigh=1200M` / `MemoryMax=1600M`** — "be a polite neighbor on a
  4 GiB server." This service is the newest tenant, so it gets roughly a
  quarter to a third of RAM, leaving the majority for the pre-existing
  sites. `MemoryHigh` triggers kernel reclaim/throttling first; `MemoryMax`
  is the hard kill line; the ~400 MB gap gives the liveness guard time to
  restart cleanly before the kernel does it uncleanly.
- **`OOMScoreAdjust=500`** — if the whole box runs out of memory anyway, the
  kernel should sacrifice this service, not the neighbors.
- **`System.GC.HeapHardLimitPercent: 50` + `ConserveMemory: 6`** — with a
  cgroup limit set, .NET sizes its heap off `memory.max`. The .NET default
  of 75% of 1600M equals 1200M — exactly `MemoryHigh` — so the GC felt no
  pressure until the service was already at the restart threshold. Pinning
  50% caps the managed heap around 800 MB and collects aggressively, leaving
  headroom for native allocations (ImageSharp/Magick buffers, sockets, JIT).
- **`Nice=10`, `CPUQuota=150%`, `IOWeight=25`, `TasksMax=256`** — the same
  politeness principle applied to CPU, disk, and thread/process count.
- **App-level caps** (production settings on tpbeta): 1 finalizer job,
  20 aggregate provider requests, 64 pending jobs, 3 GiB disk reserve, low
  per-lane provider caps.

Steady state observed 2026-08-04: cgroup `memory.current` ~750–950 MB
against the 1200M high — warm but under the throttle line.

## Other RAM users on the box, and why

tpbeta hosts one person's colocated sites. Per-service cgroup usage as
surveyed (total ~2.3 GiB used, ~1.2 GiB page cache, ~1.5 GiB available):

| Service | RAM | What it is |
|---|---|---|
| `multiimageclient-ui` | ~750–950 MB | this app |
| `tpbeta.uwsgi` | ~504 MB | Terrain Parkour beta Django site (uWSGI, `parkour2021` venv) |
| `tpdiscord-web` | ~409 MB | tpDiscord web UI — a Django dev `runserver` on port 8018, not uWSGI, which partly explains its size |
| `fuseki4_ai.uwsgi` | ~181 MB | subcreation Django app; its cgroup also holds a ~120 MB `goaccess` (log analytics) and a `fuseki4-ai-gene` worker |
| `postgresql@18-main` | ~93 MB | shared database backing the Django sites |
| `nginx` | ~64 MB | the only public listener; routes all vhosts including the secret-path miic vhost |
| `tpdiscord-reader` | ~63 MB | Discord message reader companion to tpdiscord-web |
| `syncthing@subcreation` | ~48 MB | file sync |
| `fail2ban` | ~23 MB | SSH/web brute-force banning |
| `php8.3-fpm`, `python-relay` | ~15 MB, ~13 MB | small services |

The neighbors sum to about 1.4 GiB and predate this deployment — that is why
the miic budget sits where it does: 1200M high keeps the box out of the
no-swap OOM zone even when this service and Terrain Parkour peak together.

## Open observations (no action decided)

- `tpdiscord-web` at ~400 MB via Django `runserver` is the second-largest
  tenant and the least production-shaped process on the box.
- `deploy/README.md`'s example settings show `UiMaxConcurrentGenerators: 2`
  while AGENTS.md records production at 20. The live
  `/etc/multiimageclient/settings.json` is root-owned 0640 and the deploy
  SSH account has passwordless sudo only for the update helper, so the live
  value was not confirmed during this survey.
