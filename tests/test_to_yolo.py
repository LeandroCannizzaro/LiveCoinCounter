import unittest

from coin_ml.to_yolo import label_to_yolo_lines


class ToYoloTests(unittest.TestCase):
    def test_converts_pixel_box_to_normalized_center_format(self):
        label = {
            "width": 1000,
            "height": 500,
            "coins": [{"label": 200, "bbox": [100, 50, 200, 100]}],
        }
        self.assertEqual(label_to_yolo_lines(label), ["7 0.200000 0.200000 0.200000 0.200000"])

    def test_maps_every_denomination_to_its_class(self):
        label = {
            "width": 100,
            "height": 100,
            "coins": [{"label": c, "bbox": [0, 0, 10, 10]} for c in (1, 2, 5, 10, 20, 50, 100, 200)],
        }
        classes = [int(line.split()[0]) for line in label_to_yolo_lines(label)]
        self.assertEqual(classes, [0, 1, 2, 3, 4, 5, 6, 7])

    def test_rejects_unknown_value(self):
        label = {"width": 100, "height": 100, "coins": [{"label": 3, "bbox": [0, 0, 10, 10]}]}
        with self.assertRaises(ValueError):
            label_to_yolo_lines(label)

    def test_skips_coins_without_label(self):
        label = {"width": 100, "height": 100, "coins": [
            {"label": None, "bbox": [0, 0, 10, 10]},
            {"label": 50, "bbox": [0, 0, 10, 10]},
        ]}
        lines = label_to_yolo_lines(label)
        self.assertEqual(len(lines), 1)
        self.assertTrue(lines[0].startswith("5 "))

    def test_clamps_boxes_that_exceed_the_image(self):
        label = {"width": 100, "height": 100, "coins": [{"label": 1, "bbox": [90, 90, 40, 40]}]}
        values = [float(v) for v in label_to_yolo_lines(label)[0].split()[1:]]
        self.assertTrue(all(0.0 <= v <= 1.0 for v in values))


if __name__ == "__main__":
    unittest.main()
