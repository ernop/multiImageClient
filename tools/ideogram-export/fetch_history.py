#!/usr/bin/env python3
"""Fetch paginated Ideogram web UI generation history into a local archive."""

from __future__ import annotations

import argparse
import hashlib
import json
import sys
import time
from datetime import datetime, timezone
from pathlib import Path
from typing import Any

import cloudscraper

IDEOGRAM_API_URL = "https://ideogram.ai/api/g/u"
DEFAULT_FILTERS = ["generations", "upload", "edit", "upscales"]
HELPFUL_COOKIE_NAMES = [
    "__cf_bm",
    "cf_clearance",
    "session_cookie",
]


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Download Ideogram web UI generation history (JSON pages + merged archive)."
    )
    parser.add_argument(
        "--archive-root",
        type=Path,
        default=Path("IdeogramHistoryExtractor"),
        help="Archive folder (default: IdeogramHistoryExtractor)",
    )
    parser.add_argument(
        "--config",
        type=Path,
        default=None,
        help="Auth config JSON (default: <archive-root>/config.json)",
    )
    parser.add_argument(
        "--page-delay",
        type=float,
        default=19.0,
        help="Seconds to sleep between page requests (default: 19)",
    )
    parser.add_argument(
        "--public-only",
        action="store_true",
        help="Fetch only public history",
    )
    parser.add_argument(
        "--private-only",
        action="store_true",
        help="Fetch only private history",
    )
    parser.add_argument(
        "--max-pages",
        type=int,
        default=0,
        help="Stop after N pages per visibility pass (0 = no limit)",
    )
    parser.add_argument(
        "--start-page",
        type=int,
        default=None,
        help="First page number to fetch in each pass (default: 0, or auto with --resume)",
    )
    parser.add_argument(
        "--resume",
        action="store_true",
        help="Continue each pass after the highest existing page_N*.json snapshot",
    )
    parser.add_argument(
        "--merge-only",
        action="store_true",
        help="Rebuild ideogram_data_all.json from page snapshots and exit",
    )
    parser.add_argument(
        "--merge-every",
        type=int,
        default=10,
        help="Rewrite ideogram_data_all.json every N fetched pages (default: 10, 0=only at end)",
    )
    return parser.parse_args()


def load_config(config_path: Path) -> dict[str, str]:
    if not config_path.is_file():
        template = config_path.with_name("config.template.json")
        hint = f"Copy {template} to {config_path} and fill in username, authorization, cookie."
        raise SystemExit(f"Missing config: {config_path}\n{hint}")

    data = json.loads(config_path.read_text(encoding="utf-8"))
    missing = [key for key in ("username", "authorization", "cookie") if not str(data.get(key, "")).strip()]
    if missing:
        raise SystemExit(f"Config {config_path} is missing or empty: {', '.join(missing)}")

    for placeholder in ("YOUR_IDEOGRAM_USERNAME", "REPLACE_WITH", "REPLACE_THIS", "PASTE_"):
        blob = json.dumps(data)
        if placeholder in blob:
            raise SystemExit(f"Config {config_path} still contains placeholder text ({placeholder}).")

    return {
        "username": str(data["username"]).strip(),
        "authorization": str(data["authorization"]).strip(),
        "cookie": str(data["cookie"]).strip(),
    }


def parse_cookies(cookie_string: str) -> dict[str, str]:
    cookies: dict[str, str] = {}
    for part in cookie_string.split(";"):
        part = part.strip()
        if not part or "=" not in part:
            continue
        name, value = part.split("=", 1)
        cookies[name.strip()] = value.strip()
    return cookies


def warn_missing_cookies(cookies: dict[str, str]) -> None:
    missing = [name for name in HELPFUL_COOKIE_NAMES if name not in cookies]
    if missing:
        print(f"Warning: cookie header may be incomplete; missing: {', '.join(missing)}")


def build_headers(username: str, authorization: str) -> dict[str, str]:
    return {
        "User-Agent": (
            "Mozilla/5.0 (X11; Linux x86_64; rv:130.0) Gecko/20100101 Firefox/130.0"
        ),
        "Accept": "*/*",
        "Accept-Language": "en-US,en;q=0.5",
        "Accept-Encoding": "gzip, deflate, br",
        "Referer": f"https://ideogram.ai/u/{username}/generated",
        "Content-Type": "application/json",
        "Authorization": authorization,
        "DNT": "1",
        "Sec-GPC": "1",
        "Connection": "keep-alive",
        "Sec-Fetch-Dest": "empty",
        "Sec-Fetch-Mode": "cors",
        "Sec-Fetch-Site": "same-origin",
        "Priority": "u=4",
        "TE": "trailers",
    }


def fetch_page(
    scraper: cloudscraper.CloudScraper,
    *,
    username: str,
    headers: dict[str, str],
    cookies: dict[str, str],
    page: int,
    is_private: bool,
) -> list[dict[str, Any]] | None:
    params: dict[str, Any] = {
        "user_id": username,
        "filters": DEFAULT_FILTERS,
    }
    if page > 0:
        params["page"] = page
    if is_private:
        params["private"] = "true"

    response = scraper.get(
        IDEOGRAM_API_URL,
        headers=headers,
        params=params,
        cookies=cookies,
        timeout=120,
    )
    print(f"GET {response.url} -> {response.status_code}")

    if response.status_code != 200:
        print(response.text[:2000])
        return None

    data = response.json()
    if isinstance(data, list):
        return data
    if isinstance(data, dict):
        return list(data.get("items", []))
    raise SystemExit(f"Unexpected response shape: {type(data).__name__}")


def item_key(item: dict[str, Any]) -> str:
    for field in ("request_id", "id", "generation_id", "post_id", "uuid"):
        value = item.get(field)
        if value:
            return f"{field}:{value}"
    digest = hashlib.sha256(
        json.dumps(item, sort_keys=True, ensure_ascii=False).encode("utf-8")
    ).hexdigest()[:24]
    return f"hash:{digest}"


def load_existing_items(all_path: Path) -> dict[str, dict[str, Any]]:
    if not all_path.is_file():
        return {}
    data = json.loads(all_path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise SystemExit(f"Expected a JSON array in {all_path}")
    merged: dict[str, dict[str, Any]] = {}
    for item in data:
        if isinstance(item, dict):
            merged[item_key(item)] = item
    return merged


def load_items_from_page_snapshots(pages_dir: Path) -> dict[str, dict[str, Any]]:
    merged: dict[str, dict[str, Any]] = {}
    if not pages_dir.is_dir():
        return merged

    for page_path in sorted(pages_dir.glob("page_*.json")):
        data = json.loads(page_path.read_text(encoding="utf-8"))
        if not isinstance(data, list):
            raise SystemExit(f"Expected a JSON array in {page_path}")
        for item in data:
            if isinstance(item, dict):
                merged[item_key(item)] = item
    return merged


def discover_resume_page(pages_dir: Path, is_private: bool) -> int:
    suffix = "-private" if is_private else ""
    highest = -1
    for page_path in pages_dir.glob(f"page_*{suffix}.json"):
        stem = page_path.stem
        page_part = stem.removesuffix("-private") if is_private else stem
        if not page_part.startswith("page_"):
            continue
        try:
            page_num = int(page_part.removeprefix("page_"))
        except ValueError:
            continue
        highest = max(highest, page_num)
    return highest + 1 if highest >= 0 else 0


def write_merged(all_path: Path, merged: dict[str, dict[str, Any]]) -> None:
    all_items = list(merged.values())
    all_path.write_text(json.dumps(all_items, indent=2, ensure_ascii=False), encoding="utf-8")
    print(f"Merged unique items: {len(all_items)} -> {all_path}")


def resolve_start_page(args: argparse.Namespace, pages_dir: Path, is_private: bool) -> int:
    if args.start_page is not None:
        return args.start_page
    if args.resume:
        start = discover_resume_page(pages_dir, is_private)
        label = "private" if is_private else "public"
        print(f"Resume {label}: starting at page {start}")
        return start
    return 0


def append_ledger(ledger_path: Path, record: dict[str, Any]) -> None:
    ledger_path.parent.mkdir(parents=True, exist_ok=True)
    with ledger_path.open("a", encoding="utf-8") as handle:
        handle.write(json.dumps(record, ensure_ascii=False) + "\n")


def visibility_passes(args: argparse.Namespace) -> list[bool]:
    if args.public_only and args.private_only:
        raise SystemExit("Use at most one of --public-only and --private-only.")
    if args.public_only:
        return [False]
    if args.private_only:
        return [True]
    return [False, True]


def main() -> int:
    args = parse_args()
    archive_root = args.archive_root.resolve()
    config_path = (args.config or archive_root / "config.json").resolve()
    pages_dir = archive_root / "ideogram_requests"
    all_path = archive_root / "ideogram_data_all.json"
    ledger_path = archive_root / "fetch_log.jsonl"

    config = load_config(config_path)
    cookies = parse_cookies(config["cookie"])
    warn_missing_cookies(cookies)
    headers = build_headers(config["username"], config["authorization"])
    scraper = cloudscraper.create_scraper()

    pages_dir.mkdir(parents=True, exist_ok=True)
    merged = load_items_from_page_snapshots(pages_dir)
    if not merged:
        merged = load_existing_items(all_path)
    elif all_path.is_file():
        merged.update(load_existing_items(all_path))

    if args.merge_only:
        write_merged(all_path, merged)
        print(f"Page snapshots: {pages_dir}")
        return 0

    run_started = datetime.now(timezone.utc).isoformat()
    fetched_this_run = 0
    pages_since_merge = 0

    print(
        "Fetching Ideogram history. Ideogram re-paginates over time; "
        "this run merges by stable item id into ideogram_data_all.json."
    )
    print(f"Loaded {len(merged)} unique items from existing snapshots.")

    for is_private in visibility_passes(args):
        start_page = resolve_start_page(args, pages_dir, is_private)
        page = start_page
        pages_fetched_this_pass = 0
        while True:
            if args.max_pages and pages_fetched_this_pass >= args.max_pages:
                print(f"Stopping at --max-pages={args.max_pages} for private={is_private}.")
                break

            label = "private" if is_private else "public"
            print(f"\nPage {page} ({label})...")
            items = fetch_page(
                scraper,
                username=config["username"],
                headers=headers,
                cookies=cookies,
                page=page,
                is_private=is_private,
            )
            if items is None:
                print(f"Error on page {page} ({label}); stopping this pass.")
                break

            suffix = "-private" if is_private else ""
            page_path = pages_dir / f"page_{page}{suffix}.json"
            page_path.write_text(json.dumps(items, indent=2, ensure_ascii=False), encoding="utf-8")
            print(f"Saved {len(items)} items -> {page_path}")

            new_on_page = 0
            for item in items:
                key = item_key(item)
                if key not in merged:
                    new_on_page += 1
                merged[key] = item
            fetched_this_run += len(items)

            append_ledger(
                ledger_path,
                {
                    "run_started": run_started,
                    "timestamp": datetime.now(timezone.utc).isoformat(),
                    "page": page,
                    "private": is_private,
                    "items_on_page": len(items),
                    "new_items_on_page": new_on_page,
                    "page_file": str(page_path.relative_to(archive_root)),
                },
            )

            if len(items) == 0:
                print("Empty page; end of this pass.")
                break

            page += 1
            pages_fetched_this_pass += 1
            pages_since_merge += 1
            if args.merge_every and pages_since_merge >= args.merge_every:
                write_merged(all_path, merged)
                pages_since_merge = 0

            if args.page_delay > 0:
                time.sleep(args.page_delay)

    print(f"\nRun fetched {fetched_this_run} page rows.")
    write_merged(all_path, merged)
    print(f"Page snapshots: {pages_dir}")
    print(f"Fetch log: {ledger_path}")
    print("Next: python tools/ideogram-export/extract_prompts.py --archive-root", archive_root)
    return 0


if __name__ == "__main__":
    sys.exit(main())
