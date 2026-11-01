"""Tests for whole-cell and whole-row numeric table diagnostics."""

import unittest

from generate_numeric_table_corpus import table_reference, table_rows
from numeric_table_metric import score_export, score_page


class NumericTableMetricTests(unittest.TestCase):
    def setUp(self):
        lines, _ = table_reference(4)
        self.lines = lines
        self.page = {"expectedText": "\n".join(lines), "numericTable": {"rows": table_rows(4)}}

    def test_perfect_table_with_spacing_and_unicode_normalization(self):
        prediction = "\n".join(self.lines).replace("12,87", "１２,８７").replace(" ", "  ")
        self.assertEqual(score_page(self.page, prediction),
                         {"exactCells": 16, "referenceCells": 16, "exactRows": 4, "referenceRows": 4})

    def test_wrong_cell_does_not_credit_complete_row(self):
        prediction = "\n".join(self.lines).replace("13,24", "13,25")
        self.assertEqual(score_page(self.page, prediction),
                         {"exactCells": 15, "referenceCells": 16, "exactRows": 3, "referenceRows": 4})

    def test_missing_row_and_substring_do_not_match(self):
        prediction = "\n".join(line for line in self.lines if line != self.lines[3])
        prediction = prediction.replace("25,74", "125,74")
        self.assertEqual(score_page(self.page, prediction),
                         {"exactCells": 11, "referenceCells": 16, "exactRows": 2, "referenceRows": 4})

    def test_duplicate_prediction_cannot_credit_two_rows(self):
        lines = ["TITLE", "1 2 10,00 20,00", "2 2 10,00 20,00"]
        page = {"expectedText": "\n".join(lines),
                "numericTable": {"rows": [["1", "2", "10,00", "20,00"],
                                           ["2", "2", "10,00", "20,00"]]}}
        self.assertEqual(score_page(page, "TITLE\n1 2 10,00 20,00"),
                         {"exactCells": 4, "referenceCells": 8, "exactRows": 1, "referenceRows": 2})

    def test_export_suffix_required(self):
        with self.assertRaises(ValueError):
            score_export(self.page, "\n".join(self.lines))
        self.assertEqual(score_export(self.page, "\n".join(self.lines) + "\nПроверено.")["exactRows"], 4)

    def test_declaration_must_match_complete_reference(self):
        page = {"expectedText": self.page["expectedText"],
                "numericTable": {"rows": [["1", "2", "12,87", "99,99"]]}}
        with self.assertRaises(ValueError):
            score_page(page, self.page["expectedText"])


if __name__ == "__main__":
    unittest.main()
