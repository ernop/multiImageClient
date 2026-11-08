# UI polling

## Why pages poll (settled 2026-07-27)

The UI holds zero persistent connections.
Plain-HTTP localhost has no HTTP/2.
Each SSE or WebSocket stream holds one of about six HTTP/1.1 sockets per origin, across all tabs.
Long-lived streams starved image loads twice on 2026-07-27.
Pages therefore send short polls. Cursor polls return only what changed since the last reply.

## Idle tabs stop polling (2026-10-10)

Owner requirement, 2026-10-10: polling stops when the tab itself has no action for a while.

### Behavior

- `tab-activity.js` records input in the tab.
  Input means pointer down, pointer movement, key press, wheel, touch, focus, or the tab becoming visible.
- After 10 minutes without input, every poll loop on the page stops sending requests.
- The next input or tab reveal resumes every loop at once.
- Cursor polls then fetch everything they missed: job events, activity, logs, and goal-loop entries.
- Exception: the composer keeps polling while one of the current user's own jobs is unfinished.
  This exception lasts at most 60 minutes after the last input.
  Completion notifications for one's own generations need the job-event poll.
- The goal-loop page has no exception. It sends no notifications.
- A paused page says so. Running job cards and the open logs panel read **updates paused while idle**.
  The goal-loop page header shows the same text.
- Fixed-interval refreshes skip a tick while their previous request is unfinished.
  This applies to the Vibecoders sent list, favorites, profiles, and RAM status.
- The server is unchanged. Every request except `/healthz` still counts as activity for the idle-sleep timer.

### Rationale

- A visible composer tab sent about 7,400–8,200 requests per hour, roughly two per second.
  A hidden tab sent about 2,900–3,000 per hour. Each open tab added that load.
- The original environment sleeps after 15 minutes without requests.
  Open tabs reset that timer indefinitely; see [the lifecycle contract](workspaces-prd.md#original-environment-sleeps-on-demand-2026-09-09).
- During the Vibecoders freeze of 2026-10-09, single-flight loops slowed to one request per proxy timeout.
  The Vibecoders-sent and RAM-status timers had no in-flight guard.
  They stacked a new request on every tick, about 1,160 requests per hour from one admin tab.
  See [the incident record](shared-host-memory-budget.md#vibecoders-freeze-and-owner-decisions--2026-10-10).

### Settled values

| Setting | Value | Where |
|---|---|---|
| Idle limit | 10 minutes without input | `IdleAfterMs` in `tab-activity.js` |
| Own-work limit | 60 minutes after the last input | `OwnWorkLimitMs` in `tab-activity.js` |

Both are declared defaults, not user settings. They do not enter the portable personal configuration.

## Server restarts keep open pages (2026-11-07)

Owner report, 2026-11-07: a reopened tab showed its content for about a second, then reloaded itself.

### Cause

- Since 2026-08-13, each server process has a random token, `clientInstanceId` in `/api/config`.
- The composer sends the token with each job-event poll. A different process answers HTTP 401.
- Until 2026-11-07, the page reloaded on every such 401.
- The original environment sleeps after 15 idle minutes. The next request starts a new process.
- Since 2026-10-10, idle tabs stop polling. An open tab therefore no longer keeps the original awake.
- A tab that returns after a sleep wakes the server, receives a 401, and reloads.
- Its old content stays visible while the new process starts.
- The reload scrolls to the top. It drops attached images and open dialogs, such as the sketch canvas.
  The per-tab draft store keeps the prompt text.

Process starts in the systemd journals, 2026-10-24 to 2026-11-07:

| Instance | Process starts | Starts after an idle sleep |
|---|---|---|
| Original | 73 | 66 |
| Vibecoders | 11 | none; it stays resident |
| Local unit on `PC` | 60, including 43 on 2026-11-03 | none; it stays resident |

### Behavior

- On a 401 from the job-event poll, the composer reads `/api/config` once.
- The page stays when the build commit is unchanged and is a plain hexadecimal hash.
  It adopts the new token and replays today's job events from cursor 0, as a fresh load does.
- The replay sends no own-job notifications. A fresh load sends none either.
- The logs panel restarts at the new process's first line. Each process numbers its own lines.
- The page reloads when the commit changed, ends in `-dirty`, or reads `untracked-build`.
- The page also reloads when `/api/config` answers 401. That answer means the login ended.
- A failed `/api/config` read counts as a failed poll.
  The page shows **server disconnected — retrying** and polls again.
- The server is unchanged. It still answers a stale token with 401.
  Pages from older releases therefore reload once.

### Rationale

- The reload dates from 2026-08-13. A tab then ran old viewer code against a newer server.
- A process of the same committed build serves the same page code. That risk therefore does not apply.
- Release builds come from clean checkouts.
  Production reported only plain hashes in the three days before this change.
- Other poll cursors survive a restart:
  - Activity, developer requests, and profiles count in SQLite.
  - Favorites, deletions, and the Vibecoders sent list put the process token in their versions.
    A new process therefore sends complete snapshots.
- The job-event log and the log lines count within one process. Adoption restarts both.
- A replay produces the same cards. Replays already ran within one process after envelope trims.
- A failed result's provider hint stacked one more copy on each replay. It now appears once.

### Limits

- The page reads its generator catalog, availability, and archive day list only at load.
  A restart with changed settings but the same commit leaves them stale until a manual reload.
  Changes during one process already behave the same way.
- Each release changes the commit. Open tabs therefore still reload after a release.
- Known defect, not fixed: the envelope log drops its oldest half above 8,000 entries.
  A cursor inside the shortened log then reads a shifted slice and misses events.
  Neither production journal shows a trim from 2026-09-27 to 2026-11-07.

### Rule for new code

A new composer poll whose cursor counts within one process must restart that cursor in `adoptRestartedServerProcess` in `app.js`.

## Poll loops (as of 2026-10-10)

| Page | Loop | Visible tab | Hidden tab |
|---|---|---|---|
| Composer | job events, `api/events/poll` | 1 s | 5 s |
| Composer | activity, `api/activity/poll` | 2.5 s | 10 s |
| Composer | developer requests (developer only, within the activity poll) | 5 s | 30 s |
| Composer | logs, `api/logs/poll` (panel open) | 1 s | 1 s |
| Composer | favorites, `api/favorites` | 5 s | 30 s |
| Composer | profiles, `api/profiles` | 5 s | 5 s |
| Composer | Vibecoders sent list, `api/discord/vibecoders` | 5 s | 5 s |
| Composer | RAM status, `api/status` (admin header only) | 15 s | 15 s |
| Goal loops | loop list, `api/goal-loops` | 4 s | 15 s |
| Goal loops | selected loop, `api/goal-loops/{id}` | 1 s running, 3 s otherwise | 5 s |

Every loop stops while the tab is idle, except the composer's own-work exception above.
A new poll loop must check `MultiImageTabActivity.shouldPoll()` before each timer-driven request.
It must register an `onResume` handler that restarts it.

## Files

- `MultiImageClient/Ui/wwwroot/tab-activity.js`: input tracking, idle decision, resume handlers.
- `MultiImageClient/Ui/wwwroot/app.js`: composer loops, own-work exception, `refreshEvery` guard, server-restart replay (`adoptRestartedServerProcess`).
- `MultiImageClient/Workflows/UiWorkflow.cs`: the process token in `/api/config` and the 401 on `/api/events/poll`.
- `MultiImageClient/Ui/wwwroot/goal.js`: goal-loop list and loop polls.
- `MultiImageClient/Ui/wwwroot/index.html`, `goal.html`: load `tab-activity.js` before the page script.
- `MultiImageClient/Ui/wwwroot/style.css`: `.job-connection.paused`.

## Tests

- `node --test MultiImageClient/Ui/tests/tab-activity.test.js` covers the idle limit, every input kind, tab reveal, resume handlers, and the own-work limit.
- `node tools/test-idle-polling-browser.cjs` drives the local UI with a fake page clock.
  It checks that both pages poll while active, send no polls while idle, and resume on input.
  It also checks the own-work exception. It sends only GET requests.
- `node tools/test-server-restart-browser.cjs` gives a local composer a stale process token.
  The server then answers as after a restart.
  The test checks that the same committed build replays in place and leaves every card unchanged.
  It checks that a replayed failure keeps one provider hint, and that a failed config read retries.
  It checks that a changed build, an uncommitted build, and an ended login reload. It sends only GET requests.
- Both browser tests need the `playwright` package. Set `NODE_PATH` to an install that matches the cached Chromium.
