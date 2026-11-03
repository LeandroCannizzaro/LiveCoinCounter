import tempfile
import unittest
from pathlib import Path

import cv2
import numpy as np

from coin_ml import synth
from coin_ml.make_dataset import group_similar, split_groups
from coin_ml.stress import add_shadow, stress


class StressTests(unittest.TestCase):
    def test_shadow_darkens_but_keeps_shape_and_is_deterministic(self):
        image = np.full((120, 160, 3), 200, np.uint8)
        a = add_shadow(image, np.random.default_rng(1))
        b = add_shadow(image, np.random.default_rng(1))
        self.assertEqual(a.shape, image.shape)
        self.assertTrue((a == b).all())
        self.assertLess(a.mean(), image.mean())

    def test_stress_changes_the_image(self):
        image = np.full((100, 100, 3), 180, np.uint8)
        self.assertGreater(np.abs(stress(image, np.random.default_rng(3)).astype(int) - image).mean(), 1)


class SynthTests(unittest.TestCase):
    def test_generated_labels_are_inside_the_image_and_valid(self):
        with tempfile.TemporaryDirectory() as tmp:
            root = Path(tmp)
            (root / "images" / "train").mkdir(parents=True)
            (root / "labels" / "train").mkdir(parents=True)
            rng = np.random.default_rng(0)
            # una foto di prova con due monete "finte" (cerchi) e le sue etichette (1 euro, 2 euro)
            image = np.full((640, 640, 3), 90, np.uint8)
            cv2.circle(image, (160, 160), 70, (30, 160, 200), -1)
            cv2.circle(image, (440, 440), 80, (200, 200, 200), -1)
            cv2.imwrite(str(root / "images" / "train" / "a.jpg"), image)
            (root / "labels" / "train" / "a.txt").write_text(
                "6 0.25 0.25 0.22 0.22\n7 0.6875 0.6875 0.25 0.25", encoding="utf-8")

            self.assertEqual(synth.generate(root, 5, seed=1), 5)

            for label in (root / "labels" / "train").glob("syn_*.txt"):
                for line in label.read_text(encoding="utf-8").splitlines():
                    cls, cx, cy, w, h = line.split()
                    self.assertIn(int(cls), (6, 7))
                    self.assertTrue(0 < float(cx) < 1 and 0 < float(cy) < 1)
                    self.assertTrue(0 < float(w) <= 0.5 and 0 < float(h) <= 0.5)
            self.assertEqual(rng.integers(1), 0)


class GroupingTests(unittest.TestCase):
    def test_identical_images_share_a_group_and_a_split(self):
        with tempfile.TemporaryDirectory() as tmp:
            paths = []
            rng = np.random.default_rng(5)
            base = rng.integers(0, 255, (64, 64, 3), np.uint8)
            for name, img in (("a1", base), ("a2", base.copy()), ("b", rng.integers(0, 255, (64, 64, 3), np.uint8))):
                path = Path(tmp) / f"{name}.png"
                cv2.imwrite(str(path), img)
                paths.append(path)

            groups = group_similar(paths, max_distance=4)
            self.assertEqual(sorted(len(g) for g in groups), [1, 2])

            split = split_groups(groups, 0.34, 0.34, seed=0)
            for part in split.values():
                names = {p.stem for p in part}
                self.assertEqual(len(names & {"a1", "a2"}) in (0, 2), True)


if __name__ == "__main__":
    unittest.main()
