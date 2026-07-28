from __future__ import annotations

import importlib.util
from pathlib import Path
import unittest

from PIL import Image


MODULE_PATH = Path(__file__).with_name("image_perturbations.py")
SPEC = importlib.util.spec_from_file_location("image_perturbations", MODULE_PATH)
if SPEC is None or SPEC.loader is None:
    raise RuntimeError(f"Could not load {MODULE_PATH}")
perturbations = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(perturbations)


def make_pattern(width: int = 48, height: int = 40) -> Image.Image:
    image = Image.new("RGB", (width, height))
    pixels = [
        (
            (x * 9 + y * 3) % 256,
            (x * 2 + y * 11) % 256,
            (x * 7 + y * 5) % 256,
        )
        for y in range(height)
        for x in range(width)
    ]
    image.putdata(pixels)
    return image


class ImagePerturbationTests(unittest.TestCase):
    def setUp(self) -> None:
        self.image = make_pattern()
        self.original_bytes = self.image.tobytes()

    def test_all_thirty_functions_return_nonempty_images_without_mutating_input(self) -> None:
        cases = [
            ("asymmetric_edge_crop", lambda: perturbations.asymmetric_edge_crop(self.image)),
            ("translate_and_pad", lambda: perturbations.translate_and_pad(self.image)),
            ("micro_rotate", lambda: perturbations.micro_rotate(self.image)),
            ("rotate_crop_rescale", lambda: perturbations.rotate_crop_rescale(self.image)),
            ("affine_shear", lambda: perturbations.affine_shear(self.image)),
            ("anisotropic_scale", lambda: perturbations.anisotropic_scale(self.image)),
            ("perspective_keystone", lambda: perturbations.perspective_keystone(self.image)),
            ("radial_lens_distortion", lambda: perturbations.radial_lens_distortion(self.image)),
            ("elastic_deformation", lambda: perturbations.elastic_deformation(self.image)),
            ("wave_displacement", lambda: perturbations.wave_displacement(self.image)),
            ("mesh_warp", lambda: perturbations.mesh_warp(self.image)),
            ("localized_swirl", lambda: perturbations.localized_swirl(self.image)),
            ("random_patch_displacement", lambda: perturbations.random_patch_displacement(self.image)),
            ("grid_cell_permutation", lambda: perturbations.grid_cell_permutation(self.image)),
            (
                "content_aware_seam_compress",
                lambda: perturbations.content_aware_seam_compress(self.image),
            ),
            ("downsample_then_upsample", lambda: perturbations.downsample_then_upsample(self.image)),
            (
                "nearest_neighbor_resampling",
                lambda: perturbations.nearest_neighbor_resampling(self.image),
            ),
            (
                "mixed_filter_resize_chain",
                lambda: perturbations.mixed_filter_resize_chain(self.image),
            ),
            ("subpixel_translation", lambda: perturbations.subpixel_translation(self.image)),
            ("jpeg_recompression", lambda: perturbations.jpeg_recompression(self.image)),
            ("webp_recompression", lambda: perturbations.webp_recompression(self.image)),
            ("palette_quantization", lambda: perturbations.palette_quantization(self.image)),
            ("apply_dithering", lambda: perturbations.apply_dithering(self.image)),
            ("gaussian_blur", lambda: perturbations.gaussian_blur(self.image)),
            ("median_filter", lambda: perturbations.median_filter(self.image)),
            ("motion_blur", lambda: perturbations.motion_blur(self.image, angle_degrees=17)),
            ("additive_sensor_noise", lambda: perturbations.additive_sensor_noise(self.image)),
            ("gamma_contrast_remap", lambda: perturbations.gamma_contrast_remap(self.image)),
            (
                "chroma_subsample_and_channel_shift",
                lambda: perturbations.chroma_subsample_and_channel_shift(self.image),
            ),
            (
                "random_non_targeted_cutout",
                lambda: perturbations.random_non_targeted_cutout(self.image),
            ),
        ]

        self.assertEqual(30, len(cases))
        for name, operation in cases:
            with self.subTest(name=name):
                result = operation()
                self.assertIsInstance(result, Image.Image)
                self.assertGreater(result.width, 0)
                self.assertGreater(result.height, 0)
                result.load()
                self.assertEqual(self.original_bytes, self.image.tobytes())

    def test_seeded_operations_are_reproducible(self) -> None:
        operations = [
            lambda: perturbations.elastic_deformation(self.image, seed=41),
            lambda: perturbations.mesh_warp(self.image, seed=41),
            lambda: perturbations.localized_swirl(self.image, seed=41),
            lambda: perturbations.random_patch_displacement(self.image, seed=41),
            lambda: perturbations.grid_cell_permutation(self.image, seed=41),
            lambda: perturbations.additive_sensor_noise(self.image, seed=41),
            lambda: perturbations.random_non_targeted_cutout(self.image, seed=41),
        ]

        for index, operation in enumerate(operations):
            with self.subTest(index=index):
                first = operation()
                second = operation()
                self.assertEqual(first.mode, second.mode)
                self.assertEqual(first.size, second.size)
                self.assertEqual(first.tobytes(), second.tobytes())

    def test_invalid_ranges_fail_closed(self) -> None:
        with self.assertRaises(ValueError):
            perturbations.asymmetric_edge_crop(self.image, left=0.6, right=0.5)
        with self.assertRaises(ValueError):
            perturbations.downsample_then_upsample(self.image, scale=1.0)
        with self.assertRaises(ValueError):
            perturbations.jpeg_recompression(self.image, quality=0)
        with self.assertRaises(ValueError):
            perturbations.random_non_targeted_cutout(self.image, shape="targeted")


if __name__ == "__main__":
    unittest.main()
