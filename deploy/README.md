# Shared-Site Deployment (tpbeta or any Linux box)

The `--ui` web app is now a shared site: everyone generates under a chosen
username, everyone sees (and can iterate on) everyone's work, older days live
in a lazy-loaded archive, and access is gated by three independent layers.

## The three layers

| Layer | What it stops | Where |
|---|---|---|
| Dedicated nginx hostname + secret path | Direct-IP scans and probes for other hosted sites never route here; hostname discovery still does not reveal the path | `nginx-multiimageclient.conf` |
| App login (username/password → long-lived cookie) | Anyone without credentials you handed out | `ui-auth.json` via `UiAuthFilePath` in settings.json |
| Loopback bind | Direct access to Kestrel; nginx is the only public listener | built-in (`127.0.0.1` only) |

## Agent / passwordless redeploys

After the shared site is installed, give the deploy user **one** interactive
sudo to install a locked-down helper. Forever after, agents can redeploy
without a password or TTY:

```bash
# ONE TIME (password prompt):
ssh -t tpbeta 'sudo bash ~/multiImageClient/deploy/install-agent-deploy.sh'

# EVERY UPDATE (no password — agents run this):
ssh tpbeta 'bash ~/multiImageClient/deploy/agent-redeploy.sh'
```

That grants `NOPASSWD` only for `/usr/local/sbin/multiimageclient-update`
(rsync staging → `/opt`, force-restart the unit). It does not allow arbitrary
root commands. Re-run the installer after changing `update-shared-host.sh` so
`/usr/local/sbin` stays in sync.

## Setup steps

1. **App**: publish under `/opt/multiimageclient`; keep the private settings
   separately under `/etc/multiimageclient/settings.json`. The systemd unit
   selects it with `MULTIIMAGECLIENT_SETTINGS`, so no secret file sits in the
   code tree. Add:
   ```json
   "UiAuthFilePath": "/etc/multiimageclient/ui-auth.json",
   "ImageDownloadBaseFolder": "/var/lib/multiimageclient/saves",
   "LogFilePath": "/var/lib/multiimageclient/logs/multiimageclient.log",
   "UiMaxConcurrentJobs": 1,
   "UiMaxConcurrentGenerators": 2,
   "UiMinimumFreeDiskBytes": 3221225472
   ```
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
  working copy-prompt, set-active, image viewer, and video follow-ups.

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
