"""Generate reproducible random multi-transform image pipelines."""

from __future__ import annotations

import argparse
from datetime import datetime, timezone
import hashlib
import json
from pathlib import Path
import random
import time

from PIL import Image

from generate_variants import Transforms


def _sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def _save_png_atomic(image: Image.Image, destination: Path) -> None:
    temporary = destination.with_name(f".{destination.name}.partial")
    try:
        image.save(temporary, format="PNG")
        temporary.replace(destination)
    except Exception:
        temporary.unlink(missing_ok=True)
        raise


def generate(
    source: Path,
    output: Path,
    *,
    count: int,
    minimum_steps: int,
    maximum_steps: int,
    seed: int,
) -> None:
    if not source.is_file():
        raise FileNotFoundError(f"Source image does not exist: {source}")
    if count < 1:
        raise ValueError("count must be positive")
    if not 1 <= minimum_steps <= maximum_steps <= len(Transforms):
        raise ValueError(
            f"steps must satisfy 1 <= minimum <= maximum <= {len(Transforms)}"
        )
    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"Output directory must be absent or empty: {output}")

    output.mkdir(parents=True, exist_ok=True)
    manifest_path = output / "manifest.jsonl"
    run_path = output / "run.json"
    rng = random.Random(seed)
    started_at = datetime.now(timezone.utc)
    run_record = {
        "status": "running",
        "source_path": str(source.resolve()),
        "source_sha256": _sha256_file(source),
        "image_count_expected": count,
        "minimum_steps": minimum_steps,
        "maximum_steps": maximum_steps,
        "seed": seed,
        "started_at_utc": started_at.isoformat(),
    }
    run_path.write_text(
        json.dumps(run_record, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    try:
        with Image.open(source) as opened:
            original = opened.copy()

        with manifest_path.open("x", encoding="utf-8") as manifest:
            for image_number in range(1, count + 1):
                step_count = rng.randint(minimum_steps, maximum_steps)
                pipeline = rng.sample(Transforms, step_count)
                order = "-".join(f"{spec.number:02d}" for spec in pipeline)
                destination = output / (
                    f"random-{image_number:02d}__steps-{step_count:02d}"
                    f"__order-{order}.png"
                )

                result = original.copy()
                started = time.perf_counter()
                for spec in pipeline:
                    transformed = spec.apply(result)
                    result.close()
                    result = transformed
                _save_png_atomic(result, destination)
                elapsed = time.perf_counter() - started

                record = {
                    "status": "success",
                    "image_number": image_number,
                    "step_count": step_count,
                    "application_order": [spec.slug for spec in pipeline],
                    "transforms": [
                        {
                            "number": spec.number,
                            "slug": spec.slug,
                            "function": spec.function_name,
                            "arguments": spec.recorded_arguments,
                        }
                        for spec in pipeline
                    ],
                    "relative_path": destination.name,
                    "width": result.width,
                    "height": result.height,
                    "mode": result.mode,
                    "sha256": _sha256_file(destination),
                    "elapsed_seconds": round(elapsed, 6),
                }
                manifest.write(
                    json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n"
                )
                manifest.flush()
                result.close()
                print(
                    f"{image_number:02d}/{count}: {step_count:02d} steps "
                    f"[{order}]",
                    flush=True,
                )

        original.close()
        completed_at = datetime.now(timezone.utc)
        run_record.update(
            {
                "status": "success",
                "completed_at_utc": completed_at.isoformat(),
                "elapsed_seconds": round(
                    (completed_at - started_at).total_seconds(),
                    6,
                ),
            }
        )
        run_path.write_text(
            json.dumps(run_record, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
    except Exception as exception:
        run_record.update(
            {
                "status": "failure",
                "failed_at_utc": datetime.now(timezone.utc).isoformat(),
                "failure_type": type(exception).__name__,
                "failure_message": str(exception),
            }
        )
        run_path.write_text(
            json.dumps(run_record, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        raise


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description="Generate random, nonrepeating multi-transform pipelines."
    )
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument("--count", type=int, default=10)
    parser.add_argument("--minimum-steps", type=int, default=5)
    parser.add_argument("--maximum-steps", type=int, default=len(Transforms))
    parser.add_argument("--seed", type=int, default=20260723)
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    generate(
        args.source,
        args.output,
        count=args.count,
        minimum_steps=args.minimum_steps,
        maximum_steps=args.maximum_steps,
        seed=args.seed,
    )


if __name__ == "__main__":
    main()
