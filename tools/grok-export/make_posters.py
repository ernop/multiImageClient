"""Generate small poster (first-frame) thumbnails for downloaded Grok videos.

The visualizer needs lightweight thumbnails so it can show thousands of clips
without loading full mp4s. This extracts one frame near the start of each locally
downloaded video into `<archive-root>/Posters/<post_id>.jpg`.

Posters are derived archive data; keep them outside the repo.

Usage:
    python tools/grok-export/make_posters.py --archive-root "C:\\GrokArchive\\WebExport" --workers 8
"""

import argparse
import json
import subprocess
from concurrent.futures import ThreadPoolExecutor, as_completed
from pathlib import Path


def read_jsonl(path: Path):
    if not path.exists():
        return
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    continue


def make_poster(post_id: str, src: Path, dest: Path, width: int) -> tuple[str, bool, str]:
    if dest.exists() and dest.stat().st_size > 0:
        return post_id, True, "skip"
    dest.parent.mkdir(parents=True, exist_ok=True)
    cmd = [
        "ffmpeg", "-y", "-loglevel", "error",
        "-ss", "0.1", "-i", str(src),
        "-frames:v", "1",
        "-vf", f"scale={width}:-2:flags=bilinear",
        "-q:v", "5",
        str(dest),
    ]
    try:
        r = subprocess.run(cmd, capture_output=True, timeout=60)
        if r.returncode == 0 and dest.exists() and dest.stat().st_size > 0:
            return post_id, True, "ok"
        return post_id, False, (r.stderr.decode("utf-8", "replace")[:120] or "ffmpeg failed")
    except Exception as ex:
        return post_id, False, f"{type(ex).__name__}: {ex}"


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract first-frame poster thumbnails for downloaded videos")
    parser.add_argument("--archive-root", required=True, type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--width", type=int, default=400)
    parser.add_argument("--workers", type=int, default=8)
    args = parser.parse_args()

    archive_root = args.archive_root.resolve()
    posters_dir = args.out or archive_root / "Posters"

    jobs = []
    for row in read_jsonl(archive_root / "video_downloads.jsonl"):
        pid, file_name = row.get("post_id"), row.get("file")
        if not pid or not file_name:
            continue
        src = Path(file_name)
        if not src.exists():
            continue
        jobs.append((pid, src, posters_dir / f"{pid}.jpg"))

    # de-dupe by post id
    seen = {}
    for pid, src, dest in jobs:
        seen.setdefault(pid, (pid, src, dest))
    jobs = list(seen.values())

    print(f"posters: {len(jobs)} videos -> {posters_dir}", flush=True)
    ok = skip = fail = 0
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as ex:
        futures = [ex.submit(make_poster, pid, src, dest, args.width) for pid, src, dest in jobs]
        for i, fut in enumerate(as_completed(futures), start=1):
            _, success, note = fut.result()
            if note == "skip":
                skip += 1
            elif success:
                ok += 1
            else:
                fail += 1
            if i % 500 == 0:
                print(f"  {i}/{len(jobs)} ok={ok} skip={skip} fail={fail}", flush=True)
    print(f"done ok={ok} skip={skip} fail={fail}", flush=True)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
