"""Shared paths and small helpers used by every vid2img pipeline step."""

import json
import os
import re
import subprocess
import sys
from pathlib import Path

TOOL_ROOT = Path(__file__).resolve().parents[1]
SESSIONS = TOOL_ROOT / "sessions"
REPO_ROOT = TOOL_ROOT.parents[1]


def die(msg: str) -> None:
    print(f"error: {msg}", file=sys.stderr)
    sys.exit(1)


def run(cmd: list, **kw) -> subprocess.CompletedProcess:
    return subprocess.run(cmd, check=True, **kw)


def slugify(text: str) -> str:
    text = re.sub(r"[^\w\s-]", "", text).strip().lower()
    return re.sub(r"[\s_-]+", "-", text)[:60] or "video"


def ts_label(seconds: float) -> str:
    m, s = divmod(int(seconds), 60)
    return f"{m:02d}:{s:02d}"


def video_duration(path: Path) -> float:
    out = subprocess.run(
        ["ffprobe", "-v", "error", "-show_entries", "format=duration",
         "-of", "csv=p=0", str(path)],
        capture_output=True, text=True, check=True)
    return float(out.stdout.strip())


def session_dir(name: str, must_exist: bool = True) -> Path:
    sdir = SESSIONS / name
    if must_exist and not sdir.exists():
        have = ", ".join(sorted(p.name for p in SESSIONS.glob("*") if p.is_dir())) \
            if SESSIONS.exists() else ""
        die(f"no session '{name}' (run 'new' first). Have: {have or 'none'}")
    return sdir


def session_source(sdir: Path) -> Path:
    source = next((p for p in sdir.glob("source.*")), None)
    if source is None:
        die(f"no source video in {sdir} (run 'new' first)")
    return source


def load_history(sdir: Path) -> list:
    f = sdir / "session.json"
    return json.loads(f.read_text()) if f.exists() else []


def save_history(sdir: Path, history: list) -> None:
    (sdir / "session.json").write_text(json.dumps(history, indent=1))


def api_key() -> str:
    key = os.environ.get("OPENAI_API_KEY")
    if key:
        return key
    settings = REPO_ROOT / "MultiImageClient" / "settings.json"
    if settings.exists():
        key = json.loads(settings.read_text()).get("OpenAIApiKey")
        if key:
            return key
    die("no OPENAI_API_KEY in env and no OpenAIApiKey in MultiImageClient/settings.json")
