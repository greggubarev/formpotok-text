import importlib.util
import json
import tempfile
import unittest
from pathlib import Path
from unittest.mock import patch


ROOT = Path(__file__).resolve().parent


def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


ADAPTER = load("rapidocr_candidate", ROOT / "rapidocr_candidate.py")
COMPARE = load("compare_rapidocr", ROOT / "compare_rapidocr.py")
SUMMARY = load("summarize_rapidocr_ab", ROOT / "summarize_rapidocr_ab.py")


class ExternalCandidateTests(unittest.TestCase):
    def test_quality_normalization_and_edits(self):
        self.assertEqual(COMPARE.normalized("  ЁЖ\n А  "), "ёж а")
        self.assertEqual(COMPARE.normalized("① ﬁ"), "1 fi")
        self.assertEqual(COMPARE.edit_distance("абв", "авв"), 1)
        self.assertEqual(COMPARE.edit_distance(["акт", "42"], ["акт", "43"]), 1)

    def test_model_hashes_are_required(self):
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            hashes = {}
            for name in ADAPTER.MODEL_NAMES.values():
                path = root / name
                path.write_bytes(name.encode("ascii"))
                hashes[name] = ADAPTER.sha256_file(path)
            lock = root / "lock.json"
            lock.write_text(json.dumps({"rapidocrVersion": "3.10.0", "onnxruntimeVersion": "1.23.2",
                                        "pypdfium2Version": "5.14.0", "files": hashes}), encoding="utf-8")
            versions = {"rapidocr": "3.10.0", "onnxruntime": "1.23.2", "pypdfium2": "5.14.0"}
            with patch.object(ADAPTER, "LOCK", lock), patch.object(ADAPTER.importlib.metadata, "version", side_effect=versions.get):
                self.assertEqual(len(ADAPTER.validate_models(root, "eslav")), 3)
                (root / ADAPTER.MODEL_NAMES["eslav"]).write_bytes(b"changed")
                with self.assertRaisesRegex(ValueError, "changed model"):
                    ADAPTER.validate_models(root, "eslav")

    def test_ab_summary_rejects_unmatched_sources(self):
        current = {"scope": "synthetic-diagnostic-only", "corpusManifestSha256": "a", "completed": 1,
                   "failed": 0, "characterErrorRate": 0, "wordErrorRate": 0,
                   "results": [{"sourceSha256": "x", "characterEdits": 0, "referenceCharacters": 2,
                                "referenceWords": 1}]}
        row = {"sourceSha256": "x", "status": "ok", "characterEdits": 1,
               "referenceCharacters": 2, "referenceWords": 1, "wordEdits": 1}
        candidate = {"scope": "synthetic-diagnostic-only", "corpusSha256": "a",
                     "cases": [{**row, "candidate": "eslav"}, {**row, "candidate": "cyrillic"}]}
        result = SUMMARY.summarize(current, candidate)
        self.assertEqual(result["candidates"]["eslav"]["regressedPages"], 1)
        candidate["cases"][0]["sourceSha256"] = "y"
        with self.assertRaisesRegex(ValueError, "same sources"):
            SUMMARY.summarize(current, candidate)


if __name__ == "__main__":
    unittest.main()
