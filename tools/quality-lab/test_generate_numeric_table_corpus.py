"""Checks for the numeric table diagnostic corpus."""

import hashlib
import json
import tempfile
import unittest
from pathlib import Path

from PIL import Image, ImageChops

from generate_numeric_table_corpus import ROW_COUNTS, VARIANTS, make_corpus, money, table_reference, table_rows


class NumericTableCorpusTests(unittest.TestCase):
    def test_known_complete_gold(self):
        lines, marker = table_reference(4)
        self.assertEqual(lines, [
            "ВЕДОМОСТЬ № 404",
            "№ Кол-во Цена Сумма",
            "1 2 12,87 25,74",
            "2 3 13,24 39,72",
            "3 4 13,61 54,44",
            "4 5 13,98 69,90",
            "ИТОГО 189,80",
        ])
        self.assertEqual(marker, lines[-1])
        self.assertEqual(money(100), "1,00")

    def test_manifest_and_reproducible_images(self):
        with tempfile.TemporaryDirectory() as first, tempfile.TemporaryDirectory() as second:
            first_root, second_root = Path(first), Path(second)
            manifest = make_corpus(first_root)
            self.assertEqual(manifest, make_corpus(second_root))
            self.assertEqual(
                (first_root / "corpus.reference.json").read_bytes(),
                (second_root / "corpus.reference.json").read_bytes(),
            )
            self.assertEqual(len(manifest["sources"]), len(ROW_COUNTS) * len(VARIANTS))
            self.assertEqual({entry["sourceGroup"] for entry in manifest["sources"]}, {"numeric-table"})
            self.assertEqual(manifest["purpose"], "diagnostic-only")
            self.assertFalse(manifest["countsTowardRealHoldoutQuota"])
            self.assertFalse(manifest["countsTowardTrainingQuota"])
            self.assertEqual(json.loads((first_root / "corpus.reference.json").read_text(encoding="utf-8")), manifest)

            for row_count in ROW_COUNTS:
                group = [entry for entry in manifest["sources"] if entry["file"].startswith(f"numeric-table-{row_count:02d}-rows-")]
                self.assertEqual({entry["severity"] for entry in group}, set(VARIANTS))
                images = []
                for entry in group:
                    path = first_root / entry["file"]
                    payload = path.read_bytes()
                    self.assertEqual(payload, (second_root / entry["file"]).read_bytes())
                    self.assertEqual(entry["sha256"], hashlib.sha256(payload).hexdigest())
                    self.assertEqual(entry["byteLength"], len(payload))
                    self.assertEqual(entry["pageCount"], 1)
                    self.assertEqual(len(entry["pages"]), 1)
                    page = entry["pages"][0]
                    lines, marker = table_reference(row_count)
                    self.assertEqual(page, {"pageNumber": 1, "expectedText": "\n".join(lines),
                                            "marker": marker, "numericTable": {"rows": table_rows(row_count)}})
                    self.assertEqual(len(page["expectedText"].splitlines()), row_count + 3)
                    with Image.open(path) as image:
                        self.assertEqual(image.size, (1500, 280 + row_count * 54))
                        self.assertIsNotNone(ImageChops.difference(image, Image.new("RGB", image.size, "white")).getbbox())
                        images.append(image.copy())
                self.assertEqual(len({entry["sha256"] for entry in group}), len(VARIANTS))
                self.assertTrue(any(ImageChops.difference(images[0], image).getbbox() for image in images[1:]))


if __name__ == "__main__":
    unittest.main()
