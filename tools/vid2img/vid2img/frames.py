"""Step 3a: extract frames -- regular fps sampling plus extra frames at scene cuts."""

import re
import shutil
import subprocess
from pathlib import Path

from .common import run, video_duration

SCENE_THRESHOLD = 0.35
# A scene-cut frame this close (seconds) to an existing sample adds nothing.
DEDUPE_WINDOW = 0.4


def extract_frames(sdir: Path, source: Path, fps: float = 1.0, scene: bool = True) -> None:
    fdir = sdir / "frames"
    if fdir.exists():
        shutil.rmtree(fdir)
    fdir.mkdir()
    dur = video_duration(source)

    # Regular sampling at `fps` in a single ffmpeg pass.
    tmp = sdir / "_grid"
    tmp.mkdir(exist_ok=True)
    run(["ffmpeg", "-v", "error", "-i", str(source),
         "-vf", f"fps={fps}", "-pix_fmt", "yuvj420p", "-q:v", "3",
         str(tmp / "g_%05d.jpg")])
    times = []
    for i, p in enumerate(sorted(tmp.glob("g_*.jpg"))):
        t = i / fps
        p.rename(fdir / f"t{t:07.2f}.jpg")
        times.append(t)
    tmp.rmdir()

    if scene:
        for t in detect_scene_cuts(source):
            if t > dur - 0.2 or any(abs(t - x) <= DEDUPE_WINDOW for x in times):
                continue
            res = subprocess.run(
                ["ffmpeg", "-v", "error", "-ss", f"{t:.2f}", "-i", str(source),
                 "-frames:v", "1", "-pix_fmt", "yuvj420p", "-q:v", "3",
                 str(fdir / f"t{t:07.2f}.jpg")])
            if res.returncode == 0:
                times.append(t)

    # Rename to sequential order now that all times are known; the timestamp
    # stays in the filename (frame_NNNN_tSSSS.SS.jpg) for sheet labeling.
    for i, p in enumerate(sorted(fdir.glob("t*.jpg")), 1):
        p.rename(fdir / f"frame_{i:04d}_{p.name}")
    print(f"frames: {len(list(fdir.glob('*.jpg')))} in {fdir}")


def detect_scene_cuts(source: Path) -> list[float]:
    out = subprocess.run(
        ["ffmpeg", "-i", str(source), "-vf",
         f"select='gt(scene,{SCENE_THRESHOLD})',metadata=print", "-f", "null", "-"],
        capture_output=True, text=True)
    return [float(m.group(1)) for m in re.finditer(r"pts_time:([\d.]+)", out.stderr)]
