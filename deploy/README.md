# Production Deployment — Private MultiImageClient Site

This repository has one production product target: the owner's private shared
image-making site at
`https://multiimageclient.alpha.fuseki.net/<private-path>/`. The path segment
is secret and must never be printed or committed. Changing the hostname, path,
TLS, or nginx is a separate migration, not a release step.

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

## Commit and push (default agent sequence)

A request to commit and push, including the Cursor diff-tab commit-and-push
action, is a full local+production release unless the user excludes a step.

1. Commit the requested files under the repository git safety rules.
2. Run the release gate below.
3. Push the requested branch to GitHub.
4. Rebuild and restart the workstation local `--ui` with
   `tools/restart-local-ui.sh`. That process listens on `127.0.0.1:5960`.
   In-flight local jobs die. Do not use `tools/start-ui.sh`; that file is
   the Surface/WSL launcher.
5. If the push updated `origin/master`, redeploy production with the
   `tpbeta-root` command below. Skip production when the push did not
   update `origin/master`. Feature-branch pushes still restart the local
   site.
6. Verify local `http://127.0.0.1:5960/healthz`, then the production
   checks listed after the redeploy command.

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
   `/etc/multiimageclient/ui-auth.json`. The file is version 2: `enabled` must
   be `true`, `secret` must be at least 32 characters
   (`openssl rand -hex 24`), and each account stores a PBKDF2-SHA256 hash
   rather than a password. Generate a hash with
   `python3 deploy/migrate-ui-auth-v1-to-v2.py --emit-hash 'passphrase'`.
   The example file's hashes are for `example-passphrase-not-for-production`
   and `another-example-passphrase-not-for-production`; replace them. A
   version-1 plaintext file is a hard startup error. Stop
   `multiimageclient-ui` before migrating an existing production file: the
   old binary cannot read version 2, and the new binary rejects version 1.
   Then run
   `python3 deploy/migrate-ui-auth-v1-to-v2.py /etc/multiimageclient/ui-auth.json`
   (keeps `ui-auth.json.pre-hash-v1` until you verify login, then delete that
   plaintext backup), deploy the new binary, and start the unit. Existing
   cookies die at migration; everyone logs in again. Make both config files
   `root:multiimageclient` mode `0640`; never commit either one.
   - **Invalidate someone**: delete their account line, or replace their
     `passwordHash`. Their saved browser cookie dies within ~1 second (the
     file is re-read on change). No restart needed.
   - Blank `UiAuthFilePath` = auth off (local development unchanged).
     `enabled: false` is rejected, not an open mode.
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

## Additional isolated environments

See [the environment setup guide](../docs/workspaces-prd.md#operator-setup-and-recipient-flow).
Use `create-environment.py` to prepare, install, or update an explicitly selected additional instance.
It creates another private path under the existing hostname.
It preserves the original service and its data.
Routine releases above continue targeting only `multiimageclient-ui.service`.


### Global administration bootstrap (2026-09-09)

Run `sudo bash deploy/install-environment-controller.sh` on the production host after updating its checkout.
The script preserves existing account hashes and prepares the selected Vibecoders environment.
Release the original application through `deploy/agent-redeploy.sh`.
Then run `sudo systemctl enable --now multiimageclient-environment-controller.timer`.
Run the controller service once and verify both public sites before reporting completion.
Use the existing owner username and password; do not generate another owner account.
See the [canonical global-account contract](../docs/workspaces-prd.md) for permissions, data boundaries, and capacity limits.

## Original on-demand lifecycle (2026-09-09)

The owner selected socket activation for the original environment; Vibecoders remains resident.
Release the socket-aware binary, then run `python3 deploy/install-original-on-demand.py` as root on the production host.
The installer preserves the URL and enables `multiimageclient-ui.socket` on the existing loopback port.
The original application exits after 15 idle minutes, provided no jobs, requests, or goal loops remain active.
The next connection wakes it. Existing passwords and stored work remain unchanged.
Health probes do not extend the idle timeout, but probing a sleeping socket wakes the application.
Use `systemctl is-active multiimageclient-ui.socket` to check availability without waking it.
Routine releases stop the activation socket before stopping the original service and restore it before verification.
Read [the lifecycle contract](../docs/workspaces-prd.md#original-environment-sleeps-on-demand-2026-09-09) before changing these units.
