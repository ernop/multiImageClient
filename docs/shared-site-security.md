# Shared-Site Security & Malicious-Use Analysis

Written 2026-08-03, when the `--ui` web app gained the shared-site layer
(usernames, day archive, login gate, nginx kit in `deploy/`). Threat scenario
requested by the owner: **an attacker who has obtained a friend's
username/password (or their logged-in browser).** What can they actually do?

## What the attacker gets

A valid login is full app access. There are no roles: every authenticated
user can create jobs on every configured provider, see all history, and pull
every stored image. That is the product design (shared, no privacy), so a
compromised friend account = a hostile user with complete member powers.

## Damage they CAN do

1. **Spend your API credits** (acknowledged; you monitor this). Ceiling per
   job is bounded (n ≤ 10, 4 jobs running concurrently) but total volume is
   not — there are **no per-user quotas or spend caps**. A script hammering
   `POST /api/jobs` burns money as fast as the providers accept it.
2. **Abuse your consumer accounts — worse than credits.** `grok-web`,
   grok-web video ("Spicy" mode included), and `meta-web` run through *your*
   logged-in grok.com / meta.ai sessions. A malicious user can generate
   ToS-violating content **attributable to your personal X/Meta accounts**,
   risking bans of accounts you care about. If you don't fully trust every
   invitee, consider not configuring grok-web/meta-web cookies on the shared
   box, or handing out accounts only to people you'd lend your phone to.
3. **Fill the disk / exhaust memory (site takedown, not takeover).** Every
   upload and result is archived under `saves/`, job event logs grow
   unboundedly, and each job's images are also held in process memory for the
   life of the process. Sustained flooding → disk full or OOM → site down
   until you clean up and restart (systemd auto-restarts the process, but a
   full disk stays full). nginx's 64 MB body cap and rate limits slow this;
   they don't stop a patient authenticated attacker.
4. **Pollute shared history.** Offensive prompts/images are visible to
   everyone and mixed into everyone's archive; there is no delete API, so
   cleanup means removing `UiHistory/<jobId>/` folders and saved files on the
   server by hand. Usernames are not authenticated identity — any member can
   create under any name (deliberate: attribution, not privacy).

## Damage they CANNOT do (and why)

- **Read your API keys or cookies.** No endpoint serves settings.json,
  ui-auth.json, or arbitrary files. Static serving is locked to `Ui/wwwroot`;
  image serving reads only paths the server itself recorded in `images.json`.
  `/api/config` reports key *problems* ("OpenAIApiKey is empty"), never values.
  The SQLite archive redacts keys/cookies by design.
- **Escape into the server via inputs (by construction).** Prompts and
  usernames are data end-to-end: no shell interpolation, no SQL string
  concatenation (archive writes are parameterized), no reflection/eval.
  Prompt-derived filenames pass `FilenameGenerator.SanitizeFilename`
  (`[a-zA-Z0-9_-]` only, ≤200 chars) — no path traversal. Usernames are
  server-validated to `[A-Za-z0-9 ._-]{1,32}`. The frontend renders prompts,
  names, and provider errors via `textContent`, so stored XSS has no sink.
- **Ride another member's browser (CSRF/session theft).** The auth cookie is
  HttpOnly (no JS access), SameSite=Lax (cross-site POSTs don't carry it),
  and Secure behind the nginx TLS terminator.
- **Brute-force the login quietly.** Per-IP throttle in the app (10 failures
  / 5 min) plus nginx `limit_req` on the login route; failures are logged
  with IP.

## Realistic paths to actual takeover (ranked)

1. **Native image-codec exploitation — the one to respect.** Uploaded bytes
   are decoded by ImageSharp (managed; worst case historically is DoS) and
   also flow through **Magick.NET / native ImageMagick** during save/compose
   (`ImageSaving.cs`, `ImageCombiner.cs`). ImageMagick has a long CVE
   history; a crafted upload exploiting a fresh codec bug is the most
   plausible RCE route. Mitigations: keep Magick.NET updated (the NU190x
   advisories in the build are exactly this class of warning — bump it), and
   run under the hardened systemd unit in `deploy/` (`NoNewPrivileges`,
   `ProtectSystem=strict`, unprivileged user) so even successful code
   execution lands in a low-privilege, mostly read-only jail rather than
   root.
2. **Auth-file leak.** `ui-auth.json` holds plaintext passwords and the HMAC
   secret; anyone who reads it mints valid cookies for every account. It
   lives with settings.json (same sensitivity as your API keys), `chmod 600`,
   never in git. Friends' passwords should be unique to this site (they're
   pasted once, so hand out random strings).
3. **Secret-path leak.** The nginx path hides the site from scanners, not
   from anyone a friend shares the URL with. Browser history, pasted links,
   and screenshots leak it; the in-app `no-referrer` policy stops the Referer
   channel. Leaking it costs you layer 1 of 3 — rotate the path in the nginx
   config if it escapes.
4. **Kestrel/ASP.NET or nginx vulnerabilities.** Standard patching story;
   nothing app-specific. The app itself never listens publicly (loopback
   only).

## Deliberate non-mitigations (fail-closed elsewhere, open by design here)

- No per-user quotas, no delete/moderation API, no username authentication —
  the site is a high-trust shared studio for friends. The revocation lever
  (edit ui-auth.json → cookies die in ~1 s) is the moderation tool: pull an
  account first, clean the disk second.

## Owner's incident playbook

| Situation | Action |
|---|---|
| Friend's account misbehaving | Remove/repassword their entry in `ui-auth.json` (takes effect ≤1 s, no restart) |
| Everything on fire | `enabled: false` won't lock it (that opens the gate!) — instead remove all accounts, or stop the systemd unit |
| Secret path leaked | Change the path segment in nginx config, reload nginx, re-share |
| Suspect stolen HMAC secret / auth file | Rotate `secret` (logs everyone out), rotate all passwords, check server for other compromise |
| Disk filled by flood | Delete offending `saves/<day>` + `UiHistory/<jobId>` folders; restart unit |
