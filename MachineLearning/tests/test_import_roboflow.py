import unittest

from coin_ml.import_roboflow import class_map, convert_label, source_key


class ImportRoboflowTests(unittest.TestCase):
    def test_class_map_translates_alphabetical_roboflow_order_to_project_order(self):
        mapping = class_map(["1", "10", "100", "2", "20", "200", "5", "50"])
        # '1' -> 0, '10' -> 3, '100' -> 6, '2' -> 1, '20' -> 4, '200' -> 7, '5' -> 2, '50' -> 5
        self.assertEqual([mapping[i] for i in range(8)], [0, 3, 6, 1, 4, 7, 2, 5])

    def test_class_map_rejects_unknown_names(self):
        with self.assertRaises(ValueError):
            class_map(["3"])

    def test_source_key_groups_augmented_copies(self):
        a = source_key("IMG_0001_jpg.rf.0123abcd")
        b = source_key("IMG_0001_jpg.rf.ffff9999")
        self.assertEqual(a, b)
        self.assertEqual(source_key("plain_name"), "plain_name")

    def test_convert_label_remaps_class_and_keeps_coordinates(self):
        mapping = class_map(["1", "10", "100", "2", "20", "200", "5", "50"])
        self.assertEqual(convert_label("2 0.5 0.5 0.1 0.1\n", mapping), ["6 0.5 0.5 0.1 0.1"])

    def test_convert_label_rejects_polygons(self):
        mapping = class_map(["1"])
        with self.assertRaises(ValueError):
            convert_label("0 0.1 0.1 0.2 0.1 0.2 0.2 0.1 0.2", mapping)


if __name__ == "__main__":
    unittest.main()
