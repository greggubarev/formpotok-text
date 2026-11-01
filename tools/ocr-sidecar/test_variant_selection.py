import unittest
from pathlib import Path

from main import select_paddle_variant, should_use_upright_table_fallback, text_result_coordinate_variant


def variant(name, score, angle=None, blocks=10, multi_column=False):
    def block(x):
        return {"Polygon": [{"X": x, "Y": 0}, {"X": x + 20, "Y": 0},
                            {"X": x + 20, "Y": 5}, {"X": x, "Y": 5}]}
    zones = [block(5) for _ in range(5)] + [block(70) for _ in range(5)] if multi_column else [block(5)]
    return name, Path(name + ".png"), {
        "orientation_angle": angle,
        "page": {"Width": 100, "TextBlocks": zones},
        "metrics": {"score": score, "block_count": blocks},
    }


class VariantSelectionTests(unittest.TestCase):
    def test_upright_fallback_requires_improved_numeric_grid_and_nearby_score(self):
        blocks = []
        for row in range(5):
            for column in range(4):
                x, y = 100 + column * 200, 30 + row * 20
                blocks.append({"Text": str(row + column + 1), "Polygon": [
                    {"X": x, "Y": y}, {"X": x + 30, "Y": y},
                    {"X": x + 30, "Y": y + 5}, {"X": x, "Y": y + 5}]})
        selected = {"orientation_angle": 180, "metrics": {"score": 0.91, "block_count": 19},
                    "page": {"Width": 850, "Height": 150, "TextBlocks": blocks[:-1]}}
        candidate = {"metrics": {"score": 0.87, "block_count": 20},
                     "page": {"Width": 850, "Height": 150, "TextBlocks": blocks}}
        self.assertTrue(should_use_upright_table_fallback(selected, candidate))
        candidate["metrics"]["score"] = 0.82
        self.assertFalse(should_use_upright_table_fallback(selected, candidate))
        candidate["metrics"]["score"] = 0.87
        candidate["metrics"]["block_count"] = 18
        self.assertFalse(should_use_upright_table_fallback(selected, candidate))
        candidate["metrics"]["block_count"] = 20
        candidate["page"]["TextBlocks"] = blocks[:-1]
        self.assertFalse(should_use_upright_table_fallback(selected, candidate))
        candidate["page"]["TextBlocks"] = blocks
        selected["page"]["TextBlocks"] = blocks
        self.assertFalse(should_use_upright_table_fallback(selected, candidate))

    def test_internal_preprocessor_cancels_external_rotation(self):
        self.assertEqual(text_result_coordinate_variant("rot90", 270, True), "original")
        self.assertEqual(text_result_coordinate_variant("rot270", 90, True), "original")

    def test_uncancelled_rotation_keeps_variant_frame(self):
        self.assertEqual(text_result_coordinate_variant("rot90", 0, True), "rot90")
        self.assertEqual(text_result_coordinate_variant("rot90", None, True), "rot90")
        self.assertEqual(text_result_coordinate_variant("rot90", 270, False), "rot90")
        self.assertEqual(text_result_coordinate_variant("original", 90, True), "original")

    def test_upright_page_keeps_original_when_rotation_is_only_marginally_better(self):
        options = [variant("original", 0.9905, 0, multi_column=True), variant("rot90", 0.9907), variant("rot270", 0.9909)]
        self.assertEqual(select_paddle_variant(options)[0], "original")

    def test_single_column_keeps_previous_score_rule(self):
        options = [variant("original", 0.9905, 0), variant("rot90", 0.9907)]
        self.assertEqual(select_paddle_variant(options)[0], "rot90")

    def test_numeric_table_keeps_previous_rotation_rule(self):
        original = variant("original", 0.9905, 0, multi_column=True)
        blocks = [{"Text": "РЕЕСТР", "Polygon": original[2]["page"]["TextBlocks"][0]["Polygon"]}]
        for row in range(7):
            for x, text in ((5, f"Папка {row}"), (45, f"{row + 2} шт."), (85, f"{row * 100} руб.")):
                blocks.append({"Text": text, "Polygon": [
                    {"X": x, "Y": 20 + row * 12}, {"X": x + 12, "Y": 20 + row * 12},
                    {"X": x + 12, "Y": 25 + row * 12}, {"X": x, "Y": 25 + row * 12}]})
        original[2]["page"]["TextBlocks"] = blocks
        self.assertEqual(select_paddle_variant([original, variant("rot90", 0.9907)])[0], "rot90")

    def test_sideways_page_can_use_rotation_despite_small_score_difference(self):
        options = [variant("original", 0.7244, 90), variant("rot90", 0.7269)]
        self.assertEqual(select_paddle_variant(options)[0], "rot90")

    def test_strong_rotation_gain_overrides_upright_classification(self):
        options = [variant("original", 0.70, 0), variant("rot270", 0.76)]
        self.assertEqual(select_paddle_variant(options)[0], "rot270")

    def test_unknown_angle_keeps_previous_score_rule(self):
        options = [variant("original", 0.9905), variant("rot90", 0.9907)]
        self.assertEqual(select_paddle_variant(options)[0], "rot90")


if __name__ == "__main__":
    unittest.main()
