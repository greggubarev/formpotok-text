"""Validate and summarize the current package versus isolated RapidOCR candidates."""

import argparse
import json
from collections import Counter
from pathlib import Path


def summarize(current: dict, candidate: dict) -> dict:
    if current["scope"] != "synthetic-diagnostic-only" or candidate["scope"] != "synthetic-diagnostic-only":
        raise ValueError("Reports are not synthetic diagnostic runs")
    if current["corpusManifestSha256"] != candidate["corpusSha256"]:
        raise ValueError("Corpus manifests differ")
    baseline = {row["sourceSha256"]: row for row in current["results"]}
    if len(baseline) != len(current["results"]):
        raise ValueError("Duplicate baseline source")
    count = len(baseline)
    if count == 0 or current["completed"] != count or current["failed"] != 0:
        raise ValueError("Baseline run is incomplete")
    output = {"corpusSha256": candidate["corpusSha256"], "cases": count, "baseline": {
        "cer": current["characterErrorRate"], "wer": current["wordErrorRate"]}, "candidates": {}}
    for name in ("eslav", "cyrillic"):
        rows = [row for row in candidate["cases"] if row["candidate"] == name]
        matches = Counter(row["sourceSha256"] for row in rows)
        if len(rows) != count or set(matches) != set(baseline) or any(value != 1 for value in matches.values()):
            raise ValueError(f"Candidate {name} does not cover the same sources exactly once")
        if any(row["status"] != "ok" for row in rows):
            raise ValueError(f"Candidate {name} has incomplete cases")
        if any(baseline[row["sourceSha256"]]["referenceCharacters"] != row["referenceCharacters"]
               or baseline[row["sourceSha256"]]["referenceWords"] != row["referenceWords"] for row in rows):
            raise ValueError(f"Candidate {name} uses different reference normalization")
        characters = sum(row["referenceCharacters"] for row in rows)
        words = sum(row["referenceWords"] for row in rows)
        output["candidates"][name] = {
            "cer": sum(row["characterEdits"] for row in rows) / characters,
            "wer": sum(row["wordEdits"] for row in rows) / words,
            "improvedPages": sum(row["characterEdits"] < baseline[row["sourceSha256"]]["characterEdits"] for row in rows),
            "regressedPages": sum(row["characterEdits"] > baseline[row["sourceSha256"]]["characterEdits"] for row in rows),
            "unchangedPages": sum(row["characterEdits"] == baseline[row["sourceSha256"]]["characterEdits"] for row in rows),
        }
    return output


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--baseline", type=Path, required=True)
    parser.add_argument("--candidate", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    result = summarize(json.loads(args.baseline.read_text(encoding="utf-8")),
                       json.loads(args.candidate.read_text(encoding="utf-8")))
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False))


if __name__ == "__main__":
    main()
