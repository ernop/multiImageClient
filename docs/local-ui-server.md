# Local UI server maintenance

Reviewed 2026-09-10.

## Findings and decisions

The local server used removed .NET 10.0.11 runtime files after the workstation installed 10.0.12.
`/healthz` still returned `ok`, but `/api/status` returned HTTP 500 when loading `System.Diagnostics.Process`.
Restart the local UI after replacing its .NET runtime.
Verify `/api/config` and `/api/status` after startup, alongside `/healthz`.

The old restart script killed all matching UI processes and any process occupying the configured port.
It also stopped the server before building.
A failed build therefore left the local site unavailable.

## Restart contract

Run `tools/restart-local-ui.sh` from this checkout on Linux.
The shell entry point invokes `tools/restart_local_ui.py` with Python's standard library.
The launcher requires .NET 10, `ss`, and Python with Linux process-descriptor support.

| Requirement | Behavior |
|---|---|
| Default address | Listen on `127.0.0.1:5960`. |
| Port selection | `MIC_UI_PORT` accepts integers from 1 through 65535. |
| Exact process | Check the listener's user, working directory, executable path, and complete UI arguments before signaling. |
| Ambiguity | Refuse foreign, wildcard, multiple, or unidentified listeners. |
| Signal identity | Use a Linux process descriptor, which prevents signals from reaching a reused process identifier. |
| Build isolation | Publish into a new `.local-ui/<port>/release-*/app` directory before stopping the existing process. |
| Build failure | Retain the running server when publishing fails. |
| Shutdown | Allow 40 seconds for termination, then kill only the verified process if necessary. |
| Startup | Start the published DLL directly in a separate process session. |
| Readiness | Require the started process to own the exact loopback listener and return HTTP 200 with `ok` from `/healthz`. |
| Probe timeout | Bound each HTTP probe to two seconds and startup polling to 60 seconds. |
| Failed startup | Stop the new child and report failure. Do not claim success from another listener. |
| Concurrent restarts | Lock each checkout's configured port during publishing, shutdown, and startup. |
| Generated files | Remove failed releases and obsolete releases for that port. Keep the active published directory. |
| Process record | Store the verified process identifier in `.local-ui/<port>/server.pid`. Never use a saved identifier as authority to kill. |
| Logs | Use `/tmp/mic-ui-local.log`, or `/tmp/mic-ui-local-<port>.log` for other ports. `MIC_UI_LOCAL_LOG` overrides the path. |

The launcher recognizes the repository's former apphost launch and its new direct-DLL launch.
Both use `--ui`, optional explicit `--ui-port`, and optional `--ui-no-open`, in that order.
Unrecognized commands require inspection instead of broad process matching.

Settings, saved images, history, and source files remain outside generated release cleanup.
The app retains its existing settings lookup and source-tree static-file selection.
The runtime remains framework-dependent, so workstation runtime replacement still requires a restart.
In-flight local jobs can be interrupted during restart.
Production releases continue to use `deploy/agent-redeploy.sh` under the existing release policy.

## Compatible dependency updates

The 2026-09-10 review updates these libraries across all referencing projects.

| Library | Before | After | Verified source |
|---|---|---|---|
| Magick.NET Core and Q16 AnyCPU | 14.16.0 | 14.17.1 | [NuGet](https://www.nuget.org/packages/Magick.NET-Q16-AnyCPU/14.17.1) |
| Microsoft.Data.Sqlite.Core | 10.0.10 | 10.0.12 | [NuGet](https://www.nuget.org/packages/Microsoft.Data.Sqlite.Core/10.0.12) |
| Newtonsoft.Json | 13.0.3 | 13.0.4 | [NuGet](https://www.nuget.org/packages/Newtonsoft.Json/13.0.4) |
| SixLabors.ImageSharp | 3.1.11 | 3.1.12 | [NuGet](https://www.nuget.org/packages/SixLabors.ImageSharp/3.1.12) |
| SixLabors.ImageSharp.Drawing | 2.1.4 | 2.1.7 | [NuGet](https://www.nuget.org/packages/SixLabors.ImageSharp.Drawing/2.1.7) |
| SQLitePCLRaw.provider.dynamic_cdecl | 3.0.4 | 3.0.5 | [NuGet](https://www.nuget.org/packages/SQLitePCLRaw.provider.dynamic_cdecl/3.0.5) |

ImageSharp 4 and Drawing 3 require a separate major-version migration.
Provider SDK upgrades remain outside this server maintenance change.
These library updates preserve provider request contracts.
The baseline NuGet audit reported no known vulnerabilities in direct or transitive application packages.

## Upstream sync and local verification

The 2026-09-10 review fast-forwarded local `master` through upstream commit `5f73b3fc838018b1436264962a9e936a0d2086f2`.
This included 13 upstream commits after `9fa101cb5a6c`.
Upstream additions include GPT Image 2.5, Grok Imagine 2.0, configurable environments, and adaptive goal-loop search.
The local server runs this upstream revision with the uncommitted maintenance changes.
This task did not push code or deploy production.

The full solution passed 384 C# tests with `DOTNET_EnableWriteXorExecute=0`.
The JavaScript suites passed 24 tests, and the launcher suite passed 10 tests.
Two local restarts verified migration from the old executable and replacement of the new published-DLL process.
The running server reports .NET 10.0.12 and the exact recorded process identifier.
Health, configuration, status, jobs, goal loops, and archive-day endpoints returned HTTP 200 after restart.
The browser displayed the new model selectors and approximately 144 MiB of process memory.
The NuGet audit after the library updates reported no known vulnerabilities in direct or transitive application packages.
Compiler warnings remain.
These checks did not exercise paid provider generation or Discord posting.

## Status API

`GET /api/status` adds `processId`, `runtimeVersion`, and `processStartedAtUtc` to its existing memory and scheduler fields.
These identify the running process after a restart.
The existing authentication policy still applies.
`/healthz` remains content-free and independent of authentication.

Memory measurements use only the current process's recorded Linux control group, which defines its resource limits.
Missing membership produces unavailable values, never another service's measurements.
Without a control-group limit, the header reports this server process's working set.
An uncapped desktop control group can include unrelated applications.
The review observed 17.21 GiB for that group while the server used approximately 140 MiB.
Limited deployments retain group usage against their configured limit.
The tooltip identifies the displayed measurement and separately labels the group total.
See [shared-host-memory-budget.md](shared-host-memory-budget.md).

## Verification commands

```bash
python3 -m unittest discover -s tools/tests -p 'test_restart_local_ui.py' -v
DOTNET_EnableWriteXorExecute=0 dotnet test MultiImageClient.sln -c Release
node --test MultiImageClient/Ui/tests/*.test.js tools/tests/*.test.cjs
tools/restart-local-ui.sh
curl --fail --max-time 5 http://127.0.0.1:5960/healthz
curl --fail --max-time 5 http://127.0.0.1:5960/api/status
```

The restart tests cover build failure, changed port ownership, unrelated processes, and exact process termination.
`UiProcessMemoryTests` covers process identity and missing or invalid control-group membership.
The RAM-header tests distinguish desktop process memory from a limited service's total memory.
