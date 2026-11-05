# .NET build helper processes

Status: decided 2026-10-31, released 2026-11-05.

Owner request, 2026-10-31: "if stuff like that's running, it's really not that good is it! how to prevent?"

## The helpers

`dotnet build`, `dotnet test`, `dotnet run` and `dotnet publish` can start helper processes.
MSBuild starts worker nodes (`MSBuild.dll /nodemode:1`), up to one for each CPU.
The C# compiler starts a shared compiler server (`VBCSCompiler`).
By default, the helpers stay after the build so the next build starts faster.
An idle worker exits after 15 minutes.
An idle compiler server exits after 10 minutes.

## Problems

A build reuses only helpers that a build in the same login session started.
Agent tool calls ran in separate sessions when measured.
Background jobs and the local service also run in their own sessions.
So agent builds never reuse helpers, and each build adds its own set.
`dotnet build-server shutdown` also reaches only helpers from its own session.

Measured on this PC (32 CPUs) with SDK 10.0.403 on 2026-11-05:

| Build | Time | Helpers left | Memory left |
|---|---|---|---|
| Default, first build | 7.1 s | 9 | 1.6 GB |
| Default, second build in the same session | 8.0 s | 9, reused | 1.8 GB |
| With `Directory.Build.rsp`, 3 runs | 7.0–7.3 s | 0 | 0 |

On 2026-10-30, three sessions left 28 workers and a compiler server, about 3 GB.

Helpers also keep the build's output pipe open.
A command such as `dotnet test … | grep` then waits until they exit.
On 2026-10-30, one such command waited 10 minutes.

A crashed or killed build leaves its workers behind for good.
Their parent is gone, but they never notice.
On 2026-11-05, 34 such workers had waited 21.6 hours.
They came from crash experiments that ran `dotnet test` repeatedly with Ubuntu's SDK.
[local-ui-server.md](local-ui-server.md) records that crash and the move to Microsoft's SDK.

The production host has 3.8 GiB of memory, 2 CPUs and no swap.
Helpers left after a publish compete there with both instances.

## Decision 1: no build servers in this repository

`Directory.Build.rsp` in the repository root adds three switches to every command-line build:

- `-nodeReuse:false`: workers exit when the build ends.
- `-p:UseSharedCompilation=false`: the compiler runs without a server.
- `-p:UseRazorBuildServer=false`: no Razor server starts.

`dotnet build --disable-build-servers` uses the same three switches.
MSBuild reads the file for every project under the repository root.
This covers agent builds, the local `dotnet run` service, clean worktrees and production publishes.
IDE design-time builds do not read the file.
`-noAutoResponse` makes one build ignore the file.

Cost: a build takes about as long as a first build with helpers.
In one terminal session, warm helpers can make a repeat build about 3 s faster.
Agent builds never had warm helpers, so they lose nothing.

## Decision 2: a timer stops orphaned helpers on this PC

The switches file does not help when a build crashes or is killed.
`tools/reap-build-helpers.sh` stops helpers whose parent died.
The user's systemd (or PID 1) adopts such a helper, so the script checks the parent.
It stops only helpers that are at least 15 minutes old.
Reusable helpers from other projects exit on their own within that time.
The script sends SIGTERM, then SIGKILL after 10 seconds to helpers that remain.

The systemd user timer `dotnet-build-helper-reaper.timer` runs the script every 10 minutes.
So an orphaned helper lives at most about 25 minutes.
`tools/install-build-helper-reaper.sh` writes the timer and its service, then starts the timer.
Rerun the installer after moving the repository.
The timer was installed on this PC on 2026-11-05.

The production host has no timer.
Only release publishes build there, and the switches file covers them.

## Manual cleanup

Run `tools/reap-build-helpers.sh 0` to stop all orphaned helpers now.
`dotnet build-server shutdown` stops only helpers from the current session.
