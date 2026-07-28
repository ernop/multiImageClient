"""Generate deterministic single and ordered-pair image perturbations."""

from __future__ import annotations

import argparse
from concurrent.futures import ProcessPoolExecutor, as_completed
from dataclasses import dataclass
from datetime import datetime, timezone
import hashlib
import json
import os
from pathlib import Path
import time
from typing import Any

from PIL import Image

import image_perturbations as perturb


RunnerVersion = 1


@dataclass(frozen=True)
class TransformSpec:
    number: int
    slug: str
    function_name: str
    arguments: dict[str, Any]
    manifest_arguments: dict[str, Any] | None = None

    def apply(self, image: Image.Image) -> Image.Image:
        function = getattr(perturb, self.function_name)
        return function(image, **self.arguments)

    @property
    def recorded_arguments(self) -> dict[str, Any]:
        return self.arguments if self.manifest_arguments is None else self.manifest_arguments


Transforms = (
    TransformSpec(
        1,
        "asymmetric-edge-crop",
        "asymmetric_edge_crop",
        {"left": 0.0, "top": 0.0, "right": 0.01, "bottom": 0.01},
    ),
    TransformSpec(
        2,
        "translate-and-pad",
        "translate_and_pad",
        {"x_fraction": 0.02, "y_fraction": 0.02},
    ),
    TransformSpec(
        3,
        "micro-rotate",
        "micro_rotate",
        {"degrees": 0.3, "clockwise": True, "expand": True},
    ),
    TransformSpec(
        4,
        "rotate-crop-rescale",
        "rotate_crop_rescale",
        {"degrees": 1.0},
    ),
    TransformSpec(5, "affine-shear", "affine_shear", {"x_degrees": 2.0}),
    TransformSpec(
        6,
        "anisotropic-scale",
        "anisotropic_scale",
        {"width_scale": 0.98, "height_scale": 1.02},
    ),
    TransformSpec(
        7,
        "perspective-keystone",
        "perspective_keystone",
        {"top_inset_fraction": 0.04},
    ),
    TransformSpec(
        8,
        "radial-lens-distortion",
        "radial_lens_distortion",
        {"strength": 0.08, "mesh_size": 18},
    ),
    TransformSpec(
        9,
        "elastic-deformation",
        "elastic_deformation",
        {
            "amplitude_fraction": 0.012,
            "mesh_columns": 10,
            "mesh_rows": 10,
            "smoothing_passes": 3,
            "seed": 0,
        },
    ),
    TransformSpec(
        10,
        "wave-displacement",
        "wave_displacement",
        {
            "amplitude_fraction": 0.01,
            "cycles": 2.0,
            "angle_degrees": 0.0,
            "mesh_size": 24,
        },
    ),
    TransformSpec(
        11,
        "mesh-warp",
        "mesh_warp",
        {
            "amplitude_fraction": 0.015,
            "mesh_columns": 6,
            "mesh_rows": 6,
            "seed": 0,
        },
    ),
    TransformSpec(
        12,
        "localized-swirl",
        "localized_swirl",
        {
            "strength_degrees": 6.0,
            "radius_fraction": 0.3,
            "mesh_size": 28,
            "seed": 0,
        },
    ),
    TransformSpec(
        13,
        "random-patch-displacement",
        "random_patch_displacement",
        {
            "patch_count": 3,
            "patch_fraction": 0.08,
            "maximum_offset_fraction": 0.06,
            "seed": 0,
        },
    ),
    TransformSpec(
        14,
        "grid-cell-permutation",
        "grid_cell_permutation",
        {"columns": 5, "rows": 5, "swap_count": 2, "seed": 0},
    ),
    TransformSpec(
        15,
        "content-aware-seam-compress",
        "content_aware_seam_compress",
        {"width_fraction": 0.02, "restore_size": True},
    ),
    TransformSpec(
        16,
        "downsample-then-upsample",
        "downsample_then_upsample",
        {"scale": 0.5},
        {
            "scale": 0.5,
            "down_filter": "LANCZOS",
            "up_filter": "BICUBIC",
        },
    ),
    TransformSpec(
        17,
        "nearest-neighbor-resampling",
        "nearest_neighbor_resampling",
        {"scale": 0.5, "restore_size": True},
    ),
    TransformSpec(
        18,
        "mixed-filter-resize-chain",
        "mixed_filter_resize_chain",
        {"scales": (0.92, 1.07, 1.0)},
        {
            "scales": [0.92, 1.07, 1.0],
            "filters": ["BILINEAR", "BICUBIC", "LANCZOS"],
        },
    ),
    TransformSpec(
        19,
        "subpixel-translation",
        "subpixel_translation",
        {"x_pixels": 0.35, "y_pixels": 0.35},
    ),
    TransformSpec(
        20,
        "jpeg-recompression",
        "jpeg_recompression",
        {"quality": 82, "subsampling": 2},
    ),
    TransformSpec(
        21,
        "webp-recompression",
        "webp_recompression",
        {"quality": 80, "method": 4},
    ),
    TransformSpec(
        22,
        "palette-quantization",
        "palette_quantization",
        {"colors": 64},
    ),
    TransformSpec(23, "dithering", "apply_dithering", {"colors": 32}),
    TransformSpec(24, "gaussian-blur", "gaussian_blur", {"radius": 1.2}),
    TransformSpec(25, "median-filter", "median_filter", {"size": 3}),
    TransformSpec(
        26,
        "motion-blur",
        "motion_blur",
        {"length": 5, "angle_degrees": 17.0},
    ),
    TransformSpec(
        27,
        "additive-sensor-noise",
        "additive_sensor_noise",
        {
            "standard_deviation": 6.0,
            "salt_pepper_probability": 0.0,
            "seed": 0,
        },
    ),
    TransformSpec(
        28,
        "gamma-contrast-remap",
        "gamma_contrast_remap",
        {"gamma": 1.08, "contrast": 1.05},
    ),
    TransformSpec(
        29,
        "chroma-subsample-channel-shift",
        "chroma_subsample_and_channel_shift",
        {
            "chroma_scale": 0.5,
            "red_shift": (0.5, 0.0),
            "blue_shift": (-0.5, 0.0),
        },
    ),
    TransformSpec(
        30,
        "random-non-targeted-cutout",
        "random_non_targeted_cutout",
        {
            "count": 3,
            "minimum_fraction": 0.025,
            "maximum_fraction": 0.08,
            "shape": "rectangle",
            "seed": 0,
        },
    ),
)


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


def _record_for_image(
    *,
    kind: str,
    pipeline: list[TransformSpec],
    saved_path: Path,
    relative_path: Path,
    image: Image.Image,
    elapsed_seconds: float,
) -> dict[str, Any]:
    return {
        "status": "success",
        "kind": kind,
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
        "relative_path": relative_path.as_posix(),
        "width": image.width,
        "height": image.height,
        "mode": image.mode,
        "sha256": _sha256_file(saved_path),
        "elapsed_seconds": round(elapsed_seconds, 6),
    }


def _single_filename(spec: TransformSpec) -> str:
    return f"{spec.number:02d}__{spec.slug}.png"


def _pair_filename(inner: TransformSpec, outer: TransformSpec) -> str:
    return (
        f"inner-{inner.number:02d}__{inner.slug}"
        f"__outer-{outer.number:02d}__{outer.slug}.png"
    )


def _generate_pair_group(
    inner_index: int,
    singles_directory: str,
    pairs_directory: str,
) -> tuple[int, list[dict[str, Any]]]:
    inner = Transforms[inner_index]
    single_path = Path(singles_directory) / _single_filename(inner)
    if not single_path.is_file():
        raise FileNotFoundError(f"Missing exact inner-stage image: {single_path}")

    with Image.open(single_path) as opened:
        inner_image = opened.copy()

    records: list[dict[str, Any]] = []
    pairs_path = Path(pairs_directory)
    for outer in Transforms:
        started = time.perf_counter()
        result = outer.apply(inner_image)
        destination = pairs_path / _pair_filename(inner, outer)
        _save_png_atomic(result, destination)
        elapsed = time.perf_counter() - started
        records.append(
            _record_for_image(
                kind="ordered_pair",
                pipeline=[inner, outer],
                saved_path=destination,
                relative_path=Path("pairs") / destination.name,
                image=result,
                elapsed_seconds=elapsed,
            )
        )
        result.close()

    inner_image.close()
    return inner_index, records


def _append_manifest_record(manifest, record: dict[str, Any]) -> None:
    manifest.write(json.dumps(record, sort_keys=True, separators=(",", ":")) + "\n")
    manifest.flush()
    os.fsync(manifest.fileno())


def generate(source: Path, output: Path, workers: int) -> None:
    if not source.is_file():
        raise FileNotFoundError(f"Source image does not exist: {source}")
    if workers < 1:
        raise ValueError("workers must be positive")
    if output.exists() and any(output.iterdir()):
        raise FileExistsError(f"Output directory must be absent or empty: {output}")

    output.mkdir(parents=True, exist_ok=True)
    singles_directory = output / "singles"
    pairs_directory = output / "pairs"
    singles_directory.mkdir()
    pairs_directory.mkdir()
    manifest_path = output / "manifest.jsonl"
    run_path = output / "run.json"

    source_hash = _sha256_file(source)
    started_at = datetime.now(timezone.utc)
    run_metadata = {
        "runner_version": RunnerVersion,
        "source_path": str(source.resolve()),
        "source_sha256": source_hash,
        "transform_count": len(Transforms),
        "single_count_expected": len(Transforms),
        "ordered_pair_count_expected": len(Transforms) ** 2,
        "total_count_expected": len(Transforms) + len(Transforms) ** 2,
        "workers": workers,
        "started_at_utc": started_at.isoformat(),
        "status": "running",
    }
    run_path.write_text(
        json.dumps(run_metadata, indent=2, sort_keys=True) + "\n",
        encoding="utf-8",
    )

    try:
        with Image.open(source) as opened:
            original = opened.copy()

        with manifest_path.open("x", encoding="utf-8") as manifest:
            for index, spec in enumerate(Transforms, start=1):
                started = time.perf_counter()
                result = spec.apply(original)
                destination = singles_directory / _single_filename(spec)
                _save_png_atomic(result, destination)
                elapsed = time.perf_counter() - started
                record = _record_for_image(
                    kind="single",
                    pipeline=[spec],
                    saved_path=destination,
                    relative_path=Path("singles") / destination.name,
                    image=result,
                    elapsed_seconds=elapsed,
                )
                _append_manifest_record(manifest, record)
                result.close()
                print(
                    f"single {index:02d}/{len(Transforms)}: {destination.name}",
                    flush=True,
                )
        original.close()

        completed_groups = 0
        with manifest_path.open("a", encoding="utf-8") as manifest:
            with ProcessPoolExecutor(max_workers=workers) as executor:
                futures = {
                    executor.submit(
                        _generate_pair_group,
                        inner_index,
                        str(singles_directory),
                        str(pairs_directory),
                    ): inner_index
                    for inner_index in range(len(Transforms))
                }
                for future in as_completed(futures):
                    inner_index, records = future.result()
                    for record in records:
                        _append_manifest_record(manifest, record)
                    completed_groups += 1
                    print(
                        f"pair group {completed_groups:02d}/{len(Transforms)}: "
                        f"inner {Transforms[inner_index].number:02d} "
                        f"({len(records)} images)",
                        flush=True,
                    )

        completed_at = datetime.now(timezone.utc)
        run_metadata.update(
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
            json.dumps(run_metadata, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
    except Exception as exception:
        failed_at = datetime.now(timezone.utc)
        run_metadata.update(
            {
                "status": "failure",
                "failed_at_utc": failed_at.isoformat(),
                "failure_type": type(exception).__name__,
                "failure_message": str(exception),
            }
        )
        run_path.write_text(
            json.dumps(run_metadata, indent=2, sort_keys=True) + "\n",
            encoding="utf-8",
        )
        raise


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(
        description=(
            "Generate 30 single transforms and all 900 ordered two-transform "
            "compositions from one source image."
        )
    )
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    parser.add_argument(
        "--workers",
        type=int,
        default=min(4, os.cpu_count() or 1),
        help="Pair-generation worker processes (default: up to 4).",
    )
    return parser.parse_args()


def main() -> None:
    args = parse_args()
    generate(args.source, args.output, args.workers)


if __name__ == "__main__":
    main()
