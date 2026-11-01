"""Score an isolated Docling text export against a synthetic layout corpus."""

import argparse
import hashlib
import json
from pathlib import Path

from compare_rapidocr import edit_distance, normalized


def score(corpus: Path, outputs: Path) -> dict:
    manifest_path = corpus / "corpus.reference.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    cases = []
    for entry in manifest["sources"]:
        source = corpus / entry["file"]
        if hashlib.sha256(source.read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError(f"Source differs from manifest: {entry['file']}")
        exported = outputs / f"{source.stem}.txt"
        if not exported.is_file():
            raise ValueError(f"Missing Docling result: {exported.name}")
        actual = normalized(exported.read_text(encoding="utf-8"))
        expected_lines = [normalized(line) for line in entry["pages"][0]["expectedText"].splitlines()]
        expected = normalized(entry["pages"][0]["expectedText"])
        cases.append({"sourceSha256": entry["sha256"], "sourceGroup": entry["sourceGroup"],
                      "severity": entry["severity"], "characterEdits": edit_distance(actual, expected),
                      "referenceCharacters": len(expected),
                      "wordEdits": edit_distance(actual.split(), expected.split()),
                      "referenceWords": len(expected.split()),
                      "exactLinesPresent": sum(line in actual for line in expected_lines),
                      "referenceLines": len(expected_lines)})
    return {"scope": "synthetic-diagnostic-only", "corpusSha256": hashlib.sha256(
        manifest_path.read_bytes()).hexdigest(), "cases": cases,
        "cer": sum(item["characterEdits"] for item in cases) / sum(item["referenceCharacters"] for item in cases),
        "wer": sum(item["wordEdits"] for item in cases) / sum(item["referenceWords"] for item in cases),
        "exactLineRecall": sum(item["exactLinesPresent"] for item in cases) /
                           sum(item["referenceLines"] for item in cases)}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--outputs", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = score(args.corpus.resolve(), args.outputs.resolve())
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps({key: value for key, value in result.items() if key != "cases"}, ensure_ascii=False))
