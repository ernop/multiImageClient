"""Seedable image perturbations for augmentation and robustness testing.

Every function returns a new Pillow image and leaves its input untouched.
Localized stochastic operations choose their own positions from a seed; this
module intentionally has no watermark detection or targeted concealment logic.
"""

from __future__ import annotations

from io import BytesIO
import math
import random
from collections.abc import Callable, Sequence

from PIL import Image, ImageDraw, ImageEnhance, ImageFilter, ImageOps


FillColor = int | tuple[int, ...]
PointMapper = Callable[[float, float], tuple[float, float]]


__all__ = [
    "additive_sensor_noise",
    "affine_shear",
    "anisotropic_scale",
    "apply_dithering",
    "asymmetric_edge_crop",
    "chroma_subsample_and_channel_shift",
    "content_aware_seam_compress",
    "downsample_then_upsample",
    "elastic_deformation",
    "gamma_contrast_remap",
    "gaussian_blur",
    "grid_cell_permutation",
    "jpeg_recompression",
    "localized_swirl",
    "median_filter",
    "mesh_warp",
    "micro_rotate",
    "mixed_filter_resize_chain",
    "motion_blur",
    "nearest_neighbor_resampling",
    "palette_quantization",
    "perspective_keystone",
    "radial_lens_distortion",
    "random_non_targeted_cutout",
    "random_patch_displacement",
    "rotate_crop_rescale",
    "subpixel_translation",
    "translate_and_pad",
    "wave_displacement",
    "webp_recompression",
]


def _require_image(image: Image.Image) -> None:
    if not isinstance(image, Image.Image):
        raise TypeError("image must be a PIL.Image.Image")
    if image.width < 1 or image.height < 1:
        raise ValueError("image must have nonzero dimensions")


def _require_fraction(name: str, value: float, *, include_one: bool = False) -> None:
    upper_ok = value <= 1 if include_one else value < 1
    if value < 0 or not upper_ok:
        boundary = "[0, 1]" if include_one else "[0, 1)"
        raise ValueError(f"{name} must be in {boundary}")


def _default_fill(image: Image.Image) -> FillColor:
    if image.mode == "RGBA":
        return (255, 255, 255, 0)
    if image.mode == "RGB":
        return (255, 255, 255)
    if image.mode == "LA":
        return (255, 0)
    if image.mode == "L":
        return 255
    return 0


def _scaled_size(image: Image.Image, x_scale: float, y_scale: float) -> tuple[int, int]:
    if x_scale <= 0 or y_scale <= 0:
        raise ValueError("scale factors must be positive")
    return max(1, round(image.width * x_scale)), max(1, round(image.height * y_scale))


def _clamp(value: float, low: float, high: float) -> float:
    return max(low, min(high, value))


def _mesh_transform(
    image: Image.Image,
    columns: int,
    rows: int,
    mapper: PointMapper,
    *,
    fill: FillColor | None = None,
) -> Image.Image:
    if columns < 1 or rows < 1:
        raise ValueError("mesh dimensions must be positive")

    mesh: list[tuple[tuple[int, int, int, int], tuple[float, ...]]] = []
    for row in range(rows):
        y0 = round(row * image.height / rows)
        y1 = round((row + 1) * image.height / rows)
        for column in range(columns):
            x0 = round(column * image.width / columns)
            x1 = round((column + 1) * image.width / columns)
            upper_left = mapper(x0, y0)
            lower_left = mapper(x0, y1)
            lower_right = mapper(x1, y1)
            upper_right = mapper(x1, y0)
            quad = (
                upper_left[0],
                upper_left[1],
                lower_left[0],
                lower_left[1],
                lower_right[0],
                lower_right[1],
                upper_right[0],
                upper_right[1],
            )
            mesh.append(((x0, y0, x1, y1), quad))

    return image.transform(
        image.size,
        Image.Transform.MESH,
        mesh,
        resample=Image.Resampling.BICUBIC,
        fillcolor=_default_fill(image) if fill is None else fill,
    )


def _smooth_displacement_grid(
    columns: int,
    rows: int,
    amplitude: float,
    rng: random.Random,
    passes: int,
) -> list[list[tuple[float, float]]]:
    grid = [
        [
            (
                rng.uniform(-amplitude, amplitude),
                rng.uniform(-amplitude, amplitude),
            )
            for _ in range(columns + 1)
        ]
        for _ in range(rows + 1)
    ]

    for x in range(columns + 1):
        grid[0][x] = (0.0, 0.0)
        grid[rows][x] = (0.0, 0.0)
    for y in range(rows + 1):
        grid[y][0] = (0.0, 0.0)
        grid[y][columns] = (0.0, 0.0)

    for _ in range(passes):
        smoothed: list[list[tuple[float, float]]] = []
        for y in range(rows + 1):
            smoothed_row: list[tuple[float, float]] = []
            for x in range(columns + 1):
                if x in (0, columns) or y in (0, rows):
                    smoothed_row.append((0.0, 0.0))
                    continue
                neighbors = [
                    grid[y][x],
                    grid[y - 1][x],
                    grid[y + 1][x],
                    grid[y][x - 1],
                    grid[y][x + 1],
                ]
                smoothed_row.append(
                    (
                        sum(point[0] for point in neighbors) / len(neighbors),
                        sum(point[1] for point in neighbors) / len(neighbors),
                    )
                )
            smoothed.append(smoothed_row)
        grid = smoothed

    return grid


def _grid_mapper(
    image: Image.Image,
    columns: int,
    rows: int,
    grid: list[list[tuple[float, float]]],
) -> PointMapper:
    def mapper(x: float, y: float) -> tuple[float, float]:
        gx = _clamp(x / image.width * columns, 0, columns)
        gy = _clamp(y / image.height * rows, 0, rows)
        column = min(columns - 1, int(gx))
        row = min(rows - 1, int(gy))
        tx = gx - column
        ty = gy - row

        top_left = grid[row][column]
        top_right = grid[row][column + 1]
        bottom_left = grid[row + 1][column]
        bottom_right = grid[row + 1][column + 1]
        dx = (
            top_left[0] * (1 - tx) * (1 - ty)
            + top_right[0] * tx * (1 - ty)
            + bottom_left[0] * (1 - tx) * ty
            + bottom_right[0] * tx * ty
        )
        dy = (
            top_left[1] * (1 - tx) * (1 - ty)
            + top_right[1] * tx * (1 - ty)
            + bottom_left[1] * (1 - tx) * ty
            + bottom_right[1] * tx * ty
        )
        return x + dx, y + dy

    return mapper


def asymmetric_edge_crop(
    image: Image.Image,
    *,
    left: float = 0.0,
    top: float = 0.0,
    right: float = 0.01,
    bottom: float = 0.01,
) -> Image.Image:
    """Crop independently configured fractions from the four edges."""
    _require_image(image)
    for name, value in (("left", left), ("top", top), ("right", right), ("bottom", bottom)):
        _require_fraction(name, value)
    if left + right >= 1 or top + bottom >= 1:
        raise ValueError("opposing crop fractions must sum to less than 1")

    box = (
        round(image.width * left),
        round(image.height * top),
        image.width - round(image.width * right),
        image.height - round(image.height * bottom),
    )
    return image.crop(box)


def translate_and_pad(
    image: Image.Image,
    *,
    x_fraction: float = 0.02,
    y_fraction: float = 0.02,
    fill: FillColor | None = None,
) -> Image.Image:
    """Translate inside the original canvas and fill newly exposed pixels."""
    _require_image(image)
    dx = image.width * x_fraction
    dy = image.height * y_fraction
    return image.transform(
        image.size,
        Image.Transform.AFFINE,
        (1, 0, -dx, 0, 1, -dy),
        resample=Image.Resampling.BICUBIC,
        fillcolor=_default_fill(image) if fill is None else fill,
    )


def micro_rotate(
    image: Image.Image,
    *,
    degrees: float = 0.3,
    clockwise: bool = True,
    expand: bool = True,
    fill: FillColor | None = None,
) -> Image.Image:
    """Rotate by a small angle using bicubic interpolation."""
    _require_image(image)
    angle = -degrees if clockwise else degrees
    return image.rotate(
        angle,
        resample=Image.Resampling.BICUBIC,
        expand=expand,
        fillcolor=_default_fill(image) if fill is None else fill,
    )


def rotate_crop_rescale(
    image: Image.Image,
    *,
    degrees: float = 1.0,
    fill: FillColor | None = None,
) -> Image.Image:
    """Rotate, center-crop to the original aspect, and restore original size."""
    _require_image(image)
    rotated = image.rotate(
        degrees,
        resample=Image.Resampling.BICUBIC,
        expand=True,
        fillcolor=_default_fill(image) if fill is None else fill,
    )
    return ImageOps.fit(
        rotated,
        image.size,
        method=Image.Resampling.LANCZOS,
        centering=(0.5, 0.5),
    )


def affine_shear(
    image: Image.Image,
    *,
    x_degrees: float = 2.0,
    fill: FillColor | None = None,
) -> Image.Image:
    """Shear horizontally around the image center on a fixed-size canvas."""
    _require_image(image)
    shear = math.tan(math.radians(x_degrees))
    center_y = image.height / 2
    return image.transform(
        image.size,
        Image.Transform.AFFINE,
        (1, -shear, shear * center_y, 0, 1, 0),
        resample=Image.Resampling.BICUBIC,
        fillcolor=_default_fill(image) if fill is None else fill,
    )


def anisotropic_scale(
    image: Image.Image,
    *,
    width_scale: float = 0.98,
    height_scale: float = 1.02,
) -> Image.Image:
    """Resize width and height by different factors."""
    _require_image(image)
    return image.resize(
        _scaled_size(image, width_scale, height_scale),
        Image.Resampling.LANCZOS,
    )


def perspective_keystone(
    image: Image.Image,
    *,
    top_inset_fraction: float = 0.04,
    fill: FillColor | None = None,
) -> Image.Image:
    """Apply a symmetric keystone warp to simulate an off-axis view."""
    _require_image(image)
    if not 0 <= top_inset_fraction < 0.5:
        raise ValueError("top_inset_fraction must be in [0, 0.5)")
    inset = image.width * top_inset_fraction

    return image.transform(
        image.size,
        Image.Transform.QUAD,
        (
            inset,
            0,
            0,
            image.height,
            image.width,
            image.height,
            image.width - inset,
            0,
        ),
        resample=Image.Resampling.BICUBIC,
        fillcolor=_default_fill(image) if fill is None else fill,
    )


def radial_lens_distortion(
    image: Image.Image,
    *,
    strength: float = 0.08,
    mesh_size: int = 18,
    fill: FillColor | None = None,
) -> Image.Image:
    """Apply barrel (positive) or pincushion (negative) radial distortion."""
    _require_image(image)
    if mesh_size < 2:
        raise ValueError("mesh_size must be at least 2")
    center_x = image.width / 2
    center_y = image.height / 2
    radius = max(center_x, center_y)

    def mapper(x: float, y: float) -> tuple[float, float]:
        nx = (x - center_x) / radius
        ny = (y - center_y) / radius
        radius_squared = nx * nx + ny * ny
        factor = 1 + strength * radius_squared
        return center_x + nx * factor * radius, center_y + ny * factor * radius

    return _mesh_transform(image, mesh_size, mesh_size, mapper, fill=fill)


def elastic_deformation(
    image: Image.Image,
    *,
    amplitude_fraction: float = 0.012,
    mesh_columns: int = 10,
    mesh_rows: int = 10,
    smoothing_passes: int = 3,
    seed: int = 0,
    fill: FillColor | None = None,
) -> Image.Image:
    """Warp through a seeded, smoothed random displacement field."""
    _require_image(image)
    _require_fraction("amplitude_fraction", amplitude_fraction)
    if smoothing_passes < 0:
        raise ValueError("smoothing_passes must not be negative")
    amplitude = min(image.size) * amplitude_fraction
    grid = _smooth_displacement_grid(
        mesh_columns,
        mesh_rows,
        amplitude,
        random.Random(seed),
        smoothing_passes,
    )
    mapper = _grid_mapper(image, mesh_columns, mesh_rows, grid)
    return _mesh_transform(image, mesh_columns, mesh_rows, mapper, fill=fill)


def wave_displacement(
    image: Image.Image,
    *,
    amplitude_fraction: float = 0.01,
    cycles: float = 2.0,
    angle_degrees: float = 0.0,
    mesh_size: int = 24,
    fill: FillColor | None = None,
) -> Image.Image:
    """Displace pixels with a smooth sinusoidal field."""
    _require_image(image)
    _require_fraction("amplitude_fraction", amplitude_fraction)
    amplitude = min(image.size) * amplitude_fraction
    angle = math.radians(angle_degrees)
    direction_x = math.cos(angle)
    direction_y = math.sin(angle)

    def mapper(x: float, y: float) -> tuple[float, float]:
        phase = 2 * math.pi * cycles * y / max(1, image.height)
        offset = amplitude * math.sin(phase)
        return x + direction_x * offset, y + direction_y * offset

    return _mesh_transform(image, mesh_size, mesh_size, mapper, fill=fill)


def mesh_warp(
    image: Image.Image,
    *,
    amplitude_fraction: float = 0.015,
    mesh_columns: int = 6,
    mesh_rows: int = 6,
    seed: int = 0,
    fill: FillColor | None = None,
) -> Image.Image:
    """Move a seeded control mesh and interpolate smoothly between nodes."""
    _require_image(image)
    _require_fraction("amplitude_fraction", amplitude_fraction)
    amplitude = min(image.size) * amplitude_fraction
    grid = _smooth_displacement_grid(
        mesh_columns,
        mesh_rows,
        amplitude,
        random.Random(seed),
        1,
    )
    mapper = _grid_mapper(image, mesh_columns, mesh_rows, grid)
    return _mesh_transform(image, mesh_columns, mesh_rows, mapper, fill=fill)


def localized_swirl(
    image: Image.Image,
    *,
    strength_degrees: float = 6.0,
    radius_fraction: float = 0.3,
    mesh_size: int = 28,
    seed: int = 0,
    fill: FillColor | None = None,
) -> Image.Image:
    """Apply a seeded swirl at a random interior location."""
    _require_image(image)
    if not 0 < radius_fraction <= 1:
        raise ValueError("radius_fraction must be in (0, 1]")
    rng = random.Random(seed)
    center_x = rng.uniform(image.width * 0.3, image.width * 0.7)
    center_y = rng.uniform(image.height * 0.3, image.height * 0.7)
    radius = min(image.size) * radius_fraction
    maximum_angle = math.radians(strength_degrees)

    def mapper(x: float, y: float) -> tuple[float, float]:
        dx = x - center_x
        dy = y - center_y
        distance = math.hypot(dx, dy)
        if distance >= radius:
            return x, y
        falloff = (1 - distance / radius) ** 2
        angle = maximum_angle * falloff
        cosine = math.cos(angle)
        sine = math.sin(angle)
        return (
            center_x + cosine * dx - sine * dy,
            center_y + sine * dx + cosine * dy,
        )

    return _mesh_transform(image, mesh_size, mesh_size, mapper, fill=fill)


def random_patch_displacement(
    image: Image.Image,
    *,
    patch_count: int = 3,
    patch_fraction: float = 0.08,
    maximum_offset_fraction: float = 0.06,
    seed: int = 0,
) -> Image.Image:
    """Swap randomly selected patches with nearby patches of equal size."""
    _require_image(image)
    if patch_count < 1:
        raise ValueError("patch_count must be positive")
    if not 0 < patch_fraction < 1:
        raise ValueError("patch_fraction must be in (0, 1)")
    _require_fraction("maximum_offset_fraction", maximum_offset_fraction)

    rng = random.Random(seed)
    result = image.copy()
    patch_width = max(1, round(image.width * patch_fraction))
    patch_height = max(1, round(image.height * patch_fraction))
    max_dx = max(1, round(image.width * maximum_offset_fraction))
    max_dy = max(1, round(image.height * maximum_offset_fraction))

    for _ in range(patch_count):
        x1 = rng.randint(0, image.width - patch_width)
        y1 = rng.randint(0, image.height - patch_height)
        dx = rng.choice((-1, 1)) * rng.randint(1, max_dx)
        dy = rng.choice((-1, 1)) * rng.randint(1, max_dy)
        x2 = max(0, min(image.width - patch_width, x1 + dx))
        y2 = max(0, min(image.height - patch_height, y1 + dy))
        first_box = (x1, y1, x1 + patch_width, y1 + patch_height)
        second_box = (x2, y2, x2 + patch_width, y2 + patch_height)
        first_patch = result.crop(first_box)
        second_patch = result.crop(second_box)
        result.paste(second_patch, first_box)
        result.paste(first_patch, second_box)

    return result


def grid_cell_permutation(
    image: Image.Image,
    *,
    columns: int = 5,
    rows: int = 5,
    swap_count: int = 2,
    seed: int = 0,
) -> Image.Image:
    """Swap seeded neighboring grid cells."""
    _require_image(image)
    if columns < 2 or rows < 2:
        raise ValueError("columns and rows must both be at least 2")
    if swap_count < 1:
        raise ValueError("swap_count must be positive")

    rng = random.Random(seed)
    result = image.copy()

    def cell_box(column: int, row: int) -> tuple[int, int, int, int]:
        return (
            round(column * image.width / columns),
            round(row * image.height / rows),
            round((column + 1) * image.width / columns),
            round((row + 1) * image.height / rows),
        )

    for _ in range(swap_count):
        column = rng.randrange(columns)
        row = rng.randrange(rows)
        neighbors = [
            (candidate_column, candidate_row)
            for candidate_column, candidate_row in (
                (column - 1, row),
                (column + 1, row),
                (column, row - 1),
                (column, row + 1),
            )
            if 0 <= candidate_column < columns and 0 <= candidate_row < rows
        ]
        other_column, other_row = rng.choice(neighbors)
        first_box = cell_box(column, row)
        second_box = cell_box(other_column, other_row)
        first_patch = result.crop(first_box)
        second_patch = result.crop(second_box)
        first_size = (first_box[2] - first_box[0], first_box[3] - first_box[1])
        second_size = (second_box[2] - second_box[0], second_box[3] - second_box[1])
        result.paste(second_patch.resize(first_size, Image.Resampling.LANCZOS), first_box)
        result.paste(first_patch.resize(second_size, Image.Resampling.LANCZOS), second_box)

    return result


def _pixel_luminance(pixel: int | tuple[int, ...]) -> int:
    if isinstance(pixel, int):
        return pixel
    if len(pixel) < 3:
        return pixel[0]
    return round(0.299 * pixel[0] + 0.587 * pixel[1] + 0.114 * pixel[2])


def _remove_one_vertical_seam(
    pixel_rows: list[list[int | tuple[int, ...]]],
    luminance_rows: list[list[int]],
) -> None:
    height = len(luminance_rows)
    width = len(luminance_rows[0])
    energy: list[list[int]] = [[0] * width for _ in range(height)]
    for y in range(height):
        above = max(0, y - 1)
        below = min(height - 1, y + 1)
        for x in range(width):
            left = max(0, x - 1)
            right = min(width - 1, x + 1)
            energy[y][x] = (
                abs(luminance_rows[y][right] - luminance_rows[y][left])
                + abs(luminance_rows[below][x] - luminance_rows[above][x])
            )

    cumulative = energy[0][:]
    parents: list[list[int]] = [[0] * width for _ in range(height)]
    for y in range(1, height):
        next_cumulative = [0] * width
        for x in range(width):
            candidates = range(max(0, x - 1), min(width, x + 2))
            parent_x = min(candidates, key=lambda candidate: cumulative[candidate])
            parents[y][x] = parent_x
            next_cumulative[x] = energy[y][x] + cumulative[parent_x]
        cumulative = next_cumulative

    seam_x = min(range(width), key=lambda x: cumulative[x])
    seam = [0] * height
    seam[-1] = seam_x
    for y in range(height - 1, 0, -1):
        seam[y - 1] = parents[y][seam[y]]

    for y, x in enumerate(seam):
        del pixel_rows[y][x]
        del luminance_rows[y][x]


def content_aware_seam_compress(
    image: Image.Image,
    *,
    width_fraction: float = 0.02,
    restore_size: bool = True,
) -> Image.Image:
    """Remove low-energy vertical seams, optionally restoring original size."""
    _require_image(image)
    if not 0 < width_fraction < 1:
        raise ValueError("width_fraction must be in (0, 1)")
    seams = max(1, round(image.width * width_fraction))
    if seams >= image.width:
        raise ValueError("width_fraction removes the entire image")

    pixels = list(image.get_flattened_data())
    pixel_rows = [
        pixels[y * image.width : (y + 1) * image.width]
        for y in range(image.height)
    ]
    luminance_rows = [
        [_pixel_luminance(pixel) for pixel in row]
        for row in pixel_rows
    ]
    for _ in range(seams):
        _remove_one_vertical_seam(pixel_rows, luminance_rows)

    compressed = Image.new(image.mode, (image.width - seams, image.height))
    compressed.putdata([pixel for row in pixel_rows for pixel in row])
    if not restore_size:
        return compressed
    return compressed.resize(image.size, Image.Resampling.LANCZOS)


def downsample_then_upsample(
    image: Image.Image,
    *,
    scale: float = 0.5,
    down_filter: Image.Resampling = Image.Resampling.LANCZOS,
    up_filter: Image.Resampling = Image.Resampling.BICUBIC,
) -> Image.Image:
    """Reduce dimensions and restore the original size."""
    _require_image(image)
    if not 0 < scale < 1:
        raise ValueError("scale must be in (0, 1)")
    reduced = image.resize(_scaled_size(image, scale, scale), down_filter)
    return reduced.resize(image.size, up_filter)


def nearest_neighbor_resampling(
    image: Image.Image,
    *,
    scale: float = 0.5,
    restore_size: bool = True,
) -> Image.Image:
    """Resize through nearest-neighbor sampling for block-like replication."""
    _require_image(image)
    if scale <= 0:
        raise ValueError("scale must be positive")
    resized = image.resize(_scaled_size(image, scale, scale), Image.Resampling.NEAREST)
    if not restore_size:
        return resized
    return resized.resize(image.size, Image.Resampling.NEAREST)


def mixed_filter_resize_chain(
    image: Image.Image,
    *,
    scales: Sequence[float] = (0.92, 1.07, 1.0),
    filters: Sequence[Image.Resampling] = (
        Image.Resampling.BILINEAR,
        Image.Resampling.BICUBIC,
        Image.Resampling.LANCZOS,
    ),
) -> Image.Image:
    """Apply a resize chain with explicitly paired scales and filters."""
    _require_image(image)
    if not scales or len(scales) != len(filters):
        raise ValueError("scales and filters must be nonempty and have equal lengths")
    if any(scale <= 0 for scale in scales):
        raise ValueError("every scale must be positive")

    result = image.copy()
    original_size = image.size
    for index, (scale, resize_filter) in enumerate(zip(scales, filters, strict=True)):
        target_size = original_size if index == len(scales) - 1 and scale == 1 else _scaled_size(result, scale, scale)
        result = result.resize(target_size, resize_filter)
    return result


def subpixel_translation(
    image: Image.Image,
    *,
    x_pixels: float = 0.35,
    y_pixels: float = 0.35,
    fill: FillColor | None = None,
) -> Image.Image:
    """Translate by fractional pixels using bicubic interpolation."""
    _require_image(image)
    return image.transform(
        image.size,
        Image.Transform.AFFINE,
        (1, 0, -x_pixels, 0, 1, -y_pixels),
        resample=Image.Resampling.BICUBIC,
        fillcolor=_default_fill(image) if fill is None else fill,
    )


def jpeg_recompression(
    image: Image.Image,
    *,
    quality: int = 82,
    subsampling: int = 2,
) -> Image.Image:
    """Round-trip through an in-memory JPEG encoding."""
    _require_image(image)
    if not 1 <= quality <= 100:
        raise ValueError("quality must be in [1, 100]")
    if subsampling not in (0, 1, 2):
        raise ValueError("subsampling must be 0, 1, or 2")
    buffer = BytesIO()
    image.convert("RGB").save(
        buffer,
        format="JPEG",
        quality=quality,
        subsampling=subsampling,
        optimize=False,
    )
    buffer.seek(0)
    with Image.open(buffer) as decoded:
        return decoded.copy()


def webp_recompression(
    image: Image.Image,
    *,
    quality: int = 80,
    method: int = 4,
) -> Image.Image:
    """Round-trip through an in-memory lossy WebP encoding."""
    _require_image(image)
    if not 1 <= quality <= 100:
        raise ValueError("quality must be in [1, 100]")
    if not 0 <= method <= 6:
        raise ValueError("method must be in [0, 6]")
    buffer = BytesIO()
    image.save(buffer, format="WEBP", quality=quality, method=method, lossless=False)
    buffer.seek(0)
    with Image.open(buffer) as decoded:
        return decoded.copy()


def palette_quantization(
    image: Image.Image,
    *,
    colors: int = 64,
) -> Image.Image:
    """Reduce the image to a limited adaptive palette and return RGB pixels."""
    _require_image(image)
    if not 2 <= colors <= 256:
        raise ValueError("colors must be in [2, 256]")
    source = image.convert("RGB")
    quantized = source.quantize(
        colors=colors,
        method=Image.Quantize.MEDIANCUT,
        dither=Image.Dither.NONE,
    )
    return quantized.convert("RGB")


def apply_dithering(
    image: Image.Image,
    *,
    colors: int = 32,
) -> Image.Image:
    """Quantize with Floyd-Steinberg error-diffusion dithering."""
    _require_image(image)
    if not 2 <= colors <= 256:
        raise ValueError("colors must be in [2, 256]")
    return image.convert("RGB").quantize(
        colors=colors,
        method=Image.Quantize.MEDIANCUT,
        dither=Image.Dither.FLOYDSTEINBERG,
    ).convert("RGB")


def gaussian_blur(
    image: Image.Image,
    *,
    radius: float = 1.2,
) -> Image.Image:
    """Apply a Gaussian neighborhood blur."""
    _require_image(image)
    if radius < 0:
        raise ValueError("radius must not be negative")
    return image.filter(ImageFilter.GaussianBlur(radius))


def median_filter(
    image: Image.Image,
    *,
    size: int = 3,
) -> Image.Image:
    """Replace pixels with the median value in an odd-sized neighborhood."""
    _require_image(image)
    if size < 3 or size % 2 == 0:
        raise ValueError("size must be an odd integer of at least 3")
    return image.filter(ImageFilter.MedianFilter(size))


def motion_blur(
    image: Image.Image,
    *,
    length: int = 5,
    angle_degrees: float = 0.0,
) -> Image.Image:
    """Apply a short directional blur at an arbitrary angle."""
    _require_image(image)
    if length not in (3, 5):
        raise ValueError("length must be 3 or 5")
    angle = math.radians(angle_degrees)
    center = length // 2
    weights = [0.0] * (length * length)
    for distance in range(-center, center + 1):
        x = round(center + distance * math.cos(angle))
        y = round(center + distance * math.sin(angle))
        weights[y * length + x] += 1.0
    sample_count = sum(weights)
    normalized = [weight / sample_count for weight in weights]
    return image.filter(ImageFilter.Kernel((length, length), normalized, scale=1))


def additive_sensor_noise(
    image: Image.Image,
    *,
    standard_deviation: float = 6.0,
    salt_pepper_probability: float = 0.0,
    seed: int = 0,
) -> Image.Image:
    """Add seeded Gaussian noise and optional salt-and-pepper impulses."""
    _require_image(image)
    if standard_deviation < 0:
        raise ValueError("standard_deviation must not be negative")
    if not 0 <= salt_pepper_probability <= 1:
        raise ValueError("salt_pepper_probability must be in [0, 1]")

    original_mode = image.mode
    has_alpha = original_mode in ("RGBA", "LA")
    source = image.convert("RGBA" if has_alpha else "RGB")
    rng = random.Random(seed)
    result_pixels: list[tuple[int, ...]] = []

    for pixel in source.get_flattened_data():
        channels = list(pixel)
        color_count = 3
        if rng.random() < salt_pepper_probability:
            impulse = 0 if rng.random() < 0.5 else 255
            channels[:color_count] = [impulse] * color_count
        else:
            channels[:color_count] = [
                round(_clamp(channel + rng.gauss(0, standard_deviation), 0, 255))
                for channel in channels[:color_count]
            ]
        result_pixels.append(tuple(channels))

    result = Image.new(source.mode, source.size)
    result.putdata(result_pixels)
    if original_mode in ("RGB", "RGBA"):
        return result
    return result.convert(original_mode)


def gamma_contrast_remap(
    image: Image.Image,
    *,
    gamma: float = 1.08,
    contrast: float = 1.05,
) -> Image.Image:
    """Apply nonlinear gamma remapping followed by contrast adjustment."""
    _require_image(image)
    if gamma <= 0 or contrast < 0:
        raise ValueError("gamma must be positive and contrast must not be negative")
    lookup = [
        round(255 * ((value / 255) ** (1 / gamma)))
        for value in range(256)
    ]

    if image.mode == "RGBA":
        red, green, blue, alpha = image.split()
        remapped = Image.merge(
            "RGB",
            (red.point(lookup), green.point(lookup), blue.point(lookup)),
        )
        adjusted = ImageEnhance.Contrast(remapped).enhance(contrast)
        adjusted.putalpha(alpha)
        return adjusted
    if image.mode == "RGB":
        channels = [channel.point(lookup) for channel in image.split()]
        return ImageEnhance.Contrast(Image.merge("RGB", channels)).enhance(contrast)

    converted = image.convert("RGB")
    channels = [channel.point(lookup) for channel in converted.split()]
    adjusted = ImageEnhance.Contrast(Image.merge("RGB", channels)).enhance(contrast)
    return adjusted.convert(image.mode)


def chroma_subsample_and_channel_shift(
    image: Image.Image,
    *,
    chroma_scale: float = 0.5,
    red_shift: tuple[float, float] = (0.5, 0.0),
    blue_shift: tuple[float, float] = (-0.5, 0.0),
) -> Image.Image:
    """Reduce chroma resolution and apply small red/blue channel offsets."""
    _require_image(image)
    if not 0 < chroma_scale <= 1:
        raise ValueError("chroma_scale must be in (0, 1]")

    alpha = image.getchannel("A") if "A" in image.getbands() else None
    luminance, blue_difference, red_difference = image.convert("RGB").convert("YCbCr").split()
    reduced_size = (
        max(1, round(image.width * chroma_scale)),
        max(1, round(image.height * chroma_scale)),
    )
    blue_difference = blue_difference.resize(reduced_size, Image.Resampling.BOX).resize(
        image.size,
        Image.Resampling.BILINEAR,
    )
    red_difference = red_difference.resize(reduced_size, Image.Resampling.BOX).resize(
        image.size,
        Image.Resampling.BILINEAR,
    )
    red, green, blue = Image.merge(
        "YCbCr",
        (luminance, blue_difference, red_difference),
    ).convert("RGB").split()

    def shift(channel: Image.Image, offset: tuple[float, float]) -> Image.Image:
        return channel.transform(
            channel.size,
            Image.Transform.AFFINE,
            (1, 0, -offset[0], 0, 1, -offset[1]),
            resample=Image.Resampling.BICUBIC,
            fillcolor=255,
        )

    result = Image.merge("RGB", (shift(red, red_shift), green, shift(blue, blue_shift)))
    if alpha is not None:
        result.putalpha(alpha)
    return result


def random_non_targeted_cutout(
    image: Image.Image,
    *,
    count: int = 3,
    minimum_fraction: float = 0.025,
    maximum_fraction: float = 0.08,
    shape: str = "rectangle",
    fill: FillColor | None = None,
    seed: int = 0,
) -> Image.Image:
    """Place seeded random cutouts without accepting target coordinates."""
    _require_image(image)
    if count < 1:
        raise ValueError("count must be positive")
    if not 0 < minimum_fraction <= maximum_fraction < 1:
        raise ValueError("fractions must satisfy 0 < minimum <= maximum < 1")
    if shape not in ("rectangle", "ellipse"):
        raise ValueError("shape must be 'rectangle' or 'ellipse'")

    rng = random.Random(seed)
    result = image.copy()
    draw = ImageDraw.Draw(result)
    cutout_fill = _default_fill(image) if fill is None else fill

    for _ in range(count):
        width = max(
            1,
            round(image.width * rng.uniform(minimum_fraction, maximum_fraction)),
        )
        height = max(
            1,
            round(image.height * rng.uniform(minimum_fraction, maximum_fraction)),
        )
        x = rng.randint(0, image.width - width)
        y = rng.randint(0, image.height - height)
        box = (x, y, x + width, y + height)
        if shape == "rectangle":
            draw.rectangle(box, fill=cutout_fill)
        else:
            draw.ellipse(box, fill=cutout_fill)

    return result
