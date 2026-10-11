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
- `MultiImageClient/Ui/wwwroot/app.js`: composer loops, own-work exception, `refreshEvery` guard.
- `MultiImageClient/Ui/wwwroot/goal.js`: goal-loop list and loop polls.
- `MultiImageClient/Ui/wwwroot/index.html`, `goal.html`: load `tab-activity.js` before the page script.
- `MultiImageClient/Ui/wwwroot/style.css`: `.job-connection.paused`.

## Tests

- `node --test MultiImageClient/Ui/tests/tab-activity.test.js` covers the idle limit, every input kind, tab reveal, resume handlers, and the own-work limit.
- `node tools/test-idle-polling-browser.cjs` drives the local UI with a fake page clock.
  It checks that both pages poll while active, send no polls while idle, and resume on input.
  It also checks the own-work exception. It sends only GET requests.
