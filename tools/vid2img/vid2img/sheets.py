"""Step 3b: pack frames into timestamp-labeled 4x4 contact sheets.

gpt-image-2 /edits accepts at most 16 input images, so if a video needs more
than 16 sheets the frame list is thinned evenly to fit.
"""

import math
import shutil
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont

from .common import die, ts_label

MAX_INPUT_IMAGES = 16
THUMB_W = 480
COLS = 4
ROWS = 4


def build_sheets(sdir: Path) -> None:
    fdir = sdir / "frames"
    shdir = sdir / "sheets"
    if shdir.exists():
        shutil.rmtree(shdir)
    shdir.mkdir()

    frames = sorted(fdir.glob("frame_*.jpg"))
    if not frames:
        die("no frames extracted")

    per_sheet = COLS * ROWS
    n_sheets = math.ceil(len(frames) / per_sheet)
    if n_sheets > MAX_INPUT_IMAGES:
        keep = MAX_INPUT_IMAGES * per_sheet
        step = len(frames) / keep
        frames = [frames[int(i * step)] for i in range(keep)]
        n_sheets = MAX_INPUT_IMAGES

    try:
        font = ImageFont.truetype(
            "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf", 22)
    except OSError:
        font = ImageFont.load_default()

    with Image.open(frames[0]) as first:
        thumb_h = round(THUMB_W * first.height / first.width)

    for s in range(n_sheets):
        chunk = frames[s * per_sheet:(s + 1) * per_sheet]
        rows = math.ceil(len(chunk) / COLS)
        sheet = Image.new("RGB", (COLS * THUMB_W, rows * thumb_h), "black")
        draw = ImageDraw.Draw(sheet)
        for i, fpath in enumerate(chunk):
            t = float(fpath.stem.split("_t")[1])
            with Image.open(fpath) as im:
                im = im.resize((THUMB_W, thumb_h))
                x, y = (i % COLS) * THUMB_W, (i // COLS) * thumb_h
                sheet.paste(im, (x, y))
            draw.rectangle([x + 4, y + 4, x + 78, y + 32], fill="black")
            draw.text((x + 8, y + 6), ts_label(t), fill="yellow", font=font)
        sheet.save(shdir / f"sheet_{s + 1:02d}.jpg", quality=88)
    print(f"sheets: {n_sheets} in {shdir}")
