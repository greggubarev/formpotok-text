import importlib.util
import tempfile
import unittest
from pathlib import Path


ROOT = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location("run_table_v2_lab", ROOT / "run_table_v2_lab.py")
TABLE = importlib.util.module_from_spec(spec)
spec.loader.exec_module(TABLE)


class TableCandidateTests(unittest.TestCase):
    def test_model_tree_hash_changes_with_file_content(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            (root / "inference.yml").write_text("model: first", encoding="utf-8")
            original = TABLE.sha256_tree(root)
            (root / "inference.yml").write_text("model: second", encoding="utf-8")
            self.assertNotEqual(original, TABLE.sha256_tree(root))

    def test_merged_and_empty_cells(self):
        html = ("<table><tr><td rowspan='2'>Код</td><td colspan='2'>План</td>"
                "<td rowspan='2'>Статус</td></tr><tr><td>Кол-во</td><td>Сумма</td></tr>"
                "<tr><td>A-01</td><td>5</td><td>12,50</td><td>готово</td></tr>"
                "<tr><td>A-02</td><td></td><td>8,00</td><td>ожидание</td></tr></table>")
        result = TABLE.score_rows(html, [["A-01", "5", "12,50", "готово"],
                                          ["A-02", "", "8,00", "ожидание"]],
                                  {"План": [1, 2], "Код": [2, 1], "Статус": [2, 1]})
        self.assertEqual((8, 2, 3, 4), (result["exactCells"], result["exactRows"],
                                        result["exactSpans"], result["predictedDataColumns"]))

    def test_extra_column_is_a_structural_error(self):
        html = "<table><tr><td>1</td><td>2</td><td>3</td><td>4</td><td>extra</td></tr></table>"
        result = TABLE.score_rows(html, [["1", "2", "3", "4"]])
        self.assertEqual(4, result["exactCells"])
        self.assertEqual(0, result["exactRows"])
        self.assertEqual(5, result["predictedDataColumns"])


if __name__ == "__main__":
    unittest.main()
