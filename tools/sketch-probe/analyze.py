#!/usr/bin/env python3
"""Score sketch-probe outputs: did each endpoint copy the sketch's colors or
place each subject with its own natural colors?

Every output is resized onto the stimulus geometry (1024x1024) and each
region's interior pixels (polygon shrunk 20% toward its center to avoid
boundary mixing) are classified into the hue bins from regions.json:

  copy_frac   - fraction matching the region's SKETCH color hue
                (color was copied; the layout may or may not be followed)
  follow_frac - fraction matching the subject's NATURAL color hue
                (subject placed there in its own colors = the goal)

Because the sketch colors are a derangement of the natural colors, the two
outcomes are always different hues for the same region. Per-region verdicts
aggregate into a per-image verdict, and a composite grid PNG + markdown table
are written for visual review.
"""

import colorsys
import json
import os
import sys

from PIL import Image, ImageDraw, ImageFont

OUT = sys.argv[1] if len(sys.argv) > 1 else "/tmp/sketch-probe-out"
SIZE = 1024
CONDITION_ORDER = ["A_color_current", "B_color_hard", "C_diagram",
                   "D_diagram_numbers", "E_diagram_plain", "F_plain_fullbleed"]
FONT_PATH = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"


def region_mask(region, shrink=0.20):
    cx, cy = region["center"]
    points = [(cx + (x - cx) * (1 - shrink), cy + (y - cy) * (1 - shrink))
              for x, y in region["points"]]
    mask = Image.new("L", (SIZE, SIZE), 0)
    ImageDraw.Draw(mask).polygon(points, fill=255)
    return mask


def classify(image, mask, hue_bins):
    """Return {bin: fraction} over the masked pixels of `image` (1024 RGB)."""
    pixels = image.load()
    mask_px = mask.load()
    counts = {name: 0 for name in hue_bins}
    total = 0
    for y in range(0, SIZE, 4):
        for x in range(0, SIZE, 4):
            if mask_px[x, y] == 0:
                continue
            r, g, b = pixels[x, y][:3]
            h, s, v = colorsys.rgb_to_hsv(r / 255, g / 255, b / 255)
            if v < 0.12:
                continue
            total += 1
            deg = h * 360
            for name, (lo, hi, min_sat) in hue_bins.items():
                if lo <= deg <= hi and s >= min_sat:
                    counts[name] += 1
                    break
    if total == 0:
        return {name: 0.0 for name in hue_bins}
    return {name: counts[name] / total for name in hue_bins}


def verdict(copy_frac, follow_frac):
    if follow_frac >= 0.22 and follow_frac > 1.5 * copy_frac:
        return "FOLLOW"
    if copy_frac >= 0.22 and copy_frac > 1.5 * follow_frac:
        return "COPY"
    if copy_frac >= 0.15 and follow_frac >= 0.15:
        return "MIXED"
    return "NEITHER"


def main():
    geometry = json.load(open(os.path.join(OUT, "regions.json")))
    manifest = json.load(open(os.path.join(OUT, "manifest.json")))
    hue_bins = {k: tuple(v) for k, v in geometry["hue_bins"].items()}
    masks = [(r, region_mask(r)) for r in geometry["regions"]]

    generators = manifest["generators"]
    scores = {}
    for key, record in manifest["results"].items():
        if not record.get("ok") or "file" not in record:
            scores[key] = {"error": record.get("error") or "no image"}
            continue
        img = Image.open(os.path.join(OUT, record["file"])).convert("RGB")
        img = img.resize((SIZE, SIZE), Image.LANCZOS)
        regions_out = {}
        for region, mask in masks:
            fracs = classify(img, mask, hue_bins)
            c, f = fracs[region["copy_bin"]], fracs[region["follow_bin"]]
            regions_out[region["id"]] = {
                "copy_frac": round(c, 3),
                "follow_frac": round(f, 3),
                "verdict": verdict(c, f),
                "all_bins": {k: round(v, 3) for k, v in fracs.items()},
            }
        vs = [r["verdict"] for r in regions_out.values()]
        summary = f"{vs.count('FOLLOW')}F/{vs.count('COPY')}C/{vs.count('MIXED')}M/{vs.count('NEITHER')}N"
        scores[key] = {
            "regions": regions_out,
            "summary": summary,
            "follow_mean": round(sum(r["follow_frac"] for r in regions_out.values()) / 4, 3),
            "copy_mean": round(sum(r["copy_frac"] for r in regions_out.values()) / 4, 3),
        }
    with open(os.path.join(OUT, "scores.json"), "w") as fh:
        json.dump(scores, fh, indent=1)

    # ---- markdown table ----
    lines = ["| endpoint | " + " | ".join(CONDITION_ORDER) + " |",
             "|---|" + "---|" * len(CONDITION_ORDER)]
    for gen in generators:
        cells = []
        for cond in CONDITION_ORDER:
            s = scores.get(f"{cond}/{gen}")
            if s is None:
                cells.append("-")
            elif "error" in s:
                cells.append("ERR")
            else:
                cells.append(f"{s['summary']} (f{s['follow_mean']}/c{s['copy_mean']})")
        lines.append(f"| {gen} | " + " | ".join(cells) + " |")
    table = "\n".join(lines)
    with open(os.path.join(OUT, "report.md"), "w") as fh:
        fh.write("Per cell: FOLLOW/COPY/MIXED/NEITHER region counts, then mean "
                 "follow-fraction and copy-fraction over the four regions.\n\n")
        fh.write(table + "\n")
    print(table)

    # ---- composite grid PNG ----
    cell = 256
    font = ImageFont.truetype(FONT_PATH, 18)
    small = ImageFont.truetype(FONT_PATH, 15)
    cols = 1 + len(CONDITION_ORDER)
    rows = 1 + len(generators)
    grid = Image.new("RGB", (cols * cell, rows * (cell + 26)), "white")
    draw = ImageDraw.Draw(grid)

    def paste(img_path, col, row, caption):
        x, y = col * cell, row * (cell + 26)
        try:
            im = Image.open(img_path).convert("RGB").resize((cell, cell))
            grid.paste(im, (x, y))
        except Exception:
            draw.rectangle([x, y, x + cell, y + cell], outline="red")
            draw.text((x + 8, y + cell // 2), "missing", fill="red", font=font)
        draw.text((x + 4, y + cell + 3), caption[:34], fill="black", font=small)

    stimulus_by_condition = {
        "A_color_current": "sketch.png",
        "B_color_hard": "sketch.png",
        "C_diagram": "diagram.png",
        "D_diagram_numbers": "diagram_numbers.png",
        "E_diagram_plain": "diagram_plain.png",
        "F_plain_fullbleed": "diagram_plain.png",
    }
    for i, cond in enumerate(CONDITION_ORDER, start=1):
        paste(os.path.join(OUT, stimulus_by_condition[cond]), i, 0,
              f"stimulus: {stimulus_by_condition[cond]}")
    draw.text((6, cell // 2), "stimuli", fill="black", font=font)
    for i, cond in enumerate(CONDITION_ORDER):
        draw.text(((1 + i) * cell + 4, 4), cond, fill="blue", font=font)

    for r, gen in enumerate(generators, start=1):
        draw.text((6, r * (cell + 26) + cell // 2), gen, fill="black", font=font)
        for c, cond in enumerate(CONDITION_ORDER, start=1):
            key = f"{cond}/{gen}"
            s = scores.get(key, {})
            caption = s.get("summary", s.get("error", "missing"))
            rec = manifest["results"].get(key, {})
            path = os.path.join(OUT, rec["file"]) if rec.get("file") else "/nonexistent"
            paste(path, c, r, f"{caption}")
    grid_path = os.path.join(OUT, "grid.png")
    grid.save(grid_path)
    print(f"wrote {grid_path} and {OUT}/scores.json and {OUT}/report.md")


if __name__ == "__main__":
    main()
