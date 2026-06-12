import argparse
import csv
import datetime
import json
import re
import zipfile
from pathlib import Path


ASSET_RE = re.compile(r"prod-mc-asset-server//([0-9a-f-]{36})/content$")
UUID_RE = re.compile(r"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}")


def slug(text: str, maxlen: int = 80) -> str:
    value = re.sub(r"[^A-Za-z0-9 ]+", "", text or "").strip()
    value = re.sub(r"\s+", "_", value)
    return value[:maxlen] or "no_prompt"


def sniff_ext(head: bytes) -> str:
    if head[:3] == b"\xff\xd8\xff":
        return ".jpg"
    if head[:8] == b"\x89PNG\r\n\x1a\n":
        return ".png"
    if head[:4] == b"RIFF":
        return ".webp"
    if head[4:8] == b"ftyp":
        return ".mp4"
    return ".bin"


def find_backend_json(zf: zipfile.ZipFile, preferred_name: str) -> str:
    names = zf.namelist()
    if preferred_name in names:
        return preferred_name
    matches = [name for name in names if name.endswith("prod-grok-backend.json")]
    if not matches:
        raise FileNotFoundError("Could not find prod-grok-backend.json in export zip")
    return matches[0]


def load_backend(zf: zipfile.ZipFile, backend_name: str) -> dict:
    with zf.open(backend_name) as f:
        return json.loads(f.read().decode("utf-8"))


def collect_upload_ids_and_chat_cards(data: dict) -> tuple[set[str], list[dict]]:
    upload_ids: set[str] = set()
    chat_cards: list[dict] = []
    for conversation in data.get("conversations") or []:
        title = (conversation.get("conversation") or {}).get("title", "")
        for response_row in conversation.get("responses") or []:
            response = response_row.get("response") or {}
            for attachment in response.get("file_attachments") or []:
                upload_ids.update(UUID_RE.findall(json.dumps(attachment)))

            create_time = response.get("create_time")
            ms = int(create_time["$date"]["$numberLong"]) if isinstance(create_time, dict) else 0
            for card_json in response.get("card_attachments_json") or []:
                try:
                    card = json.loads(card_json)
                except Exception:
                    continue
                chunk = card.get("image_chunk") or {}
                if chunk.get("imageUuid") and chunk.get("progress") == 100:
                    chat_cards.append(
                        {
                            "uuid": chunk["imageUuid"],
                            "ms": ms,
                            "conv_title": title,
                            "prompt": (chunk.get("imagePrompt") or {}).get("prompt", ""),
                            "model": chunk.get("imageModel", ""),
                            "moderated": chunk.get("moderated", False),
                        }
                    )
    return upload_ids, chat_cards


def extract(zip_path: Path, dest: Path, backend_name: str) -> dict:
    dest.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(zip_path) as zf:
        backend_zip_name = find_backend_json(zf, backend_name)
        data = load_backend(zf, backend_zip_name)
        (dest / "prod-grok-backend.json").write_text(json.dumps(data, ensure_ascii=False), encoding="utf-8")

        zip_paths = {}
        for name in zf.namelist():
            match = ASSET_RE.search(name)
            if match:
                zip_paths[match.group(1)] = name

        posts_by_id = {post["id"]: post for post in data.get("media_posts") or []}
        upload_ids, chat_cards = collect_upload_ids_and_chat_cards(data)

        for subdir in ("Images", "Uploads", "Unmapped"):
            (dest / subdir).mkdir(parents=True, exist_ok=True)

        manifest = []
        counts = {"Images": 0, "Uploads": 0, "Unmapped": 0}
        for uid, zpath in zip_paths.items():
            raw = zf.read(zpath)
            ext = sniff_ext(raw[:16])
            post = posts_by_id.get(uid)
            if post:
                ts = post["create_time"].replace("-", "").replace(":", "")[:15].replace("T", "_")
                name = f"{ts}_{slug(post.get('original_prompt', ''))}_{uid[:8]}{ext}"
                subdir, source = "Images", "media_post"
                prompt, ctime = post.get("original_prompt", ""), post.get("create_time", "")
            elif uid in upload_ids:
                zdt = datetime.datetime(*zf.getinfo(zpath).date_time)
                name = f"{zdt:%Y%m%d_%H%M%S}_upload_{uid[:8]}{ext}"
                subdir, source = "Uploads", "file_attachment"
                prompt, ctime = "", zdt.isoformat()
            else:
                zdt = datetime.datetime(*zf.getinfo(zpath).date_time)
                name = f"{zdt:%Y%m%d_%H%M%S}_{uid}{ext}"
                subdir, source = "Unmapped", "unknown"
                prompt, ctime = "", zdt.isoformat()

            out = dest / subdir / name
            if not (out.exists() and out.stat().st_size == len(raw)):
                out.write_bytes(raw)
            counts[subdir] += 1
            manifest.append(
                {
                    "id": uid,
                    "kind": "image",
                    "source": source,
                    "createTime": ctime,
                    "prompt": prompt,
                    "file": f"{subdir}/{name}",
                    "bytes": len(raw),
                }
            )

        vids = sorted(
            (post for post in data.get("media_posts") or [] if post.get("media_type") == "video"),
            key=lambda post: post.get("create_time", ""),
        )
        with (dest / "videos.csv").open("w", newline="", encoding="utf-8") as f:
            writer = csv.writer(f)
            writer.writerow(["create_time", "prompt", "link"])
            for post in vids:
                writer.writerow([post.get("create_time", ""), post.get("original_prompt", ""), post.get("link", "")])

        with (dest / "manifest.jsonl").open("w", encoding="utf-8") as f:
            for row in sorted(manifest, key=lambda row: row["createTime"]):
                f.write(json.dumps(row, ensure_ascii=False) + "\n")

        prompt_events = []
        for post in data.get("media_posts") or []:
            if post.get("original_prompt"):
                prompt_events.append((post.get("create_time", ""), f"imagine-{post.get('media_type', '')}", post["original_prompt"]))
        for card in chat_cards:
            if card["prompt"]:
                t = datetime.datetime.fromtimestamp(card["ms"] / 1000, datetime.timezone.utc).isoformat()
                kind = f"chat-image ({card['model']})" + (" [moderated]" if card["moderated"] else "")
                prompt_events.append((t, kind, card["prompt"]))
        prompt_events.sort()
        with (dest / "prompts.txt").open("w", encoding="utf-8") as f:
            f.write(f"# Every prompt in grok.com export - {len(prompt_events)} entries\n\n")
            for t, kind, prompt in prompt_events:
                f.write(f"[{t}] {kind}\n{prompt}\n\n")

    return {
        "assets": counts,
        "manifest_entries": len(manifest),
        "video_posts": len(vids),
        "prompt_entries": len(prompt_events),
        "chat_image_cards": len(chat_cards),
    }


def main() -> int:
    parser = argparse.ArgumentParser(description="Extract a grok.com data export zip into a local archive root")
    parser.add_argument("--zip", required=True, type=Path, dest="zip_path")
    parser.add_argument("--archive-root", required=True, type=Path)
    parser.add_argument("--backend-name", default="prod-grok-backend.json")
    args = parser.parse_args()

    stats = extract(args.zip_path.resolve(), args.archive_root.resolve(), args.backend_name)
    print(json.dumps(stats, indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
