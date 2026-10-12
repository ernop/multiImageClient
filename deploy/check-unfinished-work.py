#!/usr/bin/env python3
"""Refuse a production restart while an instance has unfinished work.

A restart kills in-flight jobs and goal loops. Each argument names one
instance's systemd unit. The check reads that instance's history on disk and
sends no request, so a sleeping socket-activated service stays asleep. Output
names units and counts only, never private paths.

A job is unfinished when its events.jsonl changed after the current process
started and holds no job-done event. A job from an earlier process cannot still
run; the server closes it as interrupted when it loads it. A goal loop is
unfinished while its loop.json says running, because every process start marks
loops left running by an earlier process as stopped.

Exit status: 0 when every unit is idle; 75 (EX_TEMPFAIL) while any unit is
busy after --wait-minutes, so callers can tell it from sudo's own failures;
2 when a unit's state cannot be read.
"""
import argparse
import glob
import json
import os
import shlex
import subprocess
import sys
import time


def show(unit, prop):
    return subprocess.run(["systemctl", "show", unit, "-p", prop, "--value"],
                          check=True, capture_output=True, text=True).stdout.strip()


def settings_path(environment):
    for assignment in shlex.split(environment):
        name, _, value = assignment.partition("=")
        if name == "MULTIIMAGECLIENT_SETTINGS" and value:
            return value
    raise RuntimeError("the unit sets no MULTIIMAGECLIENT_SETTINGS")


def process_started_at(monotonic_microseconds, margin_seconds=5):
    # systemd stamps the start on CLOCK_MONOTONIC, the clock behind
    # time.monotonic(). The margin absorbs wall-clock steps and only widens
    # the set of jobs that count as current.
    started = int(monotonic_microseconds)
    if started <= 0:
        raise RuntimeError("the active unit reports no process start time")
    return time.time() - (time.monotonic() - started / 1_000_000) - margin_seconds


def has_job_done(events):
    with open(events, encoding="utf-8", errors="replace") as lines:
        for line in lines:
            try:
                record = json.loads(line)
            except ValueError:
                # A line still being appended, or one cut off by a crash, is
                # not a job-done record. Missing job-done keeps the job unfinished.
                continue
            if isinstance(record, dict) and record.get("type") == "job-done":
                return True
    return False


def loop_running(path):
    with open(path, encoding="utf-8") as f:
        try:
            loop = json.load(f)
        except ValueError:
            # loop.json is replaced atomically, so this file is corrupt.
            # Refuse rather than assume the loop has stopped.
            return True
    return not isinstance(loop, dict) or loop.get("status") == "running"


def unfinished_work(base, started_at):
    jobs = 0
    for events in glob.glob(os.path.join(base, "UiHistory", "*", "events.jsonl")):
        try:
            if os.path.getmtime(events) >= started_at and not has_job_done(events):
                jobs += 1
        except FileNotFoundError:
            continue  # deleted while scanning; running jobs cannot be deleted
    loops = 0
    for path in glob.glob(os.path.join(base, "UiGoalLoops", "*", "loop.json")):
        try:
            if loop_running(path):
                loops += 1
        except FileNotFoundError:
            continue
    return jobs, loops


def unit_status(unit):
    """Return (idle, description) for one unit."""
    if show(unit, "LoadState") != "loaded":
        raise RuntimeError(f"{unit} is not an installed unit")
    state = show(unit, "ActiveState")
    if state in ("inactive", "failed"):
        # No process exists, so nothing can be running. A sleeping
        # socket-activated original is in this state.
        return True, f"{unit}: idle (no running process)"
    if state != "active":
        return False, f"{unit}: service is {state}"
    with open(settings_path(show(unit, "Environment")), encoding="utf-8") as f:
        base = json.load(f)["ImageDownloadBaseFolder"]
    started_at = process_started_at(show(unit, "ExecMainStartTimestampMonotonic"))
    jobs, loops = unfinished_work(base, started_at)
    description = f"{unit}: {jobs} unfinished job(s), {loops} running goal loop(s)"
    return jobs == 0 and loops == 0, description


def main():
    parser = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    parser.add_argument("units", nargs="+", metavar="UNIT")
    parser.add_argument("--wait-minutes", type=float, default=0,
                        help="poll every 30 seconds until idle, for at most this long")
    args = parser.parse_args()
    deadline = time.monotonic() + max(args.wait_minutes, 0) * 60
    reported = None
    while True:
        try:
            statuses = [unit_status(unit) for unit in args.units]
        except (OSError, RuntimeError, KeyError, ValueError, subprocess.CalledProcessError) as error:
            print(f"ERROR: cannot read instance state: {error}", file=sys.stderr)
            return 2
        lines = [description for _, description in statuses]
        if lines != reported:
            print("\n".join(lines), flush=True)
            reported = lines
        if all(idle for idle, _ in statuses):
            return 0
        if time.monotonic() >= deadline:
            print("BUSY: unfinished jobs or goal loops; nothing changed. "
                  "Rerun after they finish, or wait longer with --wait-minutes N.", file=sys.stderr)
            return 75
        time.sleep(30)


if __name__ == "__main__":
    sys.exit(main())
