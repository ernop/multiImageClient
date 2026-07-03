#!/usr/bin/env python3
"""Extract deduplicated prompt text files from Ideogram history JSON."""

from __future__ import annotations

import argparse
import json
import sys
from pathlib import Path
from typing import Any, Iterable


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Extract user and magic prompts from Ideogram history JSON."
    )
    parser.add_argument(
        "--archive-root",
        type=Path,
        default=Path("IdeogramHistoryExtractor"),
        help="Archive folder (default: IdeogramHistoryExtractor)",
    )
    parser.add_argument(
        "--source",
        choices=("merged", "pages"),
        default="merged",
        help="Read ideogram_data_all.json (merged) or ideogram_requests/page_*.json (pages)",
    )
    return parser.parse_args()


def normalize_prompt(text: str) -> str:
    return " ".join(text.replace("\r", " ").replace("\n", " ").split()).strip()


def iter_items_from_pages(pages_dir: Path) -> Iterable[tuple[dict[str, Any], bool]]:
    if not pages_dir.is_dir():
        raise SystemExit(f"Missing pages directory: {pages_dir}")

    for page_path in sorted(pages_dir.glob("page_*.json")):
        is_private = page_path.stem.endswith("-private")
        data = json.loads(page_path.read_text(encoding="utf-8"))
        if not isinstance(data, list):
            raise SystemExit(f"Expected JSON array in {page_path}")
        for item in data:
            if isinstance(item, dict):
                yield item, is_private


def iter_items_from_merged(all_path: Path) -> Iterable[tuple[dict[str, Any], bool | None]]:
    if not all_path.is_file():
        raise SystemExit(f"Missing merged archive: {all_path}")

    data = json.loads(all_path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise SystemExit(f"Expected JSON array in {all_path}")

    for item in data:
        if not isinstance(item, dict):
            continue
        is_private = item.get("is_private")
        if is_private is None:
            is_private = item.get("private")
        yield item, is_private


def should_skip_item(item: dict[str, Any]) -> bool:
    upload_type = item.get("upload_type")
    return upload_type in {"upload", "edit"}


def collect_prompts(items: Iterable[tuple[dict[str, Any], bool | None]]) -> tuple[
    dict[str, set[str]],
    dict[str, set[str]],
]:
    user_prompts: dict[str, set[str]] = {"public": set(), "private": set(), "unknown": set()}
    gen_prompts: dict[str, set[str]] = {"public": set(), "private": set(), "unknown": set()}

    for item, is_private in items:
        if should_skip_item(item):
            continue

        bucket = "unknown"
        if is_private is True:
            bucket = "private"
        elif is_private is False:
            bucket = "public"

        user_prompt = item.get("user_prompt")
        if isinstance(user_prompt, str) and user_prompt.strip():
            user_prompts[bucket].add(normalize_prompt(user_prompt))

        responses = item.get("responses")
        if isinstance(responses, list):
            for response in responses:
                if not isinstance(response, dict):
                    continue
                prompt = response.get("prompt")
                if isinstance(prompt, str) and prompt.strip():
                    gen_prompts[bucket].add(normalize_prompt(prompt))

    return user_prompts, gen_prompts


def write_prompt_file(path: Path, prompts: set[str]) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text("\n".join(sorted(prompts)) + ("\n" if prompts else ""), encoding="utf-8")


def write_outputs(
    output_dir: Path,
    user_prompts: dict[str, set[str]],
    gen_prompts: dict[str, set[str]],
) -> None:
    mapping = [
        ("myPrompts.txt", user_prompts["public"]),
        ("myPrompts-private.txt", user_prompts["private"]),
        ("myPrompts-unknown-visibility.txt", user_prompts["unknown"]),
        ("myGenPrompts.txt", gen_prompts["public"]),
        ("myGenPrompts-private.txt", gen_prompts["private"]),
        ("myGenPrompts-unknown-visibility.txt", gen_prompts["unknown"]),
    ]
    for filename, prompts in mapping:
        if not prompts:
            continue
        out_path = output_dir / filename
        write_prompt_file(out_path, prompts)
        print(f"Wrote {len(prompts)} prompts -> {out_path}")

    combined_user = set().union(*user_prompts.values())
    combined_gen = set().union(*gen_prompts.values())
    if combined_user:
        write_prompt_file(output_dir / "myPrompts-all.txt", combined_user)
        print(f"Wrote {len(combined_user)} prompts -> {output_dir / 'myPrompts-all.txt'}")
    if combined_gen:
        write_prompt_file(output_dir / "myGenPrompts-all.txt", combined_gen)
        print(f"Wrote {len(combined_gen)} prompts -> {output_dir / 'myGenPrompts-all.txt'}")


def main() -> int:
    args = parse_args()
    archive_root = args.archive_root.resolve()
    output_dir = archive_root / "myPrompts"

    if args.source == "pages":
        items = iter_items_from_pages(archive_root / "ideogram_requests")
    else:
        items = iter_items_from_merged(archive_root / "ideogram_data_all.json")

    user_prompts, gen_prompts = collect_prompts(items)
    write_outputs(output_dir, user_prompts, gen_prompts)

    total_user = sum(len(values) for values in user_prompts.values())
    total_gen = sum(len(values) for values in gen_prompts.values())
    print(f"\nUnique user prompts: {total_user}")
    print(f"Unique magic/expanded prompts: {total_gen}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
