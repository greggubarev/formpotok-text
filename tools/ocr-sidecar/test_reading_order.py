import unittest

from reading_order import reorder_four_column_numeric_table, reorder_numeric_table, reorder_two_columns


def block(text, x, y, width=35):
    return {"Text": text, "Polygon": [
        {"X": x, "Y": y}, {"X": x + width, "Y": y},
        {"X": x + width, "Y": y + 5}, {"X": x, "Y": y + 5}]}


class ReadingOrderTests(unittest.TestCase):
    def test_numeric_four_column_table_with_column_offsets_becomes_row_major(self):
        rows = [[block(str(row), 100, 30 + row * 20),
                 block(f"{row + 2} шт.", 300, 33 + row * 20),
                 block(f"{row + 3},00", 500, 28 + row * 20),
                 block(f"{(row + 2) * (row + 3)},00", 700, 34 + row * 20)]
                for row in range(7)]
        title = block("ВЕДОМОСТЬ", 5, 0)
        total = block("ИТОГО 100,00", 600, 210)
        incoming = [title, *[row[column] for column in range(4) for row in rows], total]
        ordered, detected = reorder_four_column_numeric_table(
            {"Width": 850, "Height": 240, "TextBlocks": incoming})
        self.assertTrue(detected)
        self.assertEqual(ordered, [title, *[cell for row in rows for cell in row], total])

    def test_numeric_four_column_table_with_perspective_drift_becomes_row_major(self):
        rows = [[block(str(row), 100, 30 + row * 20),
                 block(f"{row + 2} шт.", 300, 33 + row * 20),
                 block(f"{row + 3},00", 500, 28 + row * 20),
                 block(f"{(row + 2) * (row + 3)},00", 700, 18 + row * 21)]
                for row in range(12)]
        incoming = [row[column] for column in range(4) for row in rows]
        ordered, detected = reorder_four_column_numeric_table(
            {"Width": 850, "Height": 290, "TextBlocks": incoming})
        self.assertTrue(detected)
        self.assertEqual(ordered, [cell for row in rows for cell in row])

    def test_four_column_table_with_missing_cell_remains_unchanged(self):
        blocks = [block(str(row + column), 100 + column * 200, 30 + row * 20)
                  for column in range(4) for row in range(7)]
        blocks.pop()
        ordered, detected = reorder_four_column_numeric_table(
            {"Width": 850, "Height": 240, "TextBlocks": blocks})
        self.assertFalse(detected)
        self.assertEqual(ordered, blocks)

    def test_four_editorial_columns_remain_unchanged(self):
        blocks = [block(f"Текст {column}-{row}", 100 + column * 200, 30 + row * 20)
                  for column in range(4) for row in range(7)]
        for item in blocks:
            item["Text"] = item["Text"].split()[0]
        self.assertFalse(reorder_four_column_numeric_table(
            {"Width": 850, "Height": 240, "TextBlocks": blocks})[1])

    def test_numeric_three_column_table_becomes_row_major(self):
        blocks = [block("РЕЕСТР", 5, 1, 28)]
        for row in range(7):
            blocks.extend((block(f"Папка {row}", 5, 20 + row * 12, 20),
                           block(f"{row + 2} шт.", 45, 21 + row * 12, 16),
                           block(f"{(row + 2) * 100} руб.", 85, 20 + row * 12, 20)))
        incoming = [blocks[0], *blocks[1::3], *blocks[2::3], *blocks[3::3]]
        ordered, changed = reorder_numeric_table({"Width": 110, "Height": 120, "TextBlocks": incoming})
        self.assertTrue(changed)
        self.assertEqual(ordered, blocks)

    def test_already_row_major_numeric_table_is_not_reclassified_as_columns(self):
        blocks = [block("РЕЕСТР", 5, 1, 28)]
        for row in range(7):
            blocks.extend((block(f"Папка {row}", 5, 20 + row * 12, 20),
                           block(f"{row + 2} шт.", 45, 20 + row * 12, 16),
                           block(f"{(row + 2) * 100} руб.", 85, 20 + row * 12, 20)))
        ordered, detected = reorder_numeric_table({"Width": 110, "Height": 120, "TextBlocks": blocks})
        self.assertTrue(detected)
        self.assertEqual(ordered, blocks)

    def test_three_editorial_columns_remain_unchanged(self):
        blocks = [block(f"Текст {chr(65 + column)}{chr(65 + row)}", 5 + column * 40, 20 + row * 12, 20)
                  for column in range(3) for row in range(7)]
        ordered, changed = reorder_numeric_table({"Width": 110, "Height": 120, "TextBlocks": blocks})
        self.assertFalse(changed)
        self.assertEqual(ordered, blocks)

    def test_numeric_table_with_missing_cell_remains_unchanged(self):
        blocks = [block(f"Ячейка {column}-{row}", 5 + column * 40, 20 + row * 12, 20)
                  for column in range(3) for row in range(7)]
        blocks.pop()
        ordered, changed = reorder_numeric_table({"Width": 110, "Height": 120, "TextBlocks": blocks})
        self.assertFalse(changed)
        self.assertEqual(ordered, blocks)

    def test_interleaved_columns_become_column_major(self):
        blocks = [block(f"L{i}", 5, i * 9) if side == "L" else block(f"R{i}", 65, i * 9)
                  for i in range(8) for side in ("L", "R")]
        ordered, changed = reorder_two_columns({"Width": 110, "Height": 90, "TextBlocks": blocks})
        self.assertTrue(changed)
        self.assertEqual([item["Text"] for item in ordered],
                         [f"L{i}" for i in range(8)] + [f"R{i}" for i in range(8)])

    def test_middle_full_width_block_rejects_reordering(self):
        blocks = [block(f"L{i}", 5, i * 9) if side == "L" else block(f"R{i}", 65, i * 9)
                  for i in range(8) for side in ("L", "R")]
        blocks.insert(8, block("bridge", 15, 34, 80))
        ordered, changed = reorder_two_columns({"Width": 110, "Height": 90, "TextBlocks": blocks})
        self.assertFalse(changed)
        self.assertEqual(ordered, blocks)

    def test_single_column_remains_unchanged(self):
        blocks = [block(str(i), 10, i * 8, 75) for i in range(15)]
        ordered, changed = reorder_two_columns({"Width": 110, "Height": 130, "TextBlocks": blocks})
        self.assertFalse(changed)
        self.assertEqual(ordered, blocks)

    def test_missing_geometry_fails_closed(self):
        blocks = [block(str(i), 5, i * 8) for i in range(12)]
        blocks[0]["Polygon"] = []
        self.assertFalse(reorder_two_columns({"Width": 110, "Height": 130, "TextBlocks": blocks})[1])


if __name__ == "__main__":
    unittest.main()
