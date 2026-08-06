# Production Deployment — Private MultiImageClient Site

This repository has one production product target: the owner's private shared
image-making site, referred to as `multi-image-client-alpha.fuseki.net`. The
verified installed nginx/TLS hostname is currently
`multiimageclient.alpha.fuseki.net`; changing that hostname is a separate
DNS/TLS migration, not a release step.

The site runs on the colocated machine `tpbeta` as
`multiimageclient-ui.service`. `tpbeta` is host infrastructure, not the
product target. Its other sites, vhosts, and services are outside this
repository's deployment scope.

The `--ui` web app lets everyone generate under a chosen username, see and
iterate on shared work, and open older days through a lazy-loaded archive.
Access is gated by three independent layers.

## The three layers

| Layer | What it stops | Where |
|---|---|---|
| Dedicated nginx hostname + secret path | Direct-IP scans and probes for other hosted sites never route here; hostname discovery still does not reveal the path | `nginx-multiimageclient.conf` |
| App login (username/password → long-lived cookie) | Anyone without credentials you handed out | `ui-auth.json` via `UiAuthFilePath` in settings.json |
| Loopback bind | Direct access to Kestrel; nginx is the only public listener | built-in (`127.0.0.1` only) |

## Routine production release

This repository currently has no hosted `.github/workflows` pipeline. The
release gate is the local equivalent:

```bash
node --check MultiImageClient/Ui/wwwroot/app.js
dotnet test MultiImageClient.sln --no-restore
git status --short --branch
```

Push the intended commit to GitHub only after those checks pass. From the
current development workstation, release that commit with:

```bash
ssh tpbeta-root \
  'sudo -u tparkour -H bash /home/tparkour/multiImageClient/deploy/agent-redeploy.sh'
```

Do not use plain `ssh tpbeta` here: on this workstation that alias logs in as
`subcreation`, which does not own this checkout or service. Do not search for
another checkout or deploy a similarly named service.

`tparkour` in the command is only the Unix account that owns this repository's
server checkout and .NET installation. It does not identify Terrain Parkour as
the product being deployed. The physical host serves several projects; this
release remains scoped to MultiImageClient.

`agent-redeploy.sh` fast-forwards `/home/tparkour/multiImageClient`, publishes
to a staging directory, and invokes the locked-down update helper. The helper
rsyncs only into `/opt/multiimageclient` and restarts only
`multiimageclient-ui.service`. It does not edit nginx or any neighboring
service during a routine release. Use this automation for normal releases;
do not substitute host-wide rsync, service restarts, or nginx changes.

After it returns, require all of the following:

- the remote checkout commit equals the pushed commit;
- `multiimageclient-ui.service` is active with a new start timestamp;
- the loopback HTTP probe succeeds;
- the private public vhost still answers through its existing secret path.

Never print the secret path, login credentials, cookies, or provider keys in
deployment output.

## Passwordless redeploy helper installation

After the shared site is installed, give the deploy user **one** interactive
sudo to install a locked-down helper. Forever after, agents can redeploy
without a password or TTY:

```bash
# One-time helper installation as root:
ssh tpbeta-root \
  'bash /home/tparkour/multiImageClient/deploy/install-agent-deploy.sh'

# Routine release after installation:
ssh tpbeta-root \
  'sudo -u tparkour -H bash /home/tparkour/multiImageClient/deploy/agent-redeploy.sh'
```

That grants `NOPASSWD` only for `/usr/local/sbin/multiimageclient-update`
(rsync staging → `/opt`, force-restart the unit). It does not allow arbitrary
root commands. Re-run the installer after changing `update-shared-host.sh` so
`/usr/local/sbin` stays in sync.

## Setup steps

1. **App**: publish under `/opt/multiimageclient`; keep the private settings
   separately under `/etc/multiimageclient/settings.json`. The systemd unit
   selects it with `MULTIIMAGECLIENT_SETTINGS`, so no secret file sits in the
   code tree. The following is a conservative **greenfield baseline**, not a
   snapshot of current production:
   ```json
   "UiAuthFilePath": "/etc/multiimageclient/ui-auth.json",
   "ImageDownloadBaseFolder": "/var/lib/multiimageclient/saves",
   "LogFilePath": "/var/lib/multiimageclient/logs/multiimageclient.log",
   "UiMaxConcurrentJobs": 1,
   "UiMaxConcurrentGenerators": 2,
   "UiMaxPendingJobs": 64,
   "UiTargetConcurrency": {
     "openai": 2,
     "xai-api": 1,
     "grok-web-ws": 1,
     "google": 2,
     "bfl": 2,
     "ideogram": 1,
     "recraft": 1,
     "comfyui": 1
   },
   "UiMinimumFreeDiskBytes": 3221225472
   ```
   `UiMaxConcurrentGenerators` is the process-wide request cap.
   `UiTargetConcurrency` applies provider/account caps beneath it.
   `UiMaxConcurrentJobs` now gates only memory-heavy contact-sheet
   finalization, so one slow job does not prevent later jobs from using
   unrelated targets.
   Current production was verified 2026-08-06 at 1 finalizer, 14 aggregate
   requests, default 64 pending jobs, scheduler-default lane caps, and a 3 GiB
   reserve. `/etc/multiimageclient/settings.json` is the source of truth for
   live app-level caps.
2. **Auth file**: copy `ui-auth.example.json` to
   `/etc/multiimageclient/ui-auth.json`, set a
   long random `secret` (`openssl rand -hex 24`), add one account per friend.
   Make both config files `root:multiimageclient` mode `0640`; never commit
   either one.
   - **Invalidate someone**: delete their account line, or change their
     password. Their saved browser cookie dies within ~1 second (the file is
     re-read on change). No restart needed.
   - Blank `UiAuthFilePath` = auth off (local development unchanged).
3. **systemd**: create the unprivileged `multiimageclient` system user, then
   install `multiimageclient-ui.service`. It confines writes to
   `/var/lib/multiimageclient`, caps memory/CPU/I/O, and gives the process no
   Linux capabilities.
4. **nginx**: `nginx-multiimageclient.conf` is a named vhost, never a default
   vhost. This is intentional on shared nginx servers: it cannot take routing
   away from existing applications. Replace `REPLACE_HOSTNAME` and the
   `REPLACE-WITH-LONG-RANDOM-SEGMENT` secret path (`openssl rand -hex 16`).
   Share the full URL `https://host/SECRET/` only with your friends.
5. **TLS without disturbing other vhosts**: first install only the template's
   port-80 server and ACME location, validate with `nginx -t`, and reload.
   Then run:
   ```sh
   certbot certonly --webroot -w /var/www/letsencrypt \
     -d REPLACE_HOSTNAME --non-interactive --agree-tos \
     --register-unsafely-without-email
   ```
   Install the final TLS block, run `nginx -t` again, then reload (never
   restart) nginx. Note: every issued
   certificate is published in public certificate-transparency logs, so the
   HOSTNAME is discoverable — that's why the secret is in the PATH, which CT
   logs never see.

## How users experience it

- First visit: minimal login page. One login, then the cookie lasts ~10 years
  (until you revoke it).
- Top bar: "creating as [name]" — required before generating, saved in the
  browser, prefilled with their login name. Filter chips (`everyone` /
  per-person) control whose jobs are shown; multi-select works.
- The feed shows today's jobs live; the archive below lists earlier days
  (yesterday, then dated) and loads a day's full history on click — with
  working copy-prompt, set-active, and image viewer.

## Operational notes

- All history lives under `{ImageDownloadBaseFolder}/UiHistory/` +
  `saves/<day>/`; the archive endpoints serve straight from what
  `UiJobStorage` already persists. Server restarts keep everything.
- **Do not move the saves folder between OSes**: `images.json` records
  absolute paths, so history written under `/mnt/c/...` (WSL) or `C:\...`
  (Windows) only serves images on the OS that wrote it.
- Watch spend in the UI cost bar per session; the SQLite generation archive
  records per-attempt costs for real accounting.
- On colocated hosts, the free-space guard rejects new image/video jobs before
  reading uploads once the configured reserve is reached. Active jobs are
  still allowed to finish; reserve several GiB above their plausible output.
