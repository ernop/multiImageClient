# Shared-Site Deployment (tpbeta or any Linux box)

The `--ui` web app is now a shared site: everyone generates under a chosen
username, everyone sees (and can iterate on) everyone's work, older days live
in a lazy-loaded archive, and access is gated by three independent layers.

## The three layers

| Layer | What it stops | Where |
|---|---|---|
| nginx default-deny + secret path | Bots, IP scanners, hostname sprayers ever *finding* the site | `nginx-multiimageclient.conf` |
| App login (username/password → long-lived cookie) | Anyone without credentials you handed out | `ui-auth.json` via `UiAuthFilePath` in settings.json |
| Loopback bind | Direct access to Kestrel; nginx is the only public listener | built-in (`127.0.0.1` only) |

## Setup steps

1. **App**: clone + build as usual; copy your `settings.json` in and add:
   ```json
   "UiAuthFilePath": "/home/youruser/multiImageClient/ui-auth.json"
   ```
2. **Auth file**: copy `ui-auth.example.json` → `ui-auth.json` (same folder as
   settings.json is fine, it's already gitignored territory — verify!), set a
   long random `secret` (`openssl rand -hex 24`), add one account per friend.
   `chmod 600 ui-auth.json`.
   - **Invalidate someone**: delete their account line, or change their
     password. Their saved browser cookie dies within ~1 second (the file is
     re-read on change). No restart needed.
   - Blank `UiAuthFilePath` = auth off (local development unchanged).
3. **systemd**: `multiimageclient-ui.service` (edit user/paths).
4. **nginx**: `nginx-multiimageclient.conf` (edit hostname, cert paths, and
   the `REPLACE-WITH-LONG-RANDOM-SEGMENT` secret path — `openssl rand -hex 16`).
   Share the full URL `https://host/SECRET/` only with your friends.
5. TLS via certbot/Let's Encrypt for the real hostname. Note: every issued
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
