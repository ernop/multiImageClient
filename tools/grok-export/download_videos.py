import argparse
import csv
import hashlib
import html
import json
import re
import shutil
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import datetime, timezone
from pathlib import Path


VIDEO_URL_RE = re.compile(
    r"https:(?:\\?/\\?/|//)imagine-public\.x\.ai(?:\\?/|/)imagine-public(?:\\?/|/)share-videos(?:\\?/|/)"
    r"([0-9a-fA-F-]{36})\.mp4"
)
POST_ID_RE = re.compile(r"/post/([0-9a-fA-F-]{36})")

USER_AGENT = (
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
    "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/136.0.0.0 Safari/537.36"
)


@dataclass(frozen=True)
class VideoRow:
    index: int
    create_time: str
    prompt: str
    link: str
    post_id: str


def request(url: str, *, referer: str, range_header: str | None = None):
    headers = {
        "User-Agent": USER_AGENT,
        "Accept": "*/*",
        "Referer": referer,
    }
    if range_header:
        headers["Range"] = range_header
    return urllib.request.Request(url, headers=headers)


def read_existing_jsonl(path: Path) -> dict[str, dict]:
    if not path.exists():
        return {}
    result: dict[str, dict] = {}
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            post_id = obj.get("post_id")
            file_path = obj.get("file")
            if post_id and file_path and Path(file_path).exists() and Path(file_path).stat().st_size > 0:
                result[post_id] = obj
    return result


def append_jsonl(path: Path, obj: dict):
    path.parent.mkdir(parents=True, exist_ok=True)
    with path.open("a", encoding="utf-8") as f:
        f.write(json.dumps(obj, ensure_ascii=False, separators=(",", ":")) + "\n")


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as f:
        for chunk in iter(lambda: f.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def preserve_source_csv(videos_csv: Path, snapshot_path: Path) -> dict:
    snapshot_path.parent.mkdir(parents=True, exist_ok=True)
    source_hash = sha256_file(videos_csv)
    if not snapshot_path.exists():
        shutil.copy2(videos_csv, snapshot_path)
    snapshot_hash = sha256_file(snapshot_path)

    hash_path = snapshot_path.with_suffix(snapshot_path.suffix + ".sha256")
    if not hash_path.exists():
        hash_path.write_text(f"{snapshot_hash}  {snapshot_path.name}\n", encoding="utf-8")

    return {
        "source_csv": str(videos_csv),
        "source_sha256": source_hash,
        "snapshot_csv": str(snapshot_path),
        "snapshot_sha256": snapshot_hash,
        "snapshot_matches_source": source_hash == snapshot_hash,
    }


def event_record(event: str, **kwargs) -> dict:
    return {
        "event": event,
        "timestamp_utc": datetime.now(timezone.utc).isoformat(),
        **kwargs,
    }


def load_rows(videos_csv: Path, limit: int | None = None) -> list[VideoRow]:
    rows: list[VideoRow] = []
    with videos_csv.open("r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for i, row in enumerate(reader, start=1):
            link = row.get("link", "")
            match = POST_ID_RE.search(link)
            if not match:
                continue
            rows.append(
                VideoRow(
                    index=i,
                    create_time=row.get("create_time", ""),
                    prompt=row.get("prompt", ""),
                    link=link,
                    post_id=match.group(1).lower(),
                )
            )
            if limit and len(rows) >= limit:
                break
    return rows


def parse_timestamp(value: str) -> str:
    try:
        dt = datetime.fromisoformat(value.replace("Z", "+00:00"))
        return dt.strftime("%Y%m%d_%H%M%S")
    except ValueError:
        return "unknown_time"


def direct_url(media_id: str) -> str:
    return f"https://imagine-public.x.ai/imagine-public/share-videos/{media_id}.mp4"


def probe_video_url(url: str, referer: str) -> tuple[bool, str]:
    try:
        req = request(url, referer=referer, range_header="bytes=0-0")
        with urllib.request.urlopen(req, timeout=30) as res:
            status = getattr(res, "status", 200)
            ctype = res.headers.get("Content-Type", "")
            res.read(1)
        if status in (200, 206) and "video" in ctype.lower():
            return True, f"{status} {ctype}"
        return False, f"{status} {ctype}"
    except urllib.error.HTTPError as ex:
        return False, f"{ex.code} {ex.reason}"
    except Exception as ex:
        return False, type(ex).__name__ + ": " + str(ex)


def fetch_post_page(post_url: str) -> str:
    req = request(post_url, referer="https://grok.com/imagine")
    with urllib.request.urlopen(req, timeout=45) as res:
        raw = res.read()
        charset = res.headers.get_content_charset() or "utf-8"
    return raw.decode(charset, errors="replace")


def extract_video_urls(page_text: str) -> list[str]:
    page_text = html.unescape(page_text).replace("\\/", "/")
    urls: list[str] = []
    seen: set[str] = set()
    for match in VIDEO_URL_RE.finditer(page_text):
        media_id = match.group(1).lower()
        url = direct_url(media_id)
        if url not in seen:
            seen.add(url)
            urls.append(url)
    return urls


def resolve_video_urls(row: VideoRow) -> tuple[list[str], list[str]]:
    notes: list[str] = []
    candidates: list[str] = []

    guessed = direct_url(row.post_id)
    ok, note = probe_video_url(guessed, row.link)
    notes.append(f"guess:{note}")
    if ok:
        return [guessed], notes

    try:
        page = fetch_post_page(row.link)
        extracted = extract_video_urls(page)
        notes.append(f"page_urls:{len(extracted)}")
        if guessed in extracted:
            candidates.append(guessed)
        else:
            candidates.extend(extracted)
    except Exception as ex:
        notes.append("page_error:" + type(ex).__name__ + ": " + str(ex))

    valid: list[str] = []
    seen: set[str] = set()
    for url in candidates:
        if url in seen:
            continue
        seen.add(url)
        ok, note = probe_video_url(url, row.link)
        notes.append(f"{Path(urllib.parse.urlparse(url).path).stem}:{note}")
        if ok:
            valid.append(url)
    return valid, notes


def download_url(url: str, dest: Path, referer: str) -> int:
    tmp = dest.with_suffix(dest.suffix + ".part")
    if tmp.exists():
        tmp.unlink()
    req = request(url, referer=referer)
    with urllib.request.urlopen(req, timeout=120) as res:
        tmp.parent.mkdir(parents=True, exist_ok=True)
        with tmp.open("wb") as f:
            while True:
                chunk = res.read(1024 * 1024)
                if not chunk:
                    break
                f.write(chunk)
    tmp.replace(dest)
    return dest.stat().st_size


def output_path(videos_dir: Path, row: VideoRow, url: str, ordinal: int) -> Path:
    media_id = Path(urllib.parse.urlparse(url).path).stem.lower()
    stamp = parse_timestamp(row.create_time)
    suffix = "" if ordinal == 0 else f"_{ordinal + 1:02d}"
    return videos_dir / f"{stamp}_{row.post_id[:8]}_{media_id[:8]}{suffix}.mp4"


def source_row(row: VideoRow) -> dict:
    return {
        "row_index": row.index,
        "create_time": row.create_time,
        "prompt": row.prompt,
        "link": row.link,
        "post_id": row.post_id,
    }


def split_unique_rows(rows: list[VideoRow]) -> tuple[list[VideoRow], list[tuple[VideoRow, VideoRow]]]:
    unique: list[VideoRow] = []
    duplicates: list[tuple[VideoRow, VideoRow]] = []
    first_by_post_id: dict[str, VideoRow] = {}
    for row in rows:
        first = first_by_post_id.get(row.post_id)
        if first is None:
            first_by_post_id[row.post_id] = row
            unique.append(row)
        else:
            duplicates.append((row, first))
    return unique, duplicates


def process_row(row: VideoRow, videos_dir: Path, existing_done: dict[str, dict], dry_run: bool = False) -> dict:
    if row.post_id in existing_done and Path(existing_done[row.post_id].get("file", "")).exists():
        return {
            "status": "skipped",
            **source_row(row),
            "file": existing_done[row.post_id].get("file"),
            "existing_record": existing_done[row.post_id],
        }

    urls, notes = resolve_video_urls(row)
    if not urls:
        return {
            "status": "failed",
            **source_row(row),
            "notes": notes,
        }

    downloaded = []
    for ordinal, url in enumerate(urls):
        dest = output_path(videos_dir, row, url, ordinal)
        if dest.exists() and dest.stat().st_size > 0:
            size = dest.stat().st_size
        elif dry_run:
            size = 0
        else:
            size = download_url(url, dest, row.link)
        downloaded.append({"url": url, "file": str(dest), "bytes": size})

    return {
        "status": "downloaded",
        **source_row(row),
        "media": downloaded,
        "file": downloaded[0]["file"],
        "bytes": sum(x["bytes"] for x in downloaded),
        "notes": notes,
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Download Grok Imagine videos from an archive videos.csv")
    parser.add_argument("--archive-root", required=True, type=Path)
    parser.add_argument("--csv", type=Path)
    parser.add_argument("--out", type=Path)
    parser.add_argument("--manifest", type=Path)
    parser.add_argument("--failures", type=Path)
    parser.add_argument("--events", type=Path)
    parser.add_argument("--source-snapshot", type=Path)
    parser.add_argument("--limit", type=int)
    parser.add_argument("--workers", type=int, default=3)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    archive_root = args.archive_root.resolve()
    videos_csv = args.csv or archive_root / "videos.csv"
    videos_dir = args.out or archive_root / "Videos"
    manifest = args.manifest or archive_root / "video_downloads.jsonl"
    failures = args.failures or archive_root / "video_failures.jsonl"
    events = args.events or archive_root / "video_download_events.jsonl"
    source_snapshot = args.source_snapshot or archive_root / "videos_source_snapshot.csv"

    source_info = preserve_source_csv(videos_csv, source_snapshot)
    rows = load_rows(videos_csv, args.limit)
    unique_rows, duplicate_rows = split_unique_rows(rows)
    done = read_existing_jsonl(manifest)

    append_jsonl(events, event_record(
        "run_start",
        source_rows=len(rows),
        unique_rows_to_process=len(unique_rows),
        duplicate_source_rows=len(duplicate_rows),
        out=str(videos_dir),
        manifest=str(manifest),
        failures=str(failures),
        dry_run=args.dry_run,
        workers=args.workers,
        source=source_info,
    ))

    for duplicate, first in duplicate_rows:
        append_jsonl(events, event_record(
            "row_duplicate_source",
            result={
                "status": "duplicate_source",
                **source_row(duplicate),
                "canonical_row_index": first.index,
                "canonical_link": first.link,
            },
        ))

    print(
        f"source_rows={len(rows)} unique_rows_to_process={len(unique_rows)} "
        f"duplicate_source_rows={len(duplicate_rows)} out={videos_dir}",
        flush=True,
    )

    counts = {"downloaded": 0, "failed": 0, "skipped": 0}
    started = time.time()
    with ThreadPoolExecutor(max_workers=max(1, args.workers)) as executor:
        futures = {executor.submit(process_row, row, videos_dir, done, args.dry_run): row for row in unique_rows}
        for n, future in enumerate(as_completed(futures), start=1):
            row = futures[future]
            try:
                result = future.result()
            except Exception as ex:
                result = {
                    "status": "failed",
                    **source_row(row),
                    "notes": [type(ex).__name__ + ": " + str(ex)],
                }

            status = result.get("status", "failed")
            counts[status] = counts.get(status, 0) + 1
            append_jsonl(events, event_record("row_" + status, result=result))
            if args.dry_run:
                pass
            elif status == "failed":
                append_jsonl(failures, result)
            elif status == "downloaded":
                append_jsonl(manifest, result)

            if n % 10 == 0 or status != "downloaded":
                elapsed = max(1, time.time() - started)
                rate = n / elapsed
                print(
                    f"progress={n}/{len(unique_rows)} downloaded={counts.get('downloaded', 0)} "
                    f"failed={counts.get('failed', 0)} skipped={counts.get('skipped', 0)} rate={rate:.2f}/s",
                    flush=True,
                )

    append_jsonl(events, event_record("run_complete", counts=counts))
    print("complete " + " ".join(f"{k}={v}" for k, v in counts.items()), flush=True)
    return 0 if counts.get("downloaded", 0) or not counts.get("failed", 0) else 1


if __name__ == "__main__":
    sys.exit(main())
