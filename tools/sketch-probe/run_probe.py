#!/usr/bin/env python3
"""Submit the sketch-probe test matrix through the running --ui server.

Three conditions, identical base prompt and identical region meanings:

  A_color_current : colored sketch + the exact current production legend
  B_color_hard    : colored sketch + hardened anti-color-copy legend
  C_diagram       : colorless labeled line diagram + diagram legend

Each condition is one job fanned out to every probed endpoint by the server's
normal scheduler. Results are downloaded as they land; manifest.json records
per (condition, generator): ok/error, image file, cost, and the raw
gen-result event for later inspection.
"""

import json
import os
import sys
import time

import requests

BASE = os.environ.get("MIC_BASE", "http://127.0.0.1:5960")
OUT = sys.argv[1] if len(sys.argv) > 1 else "/tmp/sketch-probe-out"
USER = "sketch-probe"

GENERATORS = [
    # live-validated sketch-capable set
    "gpt2", "grok-web", "grok-web-chat", "grok-api", "grok-api-pro",
    "bfl", "bfl-flux2-pro", "bfl-flux2-max", "bfl-flux2-flex", "google",
    # previously excluded candidates: a colorless labeled diagram is a
    # different stimulus than the flat-color sketch they failed with
    "googlepro", "ideogram", "ideogram-v3",
    "bfl-kontext-pro", "bfl-kontext-max", "recraft",
]

BASE_PROMPT = (
    "A richly detailed aerial fantasy landscape seen from above, "
    "painted-illustration style. Clear, bright, full normal daytime "
    "lighting; vivid, attractive, balanced color; clean readable composition."
)

MEANINGS_COLOR = (
    "blue = dense pine forest; purple = hot sandy desert with dunes; "
    "yellow = deep ocean water; green = blooming lavender field"
)

LEGEND_A = (
    "Composition sketch legend: one attached image is a spatial map for the "
    "final image. Place each named subject where its mapped color, outline, "
    "scribble, or group of strokes appears, using roughly that share of the "
    "frame. Interpret sparse lines and repeated strokes as the approximate "
    "extent of one mapped region. The sketch colors identify subjects; "
    "choose the final palette, materials, lighting, and rendering style from "
    "the rest of this prompt and from each subject's natural appearance. "
    "White or unmarked areas are flexible background and negative space that "
    "may be used as needed to make the complete composition. In the sketch: "
    + MEANINGS_COLOR + ". Create a complete new image from this map and use "
    "the legend words as instructions."
)

LEGEND_B = (
    "Composition sketch legend: the attached image is a placement diagram, "
    "not artwork and not a style or color reference. Its flat colors are "
    "arbitrary region codes - deliberately unrelated to the correct final "
    "colors, so copying any of them is an error. Region codes: "
    + MEANINGS_COLOR + ". Put each subject where its code color appears, "
    "using roughly that share of the frame. Paint every subject in its own "
    "natural real-world colors, chosen from the rest of this prompt and the "
    "subject's real appearance - never from the diagram. Do not reuse the "
    "diagram's colors, flat fills, or hard edges anywhere; the finished "
    "image must not resemble the diagram's rendering in any way."
)

LEGEND_C = (
    "Composition sketch legend: the attached image is a colorless "
    "black-and-white placement diagram, not artwork. Each outlined region is "
    "labeled with the subject that belongs in exactly that area of the "
    "frame: PINE FOREST = dense pine forest; SANDY DESERT = hot sandy desert "
    "with dunes; OCEAN WATER = deep ocean water; LAVENDER FIELD = blooming "
    "lavender field. Use each region's position and rough share of the "
    "frame. Unmarked white areas are flexible background and negative "
    "space. Do not reproduce the diagram's black outlines, white background, "
    "or printed label text in the output - the labels are placement "
    "instructions, not content to render. Create a complete new, fully "
    "colored image; choose every color from each subject's natural "
    "appearance and from the rest of this prompt."
)

LEGEND_D = (
    "Composition sketch legend: the attached image is a colorless "
    "black-and-white placement diagram, not artwork. Each outlined region "
    "contains one small circled number naming the subject that belongs in "
    "exactly that area of the frame: 1 = dense pine forest; 2 = hot sandy "
    "desert with dunes; 3 = deep ocean water; 4 = blooming lavender field. "
    "Use each region's position and rough share of the frame. Unmarked "
    "white areas are flexible background and negative space. Do not "
    "reproduce the diagram's black outlines, circles, or digits in the "
    "output - they are placement instructions, not content to render. The "
    "finished image must contain no text, no digits, and no outlines. "
    "Create a complete new, fully colored image; choose every color from "
    "each subject's natural appearance and from the rest of this prompt."
)

LEGEND_E = (
    "Composition sketch legend: the attached image is a colorless "
    "black-and-white placement diagram, not artwork. It shows four outlined "
    "regions: the top-left region is a dense pine forest; the top-right "
    "region is a hot sandy desert with dunes; the bottom-left region is "
    "deep ocean water; the bottom-right region is a blooming lavender "
    "field. Use each region's position and rough share of the frame. "
    "Unmarked white areas are flexible background and negative space. Do "
    "not reproduce the diagram's black outlines or white background in the "
    "output - the finished image must contain no text and no outlines. "
    "Create a complete new, fully colored image; choose every color from "
    "each subject's natural appearance and from the rest of this prompt."
)

LEGEND_F = (
    LEGEND_E
    + " The finished image must be a full-bleed scene that covers the entire "
    "canvas edge to edge with continuous scenery - no plain white areas, no "
    "isolated shapes floating on a blank background."
)

CONDITIONS = [
    ("A_color_current", "sketch.png", LEGEND_A),
    ("B_color_hard", "sketch.png", LEGEND_B),
    ("C_diagram", "diagram.png", LEGEND_C),
]

# Phase 2: only the endpoints that followed the labeled diagram's layout but
# leaked its printed label text into the output.
PHASE2_CONDITIONS = [
    ("D_diagram_numbers", "diagram_numbers.png", LEGEND_D),
    ("E_diagram_plain", "diagram_plain.png", LEGEND_E),
]
PHASE2_GENERATORS = [
    "google", "googlepro", "bfl", "bfl-flux2-pro", "bfl-flux2-flex",
    "bfl-kontext-pro", "bfl-kontext-max",
]

# Phase 3: plain diagram + position legend + explicit full-bleed clause, on
# the endpoints E never covered plus the two E failure modes (white-void
# flex, outline-leaking kontext-pro).
PHASE3_CONDITIONS = [
    ("F_plain_fullbleed", "diagram_plain.png", LEGEND_F),
]
PHASE3_GENERATORS = [
    "gpt2", "grok-web", "grok-web-chat", "bfl-flux2-max",
    "bfl-flux2-flex", "bfl-kontext-pro",
]

TIMEOUT_S = 40 * 60


def main():
    collect_only = "--collect" in sys.argv
    if "--phase2" in sys.argv:
        conditions, wanted = PHASE2_CONDITIONS, PHASE2_GENERATORS
    elif "--phase3" in sys.argv:
        conditions, wanted = PHASE3_CONDITIONS, PHASE3_GENERATORS
    else:
        conditions, wanted = CONDITIONS, GENERATORS
    os.makedirs(os.path.join(OUT, "results"), exist_ok=True)
    session = requests.Session()

    config = session.get(f"{BASE}/api/config", timeout=10).json()
    client_instance = config["clientInstanceId"]
    catalog = {g["key"]: g for g in config["generators"]}
    gens = []
    for key in wanted:
        g = catalog.get(key)
        if g is None or not g.get("available"):
            problem = (g or {}).get("problem", "not in catalog")
            print(f"SKIP {key}: {problem}")
        else:
            gens.append(key)
    print(f"probing {len(gens)} endpoints: {', '.join(gens)}")

    manifest_path = os.path.join(OUT, "manifest.json")
    if os.path.exists(manifest_path):
        manifest = json.load(open(manifest_path))
    else:
        manifest = {
            "base_prompt": BASE_PROMPT,
            "conditions": {},
            "generators": gens,
            "jobs": {},
            "results": {},  # "<condition>/<gen>" -> record
        }

    if collect_only:
        jobs = dict(manifest["jobs"])
        print(f"collect mode: resuming jobs {list(jobs)}")
    else:
        jobs = dict(manifest["jobs"])
        manifest["conditions"].update(
            {n: {"image": f, "legend": l} for n, f, l in conditions})
        for name, image_file, legend in conditions:
            prompt = BASE_PROMPT + "\n\n" + legend
            with open(os.path.join(OUT, image_file), "rb") as fh:
                resp = session.post(
                    f"{BASE}/api/jobs",
                    data={
                        "prompt": prompt,
                        "user": USER,
                        "generators": ",".join(gens),
                        "shape": "auto",
                        "detail": "standard",
                        "quality": "medium",
                        "moderation": "low",
                        "n": "1",
                    },
                    files={"images": (image_file, fh, "image/png")},
                    timeout=60,
                )
            body = resp.json()
            if resp.status_code != 200 or "id" not in body:
                raise SystemExit(f"job submit failed for {name}: {resp.status_code} {body}")
            jobs[body["id"]] = name
            print(f"submitted {name}: job {body['id']}")
        manifest["jobs"] = jobs

    cursor = 0
    done_jobs = set()
    started = time.time()
    while len(done_jobs) < len(jobs) and time.time() - started < TIMEOUT_S:
        try:
            poll = session.get(
                f"{BASE}/api/events/poll",
                params={"cursor": cursor, "clientInstance": client_instance},
                timeout=30,
            ).json()
        except requests.RequestException as ex:
            print(f"poll error: {ex}; retrying")
            time.sleep(5)
            continue
        cursor = poll.get("cursor", cursor)
        for envelope in poll.get("envelopes", []):
            job_id = envelope.get("jobId") or envelope.get("job")
            if job_id not in jobs:
                continue
            event = envelope.get("event") or envelope
            etype = event.get("type")
            cond = jobs[job_id]
            if etype == "gen-result":
                gen = event.get("gen") or event.get("generator")
                key = f"{cond}/{gen}"
                if key in manifest["results"]:
                    continue
                record = {
                    "ok": bool(event.get("ok")),
                    "error": event.get("error"),
                    "cost": event.get("cost"),
                    "size": event.get("size"),
                    "label": event.get("label"),
                    "raw": event,
                }
                if record["ok"]:
                    images = event.get("images") or []
                    url = None
                    if images:
                        first = images[0]
                        url = first.get("url") if isinstance(first, dict) else first
                    if url:
                        full = url if url.startswith("http") else BASE + url
                        fname = f"results/{cond}__{gen}.png"
                        try:
                            img_resp = session.get(full, timeout=120)
                            img_resp.raise_for_status()
                            with open(os.path.join(OUT, fname), "wb") as fh:
                                fh.write(img_resp.content)
                            record["file"] = fname
                            record["url"] = url
                        except requests.RequestException as ex:
                            record["ok"] = False
                            record["error"] = f"download failed: {ex}"
                    else:
                        record["ok"] = False
                        record["error"] = "gen-result carried no image url"
                manifest["results"][key] = record
                status = "ok" if record["ok"] else f"FAIL: {record['error']}"
                print(f"[{time.strftime('%H:%M:%S')}] {key}: {status}")
            elif etype == "job-done":
                done_jobs.add(job_id)
                print(f"[{time.strftime('%H:%M:%S')}] job {job_id} ({cond}) done "
                      f"({len(done_jobs)}/{len(jobs)})")
        with open(os.path.join(OUT, "manifest.json"), "w") as fh:
            json.dump(manifest, fh, indent=1)
        if len(done_jobs) < len(jobs):
            time.sleep(3)

    missing = [f"{cond}" for jid, cond in jobs.items() if jid not in done_jobs]
    if missing:
        print(f"TIMEOUT: conditions not finished: {missing}")
    with open(os.path.join(OUT, "manifest.json"), "w") as fh:
        json.dump(manifest, fh, indent=1)
    print(f"manifest written to {OUT}/manifest.json")


if __name__ == "__main__":
    main()
