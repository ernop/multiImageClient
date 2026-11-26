#!/usr/bin/env python3
"""Read-only summary of MultiImageClient daily JSONL telemetry logs."""

from __future__ import annotations

import argparse
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
import json
from pathlib import Path
from typing import Any

DEFAULT_DAYS = 14
FILE_PREFIX = "telemetry-"
FILE_SUFFIX = ".jsonl"
TOP_LIMIT = 20


def load_records(directory: Path, days: int) -> tuple[list[dict[str, Any]], int]:
    today = datetime.now(timezone.utc).date()
    start = today - timedelta(days=max(1, days) - 1)
    records: list[dict[str, Any]] = []
    files_read = 0
    for offset in range(max(1, days)):
        day_str = (start + timedelta(days=offset)).isoformat()
        path = directory / f"{FILE_PREFIX}{day_str}{FILE_SUFFIX}"
        if not path.is_file():
            continue
        files_read += 1
        with path.open("r", encoding="utf-8") as fh:
            for line in fh:
                text = line.strip()
                if not text:
                    continue
                records.append(json.loads(text))
    return records, files_read


def summarize(records: list[dict[str, Any]], files_read: int, days: int, directory: Path) -> None:
    by_event: Counter[str] = Counter()
    by_page: Counter[str] = Counter()
    by_actor: Counter[str] = Counter()
    by_day: Counter[str] = Counter()
    tabs_by_actor: dict[str, set[str]] = defaultdict(set)
    sub_actions: dict[str, Counter[str]] = defaultdict(Counter)
    viewer_dwells: list[int] = []
    viewer_items = 0

    for rec in records:
        ev = str(rec.get("ev", ""))
        page = str(rec.get("page", ""))
        actor = str(rec.get("actor", ""))
        day = str(rec.get("day", ""))
        tab = str(rec.get("tab", ""))
        by_event[ev] += 1
        by_page[page] += 1
        by_actor[actor] += 1
        by_day[day] += 1
        if tab:
            tabs_by_actor[actor].add(tab)
        data = rec.get("d")
        if isinstance(data, dict):
            for key in ("action", "tool", "mode", "field", "status", "kind"):
                val = data.get(key)
                if isinstance(val, str) and val:
                    sub_actions[ev][f"{key}={val}"] += 1
            if ev == "viewer.close":
                dwell = data.get("dwellMs")
                items = data.get("itemsViewed")
                if isinstance(dwell, int) and dwell >= 0:
                    viewer_dwells.append(dwell)
                if isinstance(items, int) and items >= 0:
                    viewer_items += items

    all_tabs = set().union(*tabs_by_actor.values()) if tabs_by_actor else set()
    print(f"Directory: {directory}")
    print(f"Window: last {days} day(s) | files: {files_read} | events: {len(records)} | tabs: {len(all_tabs)}")
    if not records:
        return

    print("\nBy actor:")
    for actor, count in by_actor.most_common():
        print(f"  {actor:24s} events={count:5d} tabs={len(tabs_by_actor[actor]):3d}")

    print("\nBy page:")
    for page, count in by_page.most_common():
        print(f"  {page:16s} {count:5d}")

    print("\nTop events:")
    for ev, count in by_event.most_common(TOP_LIMIT):
        print(f"  {ev:28s} {count:5d}")

    print("\nAction breakdowns:")
    for ev in sorted(sub_actions):
        pairs = ", ".join(f"{k}:{v}" for k, v in sub_actions[ev].most_common(8))
        print(f"  {ev:24s} {pairs}")

    if viewer_dwells:
        viewer_dwells.sort()
        median_s = viewer_dwells[len(viewer_dwells) // 2] / 1000.0
        print(f"\nViewer: sessions={len(viewer_dwells)}, median_dwell={median_s:.1f}s, items_viewed={viewer_items}")

    print("\nBy day:")
    for day in sorted(by_day):
        print(f"  {day}  {by_day[day]:5d}")


def main() -> None:
    parser = argparse.ArgumentParser(description="Summarize local MultiImageClient UI telemetry JSONL files.")
    parser.add_argument(
        "directory",
        nargs="?",
        default=str(Path(__file__).resolve().parent.parent / "saves" / "UiTelemetry"),
        help="Path to UiTelemetry directory (default: saves/UiTelemetry)",
    )
    parser.add_argument("--days", type=int, default=DEFAULT_DAYS, help="Number of UTC days to read (default: 14)")
    args = parser.parse_args()
    target = Path(args.directory)
    records, files_read = load_records(target, args.days)
    summarize(records, files_read, args.days, target)


if __name__ == "__main__":
    main()
