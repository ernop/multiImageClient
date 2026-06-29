#!/usr/bin/env python3
"""
Evaluate an already-generated sample image set with the describe endpoints.

This is for workflows like:
1. Generate a 15-prompt provider sample in the C# app.
2. Choose one source provider, e.g. gpt-image-2.
3. Send those exact source images to every describe endpoint.
4. Render visual report cards that make omissions easy to inspect.
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
import sys
from dataclasses import asdict
from pathlib import Path
from types import SimpleNamespace
from typing import Any

from PIL import Image, ImageDraw, ImageFont

import describe_eval


PromptPattern = re.compile(
    r"^A natural full-body street photo of two (?P<age>\d+)-year-old "
    r"(?P<ethnicity>.+?) (?P<gender_plural>men|women) walking together",
    re.IGNORECASE,
)


def slug(s: str) -> str:
    return re.sub(r"[^a-z0-9]+", "_", s.lower()).strip("_")


def load_font(size: int, bold: bool = False) -> ImageFont.FreeTypeFont | ImageFont.ImageFont:
    candidates = [
        "arialbd.ttf" if bold else "arial.ttf",
        "C:/Windows/Fonts/arialbd.ttf" if bold else "C:/Windows/Fonts/arial.ttf",
        "C:/Windows/Fonts/segoeuib.ttf" if bold else "C:/Windows/Fonts/segoeui.ttf",
    ]
    for candidate in candidates:
        try:
            return ImageFont.truetype(candidate, size)
        except OSError:
            pass
    return ImageFont.load_default()


def wrap_text(draw: ImageDraw.ImageDraw, text: str, font: ImageFont.ImageFont, width: int) -> list[str]:
    lines: list[str] = []
    for paragraph in text.splitlines() or [""]:
        words = paragraph.split()
        if not words:
            lines.append("")
            continue
        current = words[0]
        for word in words[1:]:
            candidate = current + " " + word
            if draw.textbbox((0, 0), candidate, font=font)[2] <= width:
                current = candidate
            else:
                lines.append(current)
                current = word
        lines.append(current)
    return lines


def draw_wrapped(
    draw: ImageDraw.ImageDraw,
    xy: tuple[int, int],
    text: str,
    font: ImageFont.ImageFont,
    fill: str,
    width: int,
    max_lines: int | None = None,
    line_gap: int = 4,
) -> int:
    x, y = xy
    if len(text) > 1800:
        text = text[:1800].rstrip() + "..."
    lines = wrap_text(draw, text, font, width)
    if max_lines is not None and len(lines) > max_lines:
        lines = lines[:max_lines]
        lines[-1] = lines[-1].rstrip(".") + "..."
    line_height = draw.textbbox((0, 0), "Ag", font=font)[3] + line_gap
    for line in lines:
        draw.text((x, y), line, font=font, fill=fill)
        y += line_height
    return y


def parse_prompt(line: str) -> tuple[describe_eval.Case, str]:
    prompt = re.sub(r"^\s*\d+\.\s*", "", line).strip()
    match = PromptPattern.search(prompt)
    if not match:
        raise ValueError(f"Could not parse prompt: {prompt}")

    age = match.group("age")
    ethnicity_label = match.group("ethnicity")
    gender_plural = match.group("gender_plural").lower()
    gender = "man" if gender_plural == "men" else "woman"
    ethnicity_key = slug(ethnicity_label)
    case_id = f"{ethnicity_key}__{gender_plural}__age_{age}__gpt_image_2_sample"
    case = describe_eval.Case(
        case_id=case_id,
        ethnicity_key=ethnicity_key,
        ethnicity_label=ethnicity_label,
        gender=gender,
        gender_plural=gender_plural,
        age=age,
        prompt=prompt,
    )
    return case, prompt


def file_matches_case(path: Path, case: describe_eval.Case) -> bool:
    name = path.name.lower()
    tokenized_name = "_" + re.sub(r"[^a-z0-9]+", "_", name) + "_"
    ethnicity_tokens = case.ethnicity_label.lower().split()
    return (
        f"{case.age}-year-old".lower() in name
        and all(token in name for token in ethnicity_tokens)
        and f"_{case.gender_plural}_" in tokenized_name
        and "_raw" in name
    )


def import_sample(prompt_file: Path, image_glob: str, run_dir: Path) -> list[describe_eval.Case]:
    images_dir = run_dir / "images"
    images_dir.mkdir(parents=True, exist_ok=True)

    prompts = [line for line in prompt_file.read_text(encoding="utf-8").splitlines() if line.strip()]
    parsed = [parse_prompt(line) for line in prompts]
    cases = [case for case, _ in parsed]
    source_images = sorted(Path().glob(image_glob))
    if not source_images:
        raise FileNotFoundError(f"No source images matched: {image_glob}")

    manifest_path = run_dir / "manifest.jsonl"
    source_manifest_path = run_dir / "source_images.jsonl"
    with manifest_path.open("w", encoding="utf-8") as manifest, source_manifest_path.open("w", encoding="utf-8") as source_manifest:
        for case, prompt in parsed:
            matches = [path for path in source_images if file_matches_case(path, case)]
            if len(matches) != 1:
                raise RuntimeError(
                    f"Expected one source image for {case.case_id}, found {len(matches)}: "
                    + ", ".join(str(m) for m in matches[:5])
                )
            source_path = matches[0]
            dest = images_dir / f"{case.case_id}.png"
            shutil.copyfile(source_path, dest)

            manifest.write(json.dumps(asdict(case), ensure_ascii=False) + "\n")
            source_manifest.write(json.dumps({
                "case_id": case.case_id,
                "source_provider": "gpt-image-2",
                "source_prompt": prompt,
                "source_image_path": str(source_path),
                "imported_image_path": str(dest),
            }, ensure_ascii=False) + "\n")

    return cases


def read_jsonl(path: Path) -> list[dict[str, Any]]:
    if not path.exists():
        return []
    with path.open("r", encoding="utf-8") as f:
        return [json.loads(line) for line in f if line.strip()]


def make_describe_args(args: argparse.Namespace) -> SimpleNamespace:
    defaults = describe_eval.parse_args([])
    for key, value in vars(args).items():
        setattr(defaults, key.replace("-", "_"), value)
    defaults.out = str(args.out)
    defaults.endpoints = args.endpoints
    defaults.describe_prompt = args.describe_prompt
    defaults.max_tokens = args.max_tokens
    defaults.score_mode = args.score_mode
    defaults.score_judge_model = args.score_judge_model
    defaults.score_max_tokens = args.score_max_tokens
    defaults.detail = args.detail
    defaults.timeout = args.timeout
    defaults.sleep = args.sleep
    defaults.settings = args.settings
    return defaults


ScoreColumns = [
    ("people_count", "People"),
    ("gender", "Sex"),
    ("age", "Age"),
    ("ethnicity", "Ethnicity"),
    ("clothing", "Clothing"),
    ("style_body", "Style/body"),
]


def expected_people_label(case: dict[str, Any]) -> str:
    return f"two {case['age']}-year-old {case['ethnicity_label']} {case['gender_plural']}"


def score_for_record(record: dict[str, Any]) -> dict[str, Any]:
    if record.get("error"):
        return {}
    score = record.get("score") or {}
    if score:
        return score
    case = describe_eval.Case(**record["case"])
    return describe_eval.score_description_rules(case, record.get("text", ""))


def score_items(score: dict[str, Any]) -> list[tuple[str, float]]:
    return [
        (label, float(score.get(key, 0) or 0))
        for key, label in ScoreColumns
    ]


def score_text(value: float) -> tuple[str, str, str]:
    if value >= 1.0:
        return "OK", "#DDF5DD", "#4C9A4C"
    if value > 0:
        return "HALF", "#FFF3C4", "#B8860B"
    return "NO", "#FFE1E1", "#C44"


def aggregate_score_text(value: float) -> tuple[str, str, str]:
    if value >= 0.8:
        return "HIGH", "#DDF5DD", "#4C9A4C"
    if value >= 0.5:
        return "MIXED", "#FFF3C4", "#B8860B"
    return "LOW", "#FFE1E1", "#C44"


def draw_score_badges(draw: ImageDraw.ImageDraw, x: int, y: int, score: dict[str, Any], font: ImageFont.ImageFont) -> int:
    for label, value in score_items(score):
        status, color, outline = score_text(value)
        text = f"{status} {label}"
        bbox = draw.textbbox((0, 0), text, font=font)
        w = bbox[2] - bbox[0] + 18
        h = bbox[3] - bbox[1] + 12
        draw.rounded_rectangle((x, y, x + w, y + h), radius=8, fill=color, outline=outline, width=2)
        draw.text((x + 9, y + 5), text, font=font, fill="#111")
        x += w + 8
    return y + 34


def draw_table_cell(
    draw: ImageDraw.ImageDraw,
    box: tuple[int, int, int, int],
    text: str,
    font: ImageFont.ImageFont,
    fill: str = "#111",
    bg: str = "#FFFFFF",
    outline: str = "#DDD",
) -> None:
    x0, y0, x1, y1 = box
    draw.rectangle(box, fill=bg, outline=outline)
    bbox = draw.textbbox((0, 0), text, font=font)
    tx = x0 + max(8, (x1 - x0 - (bbox[2] - bbox[0])) // 2)
    ty = y0 + max(6, (y1 - y0 - (bbox[3] - bbox[1])) // 2)
    draw.text((tx, ty), text, font=font, fill=fill)


def draw_score_table_cell(
    draw: ImageDraw.ImageDraw,
    box: tuple[int, int, int, int],
    status: str,
    extracted: str,
    font: ImageFont.ImageFont,
    small_font: ImageFont.ImageFont,
    bg: str,
    outline: str,
) -> None:
    x0, y0, x1, y1 = box
    draw.rectangle(box, fill=bg, outline=outline)
    draw.text((x0 + 8, y0 + 16), status, font=font, fill="#111")
    if extracted:
        snippet = extracted.replace("\n", " ").strip()
        if len(snippet) > 34:
            snippet = snippet[:31].rstrip() + "..."
        draw_wrapped(draw, (x0 + 8, y0 + 46), snippet, small_font, "#111", x1 - x0 - 16, max_lines=2, line_gap=2)


def category_totals(scores: list[dict[str, Any]]) -> dict[str, tuple[float, float]]:
    return {
        key: (sum(float(score.get(key, 0) or 0) for score in scores), float(len(scores)))
        for key, _ in ScoreColumns
    }


def draw_endpoint_aggregate_table(
    draw: ImageDraw.ImageDraw,
    x0: int,
    y0: int,
    width: int,
    scores: list[dict[str, Any]],
    table_font: ImageFont.ImageFont,
    small_font: ImageFont.ImageFont,
) -> int:
    label_w = 150
    category_w = (width - label_w) // len(ScoreColumns)
    row_h = 58
    totals = category_totals(scores)

    draw_table_cell(draw, (x0, y0, x0 + label_w, y0 + row_h), "Aggregate", table_font, bg="#F0E7C8", outline="#B8860B")
    x = x0 + label_w
    for key, label in ScoreColumns:
        earned, possible = totals[key]
        percent = 0 if possible == 0 else earned / possible
        status, bg, outline = aggregate_score_text(percent)
        text = f"{label}\n{earned:g}/{possible:g}"
        draw.rectangle((x, y0, x + category_w, y0 + row_h), fill=bg, outline=outline)
        draw_wrapped(draw, (x + 8, y0 + 7), text, table_font, "#111", category_w - 16, max_lines=2, line_gap=2)
        draw_wrapped(draw, (x + 8, y0 + 38), status, small_font, "#111", category_w - 16, max_lines=1, line_gap=2)
        x += category_w

    return y0 + row_h


def render_by_image(run_dir: Path, describe_prompt: str) -> None:
    records = read_jsonl(run_dir / "describe_results.jsonl")
    sources = {row["case_id"]: row for row in read_jsonl(run_dir / "source_images.jsonl")}
    by_case: dict[str, list[dict[str, Any]]] = {}
    for record in records:
        by_case.setdefault(record["case"]["case_id"], []).append(record)

    out_dir = run_dir / "reports" / "by_image"
    out_dir.mkdir(parents=True, exist_ok=True)
    title_font = load_font(34, bold=True)
    body_font = load_font(20)
    small_font = load_font(16)
    table_font = load_font(16, bold=True)
    cell_small_font = load_font(12)

    for case_id, case_records in by_case.items():
        source = sources[case_id]
        case = case_records[0]["case"]
        image = Image.open(source["imported_image_path"]).convert("RGB")
        image.thumbnail((420, 420), Image.Resampling.LANCZOS)

        width = 1900
        row_h = 150
        height = 680 + row_h * len(case_records)
        canvas = Image.new("RGB", (width, height), "#FAFAF6")
        draw = ImageDraw.Draw(canvas)
        draw.text((36, 28), f"Describe Test: {expected_people_label(case)}", font=title_font, fill="#111")
        draw.text((36, 76), "Source image: gpt-image-2. Rows are describe API companies; columns are the evaluation categories.", font=body_font, fill="#222")

        canvas.paste(image, (36, 120))
        meta_x = 500
        y = 120
        y = draw_wrapped(draw, (meta_x, y), f"Source provider: {source['source_provider']}", body_font, "#111", 1200, max_lines=1)
        y = draw_wrapped(draw, (meta_x, y + 8), f"Original image prompt: {source['source_prompt']}", small_font, "#222", 1200, max_lines=5)
        y = draw_wrapped(draw, (meta_x, y + 8), f"Describe prompt: {describe_prompt}", small_font, "#222", 1200, max_lines=5)

        x0 = 36
        y0 = 570
        endpoint_w = 145
        score_w = 92
        category_w = 118
        text_x = x0 + endpoint_w + score_w + category_w * len(ScoreColumns)
        text_w = width - text_x - 36

        draw_table_cell(draw, (x0, y0, x0 + endpoint_w, y0 + 46), "Endpoint", table_font, bg="#F0E7C8", outline="#B8860B")
        draw_table_cell(draw, (x0 + endpoint_w, y0, x0 + endpoint_w + score_w, y0 + 46), "Total", table_font, bg="#F0E7C8", outline="#B8860B")
        x = x0 + endpoint_w + score_w
        for _, label in ScoreColumns:
            draw_table_cell(draw, (x, y0, x + category_w, y0 + 46), label, table_font, bg="#F0E7C8", outline="#B8860B")
            x += category_w
        draw_table_cell(draw, (text_x, y0, width - 36, y0 + 46), "What the endpoint said", table_font, bg="#F0E7C8", outline="#B8860B")
        y0 += 46

        for record in sorted(case_records, key=lambda r: r["endpoint"]):
            score = score_for_record(record)
            draw_table_cell(draw, (x0, y0, x0 + endpoint_w, y0 + row_h), record["endpoint"], table_font)
            total = f"{score.get('score', 0):g}/{score.get('possible', 0):g}" if score else "ERR"
            draw_table_cell(draw, (x0 + endpoint_w, y0, x0 + endpoint_w + score_w, y0 + row_h), total, table_font)
            x = x0 + endpoint_w + score_w
            for key, _ in ScoreColumns:
                value = float(score.get(key, 0) or 0)
                status, bg, outline = score_text(value)
                category = (score.get("categories") or {}).get(key) or {}
                extracted = str(category.get("extracted", "") or "")
                draw_score_table_cell(draw, (x, y0, x + category_w, y0 + row_h), status, extracted, table_font, cell_small_font, bg, outline)
                x += category_w
            if record.get("error"):
                text = "ERROR: " + record["error"]
                fill = "#A00"
            else:
                text = record.get("text", "")
                fill = "#222"
            draw.rectangle((text_x, y0, width - 36, y0 + row_h), fill="#FFFFFF", outline="#DDD")
            draw_wrapped(draw, (text_x + 12, y0 + 10), text, small_font, fill, text_w - 24, max_lines=6)
            y0 += row_h

        canvas.save(out_dir / f"{case_id}.png", compress_level=1)
        print(f"[report by-image] {case_id}", flush=True)


def render_by_endpoint(run_dir: Path, describe_prompt: str) -> None:
    records = read_jsonl(run_dir / "describe_results.jsonl")
    sources = {row["case_id"]: row for row in read_jsonl(run_dir / "source_images.jsonl")}
    by_endpoint: dict[str, list[dict[str, Any]]] = {}
    for record in records:
        by_endpoint.setdefault(record["endpoint"], []).append(record)

    out_dir = run_dir / "reports" / "by_endpoint"
    out_dir.mkdir(parents=True, exist_ok=True)
    title_font = load_font(30, bold=True)
    body_font = load_font(18)
    small_font = load_font(14)
    badge_font = load_font(13, bold=True)
    table_font = load_font(15, bold=True)

    for endpoint, endpoint_records in sorted(by_endpoint.items()):
        width = 1700
        row_h = 255
        header_h = 270
        height = header_h + row_h * len(endpoint_records)
        canvas = Image.new("RGB", (width, height), "#FAFAF6")
        draw = ImageDraw.Draw(canvas)
        totals = [score_for_record(r) for r in endpoint_records]
        total = sum(score.get("score", 0) for score in totals)
        possible = sum(score.get("possible", 0) for score in totals)
        draw.text((34, 24), f"{endpoint} describe quality summary", font=title_font, fill="#111")
        draw.text((34, 66), f"Source images: gpt-image-2. Prompt sent to describer: {describe_prompt}", font=small_font, fill="#111")
        draw.text((34, 104), f"Matched expected fields: {total:g}/{possible:g}", font=body_font, fill="#111")
        draw_wrapped(
            draw,
            (34, 134),
            "Endpoint aggregate by category: this shows the endpoint's overall ability to distinguish the requested axes before the per-image rows.",
            small_font,
            "#111",
            width - 68,
            max_lines=2,
        )
        draw_endpoint_aggregate_table(draw, 34, 180, width - 68, totals, table_font, small_font)

        y = header_h
        for record in endpoint_records:
            case_id = record["case"]["case_id"]
            source = sources[case_id]
            thumb = Image.open(source["imported_image_path"]).convert("RGB")
            thumb.thumbnail((190, 190), Image.Resampling.LANCZOS)
            draw.rounded_rectangle((34, y, width - 34, y + row_h - 18), radius=12, fill="#FFFFFF", outline="#DDD")
            canvas.paste(thumb, (54, y + 24))
            x = 270
            draw_wrapped(draw, (x, y + 18), expected_people_label(record["case"]), body_font, "#111", 650, max_lines=1)
            draw_wrapped(draw, (x, y + 46), source["source_prompt"], small_font, "#222", 650, max_lines=3)
            draw_score_badges(draw, x, y + 118, score_for_record(record), badge_font)
            text = "ERROR: " + record["error"] if record.get("error") else record.get("text", "")
            draw_wrapped(draw, (930, y + 18), text, small_font, "#222", 710, max_lines=9)
            y += row_h

        canvas.save(out_dir / f"{endpoint}.png", compress_level=1)
        print(f"[report by-endpoint] {endpoint}", flush=True)


def render_reports(run_dir: Path, describe_prompt: str) -> None:
    render_by_image(run_dir, describe_prompt)
    render_by_endpoint(run_dir, describe_prompt)


def rescore_results(source_results: Path, settings: dict[str, Any], args: argparse.Namespace, run_dir: Path) -> None:
    source_records = read_jsonl(source_results)
    if not source_records:
        raise FileNotFoundError(f"No describe results to rescore: {source_results}")

    results_path = run_dir / "describe_results.jsonl"
    summary_path = run_dir / "describe_summary.csv"
    rows: list[dict[str, Any]] = []
    with results_path.open("w", encoding="utf-8") as results:
        for index, record in enumerate(source_records, 1):
            endpoint = record.get("endpoint", "")
            case = describe_eval.Case(**record["case"])
            print(f"[rescore {index}/{len(source_records)}] {endpoint}: {case.case_id}", flush=True)
            if record.get("error"):
                rescored = {**record, "score": {}}
            else:
                try:
                    score = describe_eval.score_description(case, record.get("text", ""), settings, args)
                    rescored = {**record, "score": score, "error": ""}
                except Exception as ex:
                    rescored = {**record, "score": {}, "error": f"score failed: {ex}"}
            results.write(json.dumps(rescored, ensure_ascii=False) + "\n")
            results.flush()
            rows.append(describe_eval.flatten_result(rescored))
    describe_eval.write_summary(summary_path, rows)


def parse_args(argv: list[str]) -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Evaluate generated gpt-image-2 sample images with describe endpoints.")
    parser.add_argument("--prompt-file", required=True, type=Path, help="Numbered prompt list from provider sample showcase.")
    parser.add_argument("--image-glob", required=True, help="Glob for raw source images.")
    parser.add_argument("--out", required=True, type=Path, help="Output folder for imported fixtures, results, and report images.")
    parser.add_argument("--settings", default="MultiImageClient/settings.json")
    parser.add_argument("--endpoints", default="openai,grok,gemini,claude,ideogram")
    parser.add_argument("--describe-prompt", default=describe_eval.DEFAULT_DESCRIBE_PROMPT)
    parser.add_argument("--describe", action="store_true", help="Call paid describe endpoints.")
    parser.add_argument("--rescore-from", type=Path, help="Existing describe_results.jsonl to rescore without calling vision describers.")
    parser.add_argument("--report", action="store_true", help="Render report PNGs from describe_results.jsonl.")
    parser.add_argument("--overwrite-results", action="store_true", help="Delete previous describe outputs before running.")
    parser.add_argument("--sleep", type=float, default=0.5)
    parser.add_argument("--timeout", type=int, default=240)
    parser.add_argument("--max-tokens", type=int, default=1200)
    parser.add_argument("--score-mode", choices=["llm", "rules"], default="llm")
    parser.add_argument("--score-judge-model", default="gpt-4.1-mini")
    parser.add_argument("--score-max-tokens", type=int, default=900)
    parser.add_argument("--detail", default="high")
    parser.add_argument("--openai-vision-model", default="gpt-4.1")
    parser.add_argument("--grok-vision-model", default="grok-4.3")
    parser.add_argument("--gemini-vision-model", default="gemini-2.5-pro")
    parser.add_argument("--claude-vision-model", default="claude-sonnet-4-5")
    parser.add_argument("--ideogram-describe-model", default="")
    return parser.parse_args(argv)


def main(argv: list[str]) -> int:
    args = parse_args(argv)
    args.out.mkdir(parents=True, exist_ok=True)
    cases = import_sample(args.prompt_file, args.image_glob, args.out)
    print(f"imported cases: {len(cases)}", flush=True)
    print(f"out: {args.out}", flush=True)

    describe_args = make_describe_args(args)
    if args.overwrite_results:
        for path in [args.out / "describe_results.jsonl", args.out / "describe_summary.csv"]:
            if path.exists():
                path.unlink()

    if args.describe:
        settings = describe_eval.load_settings(Path(args.settings))
        describe_eval.describe_images(cases, settings, describe_args, args.out / "images", args.out)
    if args.rescore_from:
        settings = describe_eval.load_settings(Path(args.settings))
        rescore_results(args.rescore_from, settings, describe_args, args.out)
    if args.report:
        render_reports(args.out, args.describe_prompt)
        print(f"reports: {args.out / 'reports'}")
    if not args.describe and not args.rescore_from and not args.report:
        print("No describe calls, rescoring, or reports requested. Add --describe, --rescore-from, and/or --report.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
