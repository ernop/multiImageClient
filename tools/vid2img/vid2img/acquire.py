"""Step 1-2: resolve a YouTube URL or local file into sessions/<name>/source.<ext>."""

import subprocess
from pathlib import Path

from .common import SESSIONS, die, run, slugify, video_duration


def resolve_name(src: str, explicit_name: str | None) -> tuple[str, bool]:
    """Return (session name, is_url). For URLs without --name, asks yt-dlp for the title."""
    is_url = src.startswith(("http://", "https://"))
    if explicit_name:
        return explicit_name, is_url
    if is_url:
        title = subprocess.run(
            ["yt-dlp", "--print", "title", "--no-download", src],
            capture_output=True, text=True, check=True).stdout.strip()
        return slugify(title), True
    return slugify(Path(src).expanduser().stem), False


def acquire(sdir: Path, src: str, is_url: bool) -> Path:
    """Download via yt-dlp (URL) or symlink (local file). Idempotent."""
    source = next((p for p in sdir.glob("source.*")), None)
    if source is not None:
        print(f"source already present: {source.name}")
        return source

    if is_url:
        print(f"downloading: {src}")
        run(["yt-dlp", "-f", "bv*[height<=1080]+ba/b[height<=1080]/b",
             "-o", str(sdir / "source.%(ext)s"), src])
        source = next((p for p in sdir.glob("source.*")), None)
        if source is None:
            die("yt-dlp reported success but no source.* file appeared")
    else:
        src_path = Path(src).expanduser().resolve()
        if not src_path.exists():
            die(f"file not found: {src_path}")
        source = sdir / f"source{src_path.suffix}"
        source.symlink_to(src_path)

    print(f"source: {source.name} ({video_duration(source):.0f}s)")
    return source
