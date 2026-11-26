# Web UI Usage Telemetry & Build Freshness Badge (2026-11-26)

## Purpose

1. **Respectful usage telemetry:** record structured UI actions on disk so we can see which composer tools, Stage-1 flows, character helpers, feed controls, viewer interactions, and goal-loop controls people actually use, and where errors or aborts occur, without capturing private prompt content.
2. **Browser-relative build freshness:** display how long ago the running server build was updated (for example `build 9cee551 · updated 7 minutes ago`) relative to the user's browser clock, so the owner can tell at a glance whether a deploy is live.

## Telemetry storage and privacy boundary

- **Storage:** `UiTelemetryStore` (`MultiImageClient/Implementation/UiTelemetry.cs`) appends validated UTC JSONL lines to `saves/UiTelemetry/telemetry-YYYY-MM-DD.jsonl` inside each instance's own data root (`0700` directory, `0600` files on Unix). Zero in-memory event cache; daily cap 20 MiB; files older than 90 days are pruned on startup and day rollover.
- **No surveillance or content capture:** server and client both reject keys containing `prompt`, `text`, `instruction`, `description`, `notes`, `draft`, `cookie`, `password`, `token`, `secret`, `email`, `address`, `url`, `query`, `content`, `body`, `header`, `useragent`, `ua`, or `ip`. Only scalar metadata (`<= 16` fields, strings `<= 80` chars, no nested objects/arrays) is accepted.
- **Identity:** `actor` and `role` (`admin` / `normal` / `local`) come strictly from server auth context, never from client input. Each browser tab generates an ephemeral random `tab` ID in `sessionStorage` only.
- **Idle-sleep safety:** `telemetry.js` never flushes an empty queue. `POST /api/telemetry` does not extend `UiIdleLifetime` and does not churn `account-activity.json`.
- **Rate & size limits:** at most 64 events per batch, 32 KiB per request, and 240 events per minute per actor (HTTP 429 on excess).

## Endpoints and tooling

- `POST /api/telemetry` — accepts `{ tab, page, events: [{ ev, seq, d }] }` from signed-in users (or local), validates fail-closed, appends to today's JSONL file.
- `GET /api/admin/telemetry/summary?days=14` — admin-only (HTTP 403 for non-admins); streams daily JSONL files line-by-line and returns counts by event, page, actor, day, sub-actions, and viewer dwell median.
- `tools/telemetry-summary.py [dir] [--days N]` — read-only CLI summary tool for local or production `saves/UiTelemetry` directories.

## Client instrumentation

`MultiImageClient/Ui/wwwroot/telemetry.js` (`window.MultiImageTelemetry`) loads on `index.html`, `goal.html`, `prompt-archive.html`, and `admin.html`. It tracks:

- `page.open` (viewport bucket) and `page.hide` (visible dwell ms)
- `composer.generate`, `composer.prompt_action`, `composer.tool`
- `stage1.mode`, `stage1.preset`, `stage1.auto_render`, `stage1.run`, `stage1.card_done`, `stage1.card_action`
- `character.action`
- `feed.action` (including split-group round-trips)
- `viewer.open`, `viewer.close` (dwell, unique items viewed, nav steps, action count), `viewer.action`
- `goal.start`
- `client.error` (kind + basename only; never full URLs or secret paths)

## Build freshness badge

- `MultiImageClient.csproj` embeds UTC compile time (`MicBuildTimeUtc`) alongside `MicCommitDate` and `SourceRevisionId`.
- `UiBuildInfo.BuiltAtUtc` exposes it through `/api/config` (`build.builtAtUtc`). Using compile time reflects local rebuilds and production deploys accurately without resetting when AVE wakes from idle sleep.
- `MultiImageClient/Ui/wwwroot/build-badge.js` (`window.MultiImageBuildBadge`) renders `build <hash> · updated <relative age>` in `#build-info` across `index.html`, `goal.html`, `prompt-archive.html`, and `admin.html`, ticking every 30 seconds while visible and active, and shows exact local build and commit times in the tooltip. On shared sites, `#build-info` stays admin-only (`environment-storage.js`).

## Files

- `MultiImageClient/Implementation/UiTelemetry.cs`
- `MultiImageClient/Workflows/UiWorkflow.Telemetry.cs`
- `MultiImageClient/Implementation/UiBuildInfo.cs`
- `MultiImageClient/Ui/wwwroot/telemetry.js`
- `MultiImageClient/Ui/wwwroot/build-badge.js`
- `tools/telemetry-summary.py`
- `MultiImageClient.Tests/UiTelemetryTests.cs`
- `MultiImageClient/Ui/tests/telemetry.test.js`
- `MultiImageClient/Ui/tests/build-badge.test.js`
