import unittest

from coin_ml.scoring import score, total_cents


class ScoringTests(unittest.TestCase):
    def test_total_cents(self):
        self.assertEqual(total_cents([0, 7, 6]), 1 + 200 + 100)

    def test_perfect_predictions(self):
        truth = {"a": [7, 7, 0], "b": [3]}
        result = score(truth, truth)
        self.assertEqual(result["total_exact"], 1.0)
        self.assertEqual(result["counts_exact"], 1.0)
        self.assertEqual(result["mean_abs_error_cents"], 0.0)

    def test_total_can_be_right_with_wrong_counts(self):
        # 2 x 1 ct = 2 ct = 1 x 2 ct: totale uguale, conteggi diversi
        result = score({"a": [0, 0]}, {"a": [1]})
        self.assertEqual(result["total_exact"], 1.0)
        self.assertEqual(result["counts_exact"], 0.0)

    def test_missing_prediction_counts_as_no_coins(self):
        result = score({}, {"a": [7]})
        self.assertEqual(result["total_exact"], 0.0)
        self.assertEqual(result["mean_abs_error_cents"], 200.0)

    def test_per_class_recall_and_precision(self):
        result = score({"a": [7, 7, 6]}, {"a": [7, 6, 6]})
        two = result["per_class"]["2 euro"]
        self.assertEqual(two["recall"], 1.0)       # 1 vera moneta da 2€, trovata
        self.assertEqual(two["precision"], 0.5)    # 2 predette, 1 giusta
        one = result["per_class"]["1 euro"]
        self.assertEqual(one["recall"], 0.5)


if __name__ == "__main__":
    unittest.main()
