import argparse
import csv
import json
import math
import re
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path


UUID_RE = re.compile(r"/post/([0-9a-fA-F-]{36})")
TOKEN_RE = re.compile(r"[a-z0-9']+")


def parse_time(value: str) -> datetime:
    if not value:
        return datetime.min.replace(tzinfo=timezone.utc)
    return datetime.fromisoformat(value.replace("Z", "+00:00"))


def unix_ms(value: str) -> int:
    return int(parse_time(value).timestamp() * 1000)


def read_jsonl(path: Path) -> list[dict]:
    if not path.exists():
        return []
    rows = []
    with path.open("r", encoding="utf-8") as f:
        for line in f:
            if not line.strip():
                continue
            try:
                rows.append(json.loads(line))
            except json.JSONDecodeError:
                continue
    return rows


def prompt_key(prompt: str) -> str:
    return " ".join(TOKEN_RE.findall((prompt or "").lower()))


def tokens(prompt: str) -> set[str]:
    return {t for t in TOKEN_RE.findall((prompt or "").lower()) if len(t) > 2}


def relpath(path: str | Path, archive_root: Path) -> str:
    p = Path(path)
    try:
        return p.relative_to(archive_root).as_posix()
    except ValueError:
        return p.as_posix()


def post_id_from_link(link: str) -> str:
    match = UUID_RE.search(link or "")
    return match.group(1).lower() if match else ""


def load_manifest_images(archive_root: Path) -> list[dict]:
    images = []
    for i, row in enumerate(read_jsonl(archive_root / "manifest.jsonl"), start=1):
        file_name = row.get("file") or ""
        file_path = archive_root / file_name
        images.append(
            {
                "id": row.get("id") or f"image-{i}",
                "type": "image",
                "source": row.get("source") or "",
                "createTime": row.get("createTime") or "",
                "t": unix_ms(row.get("createTime") or ""),
                "prompt": row.get("prompt") or "",
                "file": relpath(file_name, archive_root),
                "bytes": row.get("bytes") or (file_path.stat().st_size if file_path.exists() else 0),
            }
        )
    return sorted(images, key=lambda x: (x["t"], x["id"]))


def load_video_rows(archive_root: Path) -> list[dict]:
    rows = []
    with (archive_root / "videos.csv").open("r", encoding="utf-8-sig", newline="") as f:
        reader = csv.DictReader(f)
        for i, row in enumerate(reader, start=1):
            post_id = post_id_from_link(row.get("link", ""))
            rows.append(
                {
                    "id": post_id or f"video-{i}",
                    "type": "video",
                    "rowIndex": i,
                    "createTime": row.get("create_time") or "",
                    "t": unix_ms(row.get("create_time") or ""),
                    "prompt": row.get("prompt") or "",
                    "link": row.get("link") or "",
                }
            )
    return rows


def load_downloads(archive_root: Path) -> dict[str, dict]:
    downloads = {}
    for row in read_jsonl(archive_root / "video_downloads.jsonl"):
        post_id = row.get("post_id")
        file_name = row.get("file")
        if not post_id or not file_name:
            continue
        file_path = Path(file_name)
        if not file_path.exists() or file_path.stat().st_size <= 0:
            continue
        media = (row.get("media") or [{}])[0]
        downloads[post_id] = {
            "status": "downloaded",
            "file": relpath(file_path, archive_root),
            "bytes": file_path.stat().st_size,
            "mediaUrl": media.get("url") or "",
            "notes": row.get("notes") or [],
        }
    return downloads


def load_failures(archive_root: Path) -> dict[str, dict]:
    failures = {}
    for row in read_jsonl(archive_root / "video_failures.jsonl"):
        post_id = row.get("post_id")
        if not post_id:
            continue
        failures[post_id] = {
            "status": "failed",
            "error": row.get("error") or row.get("reason") or "",
            "notes": row.get("notes") or [],
        }
    return failures


def enrich_videos(archive_root: Path, videos: list[dict]) -> list[dict]:
    downloads = load_downloads(archive_root)
    failures = load_failures(archive_root)
    for video in videos:
        if video["id"] in downloads:
            video.update(downloads[video["id"]])
        elif video["id"] in failures:
            video.update(failures[video["id"]])
        else:
            video["status"] = "missing"
        media_url = video.get("mediaUrl") or ""
        media_id = Path(media_url).stem if media_url else ""
        video["mediaId"] = media_id
        video["suspectMedia"] = bool(media_id and media_id != video["id"])
    return videos


def assign_prompt_groups(items: list[dict]) -> dict[str, list[str]]:
    groups = defaultdict(list)
    item_by_id = {item["id"]: item for item in items}
    for item in sorted(items, key=lambda x: (x["t"], x["id"])):
        key = prompt_key(item.get("prompt") or "")
        if key:
            groups[key].append(item["id"])

    named_groups = {}
    group_num = 1
    for key, ids in sorted(groups.items(), key=lambda kv: (-len(kv[1]), kv[0])):
        if len(ids) < 2:
            continue
        group_id = f"p{group_num}"
        group_num += 1
        named_groups[group_id] = ids
        for seq, item_id in enumerate(ids, start=1):
            item = item_by_id.get(item_id)
            if item:
                item["promptGroup"] = group_id
                item["promptSeq"] = seq
                item["promptGroupCount"] = len(ids)
    return named_groups


def rank_source_candidates(images: list[dict], videos: list[dict]) -> None:
    image_tokens = {img["id"]: tokens(img.get("prompt") or "") for img in images}
    sorted_images = sorted(images, key=lambda x: x["t"])

    for video in videos:
        vt = video["t"]
        vp = tokens(video.get("prompt") or "")
        scored = []
        for img in sorted_images:
            if img["t"] > vt:
                break
            age_hours = max((vt - img["t"]) / 3_600_000, 0)
            if age_hours > 24 * 45:
                continue
            it = image_tokens[img["id"]]
            overlap = len(vp & it)
            union = len(vp | it) or 1
            jaccard = overlap / union
            time_score = 1 / (1 + math.log1p(age_hours))
            same_prompt = 1 if prompt_key(video.get("prompt") or "") == prompt_key(img.get("prompt") or "") else 0
            score = (jaccard * 70) + (time_score * 25) + (same_prompt * 40)
            if overlap or age_hours <= 8 or same_prompt:
                scored.append((score, img["id"]))
        scored.sort(reverse=True)
        video["sourceCandidates"] = [image_id for _, image_id in scored[:16]]


def build(archive_root: Path, backend_json: Path, out: Path) -> dict:
    with backend_json.open("r", encoding="utf-8") as f:
        backend = json.load(f)

    images = load_manifest_images(archive_root)
    videos = enrich_videos(archive_root, load_video_rows(archive_root))
    rank_source_candidates(images, videos)

    all_items = images + videos
    prompt_groups = assign_prompt_groups(all_items)

    candidate_videos_by_image = defaultdict(list)
    for video in videos:
        for image_id in video.get("sourceCandidates") or []:
            candidate_videos_by_image[image_id].append(video["id"])
    for image in images:
        image["candidateVideoIds"] = candidate_videos_by_image.get(image["id"], [])[:80]

    media_counts = defaultdict(int)
    for video in videos:
        if video.get("mediaUrl"):
            media_counts[video["mediaUrl"]] += 1
    for video in videos:
        video["duplicateMediaCount"] = media_counts.get(video.get("mediaUrl") or "", 0)

    index = {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "basePath": str(archive_root),
        "stats": {
            "backendMediaPosts": len(backend.get("media_posts") or []),
            "images": len(images),
            "videos": len(videos),
            "downloadedVideos": sum(1 for v in videos if v.get("status") == "downloaded"),
            "failedVideos": sum(1 for v in videos if v.get("status") == "failed"),
            "suspectVideos": sum(1 for v in videos if v.get("suspectMedia")),
            "promptGroups": len(prompt_groups),
        },
        "items": sorted(all_items, key=lambda x: (x["t"], x["id"])),
        "promptGroups": prompt_groups,
    }

    out.write_text(
        "window.GROK_ARCHIVE_INDEX = "
        + json.dumps(index, ensure_ascii=False, separators=(",", ":"))
        + ";\n",
        encoding="utf-8",
    )
    return index


def main() -> int:
    parser = argparse.ArgumentParser(description="Build archive_index.js for a local Grok web export archive")
    parser.add_argument("--archive-root", required=True, type=Path)
    parser.add_argument("--backend-json", required=True, type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()

    archive_root = args.archive_root.resolve()
    out = args.out or archive_root / "archive_index.js"
    index = build(archive_root, args.backend_json.resolve(), out)
    print(f"Wrote {out}")
    print(json.dumps(index["stats"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
