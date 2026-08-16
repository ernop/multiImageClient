#!/usr/bin/env python3
"""Generate the sketch-probe test stimuli.

Two PNG stimuli share identical region geometry (four organic blobs, one per
quadrant), so every endpoint sees the same layout task:

  sketch.png   - flat-color regions in DELIBERATELY WRONG colors: each region
                 is drawn in the natural color of a DIFFERENT region's theme
                 (a derangement), so "copied the sketch color" and "chose the
                 subject's natural color" are distinct, measurable hue
                 outcomes per quadrant.
  diagram.png  - the same regions as black outlines on white with a printed
                 text label inside each region and zero color information.

regions.json records the geometry, themes, sketch colors, and the hue bins the
analyzer uses, so generation and analysis cannot drift apart.
"""

import json
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFont

SIZE = 1024
FONT_PATH = "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"

# Themes and their deliberately mismatched sketch colors. Natural-color hue
# bins are (lo_deg, hi_deg, min_saturation); the derangement maps each
# region's sketch color onto ANOTHER region's natural hue:
#   forest(green) drawn blue, desert(tan) drawn purple,
#   ocean(blue) drawn yellow, lavender(purple) drawn green.
HUE_BINS = {
    "green": (70, 165, 0.18),
    "yellow_tan": (32, 68, 0.12),
    "blue": (195, 255, 0.18),
    "purple": (255, 320, 0.12),
}

REGIONS = [
    {
        "id": "forest",
        "quadrant": "top-left",
        "center": (280, 280),
        "radius": 205,
        "phase": 0.7,
        "theme": "dense pine forest",
        "label": "PINE FOREST",
        "sketch_color": "#1971c2",  # blue
        "copy_bin": "blue",
        "follow_bin": "green",
    },
    {
        "id": "desert",
        "quadrant": "top-right",
        "center": (744, 280),
        "radius": 205,
        "phase": 2.1,
        "theme": "hot sandy desert with dunes",
        "label": "SANDY DESERT",
        "sketch_color": "#9c36b5",  # purple
        "copy_bin": "purple",
        "follow_bin": "yellow_tan",
    },
    {
        "id": "ocean",
        "quadrant": "bottom-left",
        "center": (280, 744),
        "radius": 205,
        "phase": 4.0,
        "theme": "deep ocean water",
        "label": "OCEAN WATER",
        "sketch_color": "#f2c230",  # yellow
        "copy_bin": "yellow_tan",
        "follow_bin": "blue",
    },
    {
        "id": "lavender",
        "quadrant": "bottom-right",
        "center": (744, 744),
        "radius": 205,
        "phase": 5.3,
        "theme": "blooming lavender field",
        "label": "LAVENDER FIELD",
        "sketch_color": "#2f9e44",  # green
        "copy_bin": "green",
        "follow_bin": "purple",
    },
]


def blob_points(center, radius, phase, steps=72):
    cx, cy = center
    points = []
    for i in range(steps):
        theta = 2 * math.pi * i / steps
        r = radius * (1 + 0.16 * math.sin(3 * theta + phase)
                      + 0.09 * math.sin(7 * theta + 2.3 * phase))
        points.append((cx + r * math.cos(theta), cy + r * math.sin(theta)))
    return points


def main(out_dir):
    os.makedirs(out_dir, exist_ok=True)

    sketch = Image.new("RGB", (SIZE, SIZE), "white")
    sketch_draw = ImageDraw.Draw(sketch)
    diagram = Image.new("RGB", (SIZE, SIZE), "white")
    diagram_draw = ImageDraw.Draw(diagram)
    font = ImageFont.truetype(FONT_PATH, 46)

    manifest = {"size": SIZE, "hue_bins": HUE_BINS, "regions": []}
    for region in REGIONS:
        points = blob_points(region["center"], region["radius"], region["phase"])
        sketch_draw.polygon(points, fill=region["sketch_color"])
        diagram_draw.polygon(points, outline="black", width=6)
        # Two-line centered label inside the blob.
        words = region["label"].split(" ")
        lines = [words[0], " ".join(words[1:])] if len(words) > 1 else words
        cx, cy = region["center"]
        line_h = 54
        top = cy - line_h * len(lines) / 2
        for i, line in enumerate(lines):
            bbox = diagram_draw.textbbox((0, 0), line, font=font)
            w = bbox[2] - bbox[0]
            diagram_draw.text((cx - w / 2, top + i * line_h), line,
                              fill="black", font=font)
        entry = {k: v for k, v in region.items()}
        entry["points"] = [(round(x, 1), round(y, 1)) for x, y in points]
        manifest["regions"].append(entry)

    # Phase-2 variants that shrink or remove the in-image text channel:
    # diagram_numbers.png - same outlines, one small circled digit per region
    # diagram_plain.png   - outlines only, zero text
    numbers = Image.new("RGB", (SIZE, SIZE), "white")
    numbers_draw = ImageDraw.Draw(numbers)
    plain = Image.new("RGB", (SIZE, SIZE), "white")
    plain_draw = ImageDraw.Draw(plain)
    digit_font = ImageFont.truetype(FONT_PATH, 44)
    for i, region in enumerate(REGIONS, start=1):
        points = blob_points(region["center"], region["radius"], region["phase"])
        numbers_draw.polygon(points, outline="black", width=6)
        plain_draw.polygon(points, outline="black", width=6)
        cx, cy = region["center"]
        r = 38
        numbers_draw.ellipse([cx - r, cy - r, cx + r, cy + r], outline="black", width=5)
        text = str(i)
        bbox = numbers_draw.textbbox((0, 0), text, font=digit_font)
        w, h = bbox[2] - bbox[0], bbox[3] - bbox[1]
        numbers_draw.text((cx - w / 2 - bbox[0], cy - h / 2 - bbox[1]), text,
                          fill="black", font=digit_font)
    numbers.save(os.path.join(out_dir, "diagram_numbers.png"))
    plain.save(os.path.join(out_dir, "diagram_plain.png"))

    sketch.save(os.path.join(out_dir, "sketch.png"))
    diagram.save(os.path.join(out_dir, "diagram.png"))
    with open(os.path.join(out_dir, "regions.json"), "w") as fh:
        json.dump(manifest, fh, indent=1)
    print(f"wrote sketch.png, diagram.png, regions.json to {out_dir}")


if __name__ == "__main__":
    main(sys.argv[1] if len(sys.argv) > 1 else "/tmp/sketch-probe-out")
