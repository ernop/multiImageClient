"""Build a normalized relationship graph for a Grok Imagine archive.

Merges the authoritative REST harvest (`rest_posts.jsonl`, produced by harvesting
grok.com's logged-in `rest/media/post/list` via the browser + harvest_relay.py)
with the locally downloaded assets (`video_downloads.jsonl`, `manifest.jsonl`)
into a single `archive_graph.js` that the visualizer consumes.

The REST data carries the real lineage the flat export lacks:
  - image -> video:      child.originalPostId = source image, child.mode = guide,
                         child.videoDuration, child.prompt
  - video -> extension:  child.originalRefType = ORIGINAL_REF_TYPE_VIDEO_EXTENSION,
                         child.originalPostId = parent video, child.videoExtensionStartTime
  - image edit:          ORIGINAL_REF_TYPE_IMAGE_EDIT / _MULTI_REF_IMAGE_EDIT,
                         child.inputMediaItems = source image(s)

Nodes are identified by UUID and merged across sources; edges record provenance and
confidence (1.0 = authoritative REST lineage).

Output is archive/user data (personal prompts, IDs, NSFW). Keep it outside the repo.
"""

import argparse
import json
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path


def parse_ms(value: str) -> int:
    if not value:
        return 0
    try:
        return int(datetime.fromisoformat(value.replace("Z", "+00:00")).timestamp() * 1000)
    except ValueError:
        return 0


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


def relpath(path: str, archive_root: Path) -> str:
    if not path:
        return ""
    try:
        return Path(path).resolve().relative_to(archive_root).as_posix()
    except ValueError:
        return Path(path).as_posix()


REF_KIND = {
    "ORIGINAL_REF_TYPE_VIDEO_EXTENSION": "extends",
    "ORIGINAL_REF_TYPE_IMAGE_EDIT": "editOf",
    "ORIGINAL_REF_TYPE_MULTI_REF_IMAGE_EDIT": "editOf",
}


def node_type(media_type: str) -> str:
    return "video" if media_type == "MEDIA_POST_TYPE_VIDEO" else "image"


def flatten_rest(rest_path: Path):
    """Yield every post (top-level and nested childPosts), de-duped by id."""
    seen = {}

    def walk(post):
        pid = post.get("id")
        if not pid:
            return
        if pid not in seen:
            seen[pid] = post
        for child in post.get("childPosts") or []:
            walk(child)

    for top in read_jsonl(rest_path):
        walk(top)
    return seen


def build(archive_root: Path, out: Path) -> dict:
    rest = flatten_rest(archive_root / "rest_posts.jsonl")

    local_video = {}
    for row in read_jsonl(archive_root / "video_downloads.jsonl"):
        pid, file_name = row.get("post_id"), row.get("file")
        if pid and file_name and Path(file_name).exists():
            local_video[pid] = relpath(file_name, archive_root)

    local_image = {}
    for row in read_jsonl(archive_root / "manifest.jsonl"):
        iid, file_name = row.get("id"), row.get("file")
        if iid and file_name:
            local_image[iid] = file_name  # manifest files are already relative

    # source images harvested from the API into ApiImages/<id>.<ext>
    api_images_dir = archive_root / "ApiImages"
    if api_images_dir.is_dir():
        for f in api_images_dir.iterdir():
            if f.is_file() and f.stat().st_size > 0:
                local_image.setdefault(f.stem, "ApiImages/" + f.name)

    nodes: dict[str, dict] = {}

    def ensure(node_id: str) -> dict:
        if node_id not in nodes:
            nodes[node_id] = {"id": node_id, "provenance": []}
        return nodes[node_id]

    # 1) authoritative REST nodes
    for pid, p in rest.items():
        n = ensure(pid)
        ntype = node_type(p.get("mediaType", ""))
        res = p.get("resolution") or {}
        n.update({
            "type": ntype,
            "createTime": p.get("createTime") or "",
            "t": parse_ms(p.get("createTime") or ""),
            "prompt": p.get("prompt") or p.get("originalPrompt") or "",
            "parentId": p.get("originalPostId") or None,
            "refType": p.get("originalRefType") or None,
            "mode": p.get("mode") or None,
            "model": p.get("modelName") or None,
            "durationSec": p.get("videoDuration") or None,
            "extStartSec": p.get("videoExtensionStartTime") or None,
            "w": res.get("width") or None,
            "h": res.get("height") or None,
            "moderated": bool(p.get("moderated")),
            "rRated": bool(p.get("rRated")),
            "rootUpload": bool(p.get("isRootUserUploaded")),
            "remoteMediaUrl": (p.get("hdMediaUrl") or p.get("mediaUrl") or "").split("?")[0],
            "remoteThumb": (p.get("thumbnailImageUrl") or "").split("?")[0],
            "remoteLastFrame": (p.get("lastFrameThumbnailImageUrl") or "").split("?")[0],
        })
        if "rest_api" not in n["provenance"]:
            n["provenance"].append("rest_api")

    # 2) local-only assets (downloaded/exported but not in the liked graph)
    for pid, rel in local_video.items():
        n = ensure(pid)
        n.setdefault("type", "video")
        if "export" not in n["provenance"] and "rest_api" not in n["provenance"]:
            n["provenance"].append("export")
    for iid, rel in local_image.items():
        n = ensure(iid)
        n.setdefault("type", "image")
        if "export" not in n["provenance"] and "rest_api" not in n["provenance"]:
            n["provenance"].append("export")

    # attach local files + status
    for nid, n in nodes.items():
        if n.get("type") == "video":
            local = local_video.get(nid)
        else:
            local = local_image.get(nid)
        n["file"] = local or ""
        n["status"] = "downloaded" if local else "remote"
        n.setdefault("createTime", "")
        n.setdefault("t", 0)
        n.setdefault("prompt", "")
        n.setdefault("parentId", None)

    # 3) edges from authoritative parent links
    edges = []
    for nid, n in nodes.items():
        parent = n.get("parentId")
        if not parent or parent not in nodes:
            continue
        ref = n.get("refType")
        if ref in REF_KIND:
            kind = REF_KIND[ref]
        else:
            kind = "derivedFrom" if n.get("type") == "video" else "variantOf"
        edge = {"from": nid, "to": parent, "kind": kind, "confidence": 1.0}
        if n.get("extStartSec"):
            edge["startSec"] = n["extStartSec"]
        edges.append(edge)

    # 4) clusters: connected lineage rooted at the earliest ancestor
    parent_of = {n["id"]: (n.get("parentId") if n.get("parentId") in nodes else None) for n in nodes.values()}

    def root_of(node_id: str) -> str:
        seen = set()
        cur = node_id
        while parent_of.get(cur) and cur not in seen:
            seen.add(cur)
            cur = parent_of[cur]
        return cur

    clusters = defaultdict(list)
    for nid in nodes:
        clusters[root_of(nid)].append(nid)

    children = defaultdict(list)
    for nid, par in parent_of.items():
        if par:
            children[par].append(nid)
    for n in nodes.values():
        n["childCount"] = len(children.get(n["id"], []))

    cluster_list = []
    for root_id, members in clusters.items():
        members.sort(key=lambda m: (nodes[m]["t"], m))
        root = nodes[root_id]
        cluster_list.append({
            "rootId": root_id,
            "rootType": root.get("type"),
            "size": len(members),
            "videoCount": sum(1 for m in members if nodes[m].get("type") == "video"),
            "t": root.get("t", 0),
            "memberIds": members,
        })
    cluster_list.sort(key=lambda c: (-c["size"], -c["t"]))

    downloaded_videos = sum(1 for n in nodes.values() if n.get("type") == "video" and n["status"] == "downloaded")
    index = {
        "generatedAt": datetime.now(timezone.utc).isoformat(),
        "stats": {
            "nodes": len(nodes),
            "images": sum(1 for n in nodes.values() if n.get("type") == "image"),
            "videos": sum(1 for n in nodes.values() if n.get("type") == "video"),
            "downloadedVideos": downloaded_videos,
            "edges": len(edges),
            "extends": sum(1 for e in edges if e["kind"] == "extends"),
            "derivedFrom": sum(1 for e in edges if e["kind"] == "derivedFrom"),
            "edits": sum(1 for e in edges if e["kind"] == "editOf"),
            "clusters": len(cluster_list),
            "restNodes": sum(1 for n in nodes.values() if "rest_api" in n["provenance"]),
        },
        "nodes": sorted(nodes.values(), key=lambda n: (n.get("t", 0), n["id"])),
        "edges": edges,
        "clusters": cluster_list,
    }

    out.write_text(
        "window.GROK_GRAPH = " + json.dumps(index, ensure_ascii=False, separators=(",", ":")) + ";\n",
        encoding="utf-8",
    )
    return index


def main() -> int:
    parser = argparse.ArgumentParser(description="Build archive_graph.js from rest_posts.jsonl + local assets")
    parser.add_argument("--archive-root", required=True, type=Path)
    parser.add_argument("--out", type=Path)
    args = parser.parse_args()
    archive_root = args.archive_root.resolve()
    out = args.out or archive_root / "archive_graph.js"
    index = build(archive_root, out)
    print(f"Wrote {out}")
    print(json.dumps(index["stats"], indent=2))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
