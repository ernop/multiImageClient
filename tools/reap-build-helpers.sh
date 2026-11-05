#!/usr/bin/env bash
set -Eeuo pipefail

# Stops .NET build helpers that no build owns: MSBuild worker nodes and the
# shared C# compiler server whose parent process died. After a crashed or
# killed build these wait forever. See docs/build-helper-processes.md.
#
# Usage: tools/reap-build-helpers.sh [MIN_AGE_SECONDS]   (default 900)

min_age=${1:-900}
[[ $min_age =~ ^[0-9]+$ ]] || { printf 'usage: %s [MIN_AGE_SECONDS]\n' "$0" >&2; exit 2; }
pattern='MSBuild\.dll .*/nodemode:|VBCSCompiler'

# A helper is orphaned when the user's systemd (or PID 1) adopted it.
orphaned_helpers() {
    local pid ppid age
    for pid in $(pgrep -u "$(id -u)" -f "$pattern"); do
        read -r ppid age < <(ps -o ppid=,etimes= -p "$pid") || continue
        [[ $(ps -o comm= -p "$ppid") == systemd ]] || continue
        if (( age >= min_age )); then printf '%s\n' "$pid"; fi
    done
}

mapfile -t pids < <(orphaned_helpers)
(( ${#pids[@]} )) || exit 0
kill "${pids[@]}" 2>/dev/null || true
sleep 10
# Checked again so a reused process ID is never force-killed.
mapfile -t left < <(orphaned_helpers)
if (( ${#left[@]} )); then kill -KILL "${left[@]}" 2>/dev/null || true; fi
printf 'Stopped %d orphaned .NET build helper(s).\n' "${#pids[@]}"
