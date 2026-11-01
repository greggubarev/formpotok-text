"""Compare isolated RapidOCR variants on a frozen synthetic corpus."""

import argparse
import hashlib
import json
import os
import subprocess
import sys
import time
import unicodedata
from pathlib import Path


ADAPTER = Path(__file__).with_name("rapidocr_candidate.py")
MODEL_LOCK = Path(__file__).with_name("rapidocr-models.lock.json")


def normalized(text: str) -> str:
    return " ".join(unicodedata.normalize("NFKC", text).lower().split())


def edit_distance(left, right) -> int:
    previous = list(range(len(right) + 1))
    for index, item in enumerate(left, start=1):
        current = [index]
        for column, other in enumerate(right, start=1):
            current.append(min(current[-1] + 1, previous[column] + 1,
                               previous[column - 1] + (item != other)))
        previous = current
    return previous[-1]


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def run_case(source: Path, reference: str, model_root: Path, language: str, timeout: int) -> dict:
    started = time.perf_counter()
    try:
        result = subprocess.run(
            [sys.executable, str(ADAPTER), "--model-root", str(model_root),
             "--language", language, str(source)],
            capture_output=True, encoding="utf-8", timeout=timeout, check=False,
            env={**os.environ, "PYTHONIOENCODING": "utf-8"},
        )
        if result.returncode != 0:
            return {"status": "error", "exitCode": result.returncode,
                    "durationSeconds": round(time.perf_counter() - started, 3)}
        actual = normalized(result.stdout)
        expected = normalized(reference)
        return {
            "status": "ok",
            "characterEdits": edit_distance(actual, expected),
            "referenceCharacters": len(expected),
            "wordEdits": edit_distance(actual.split(), expected.split()),
            "referenceWords": len(expected.split()),
            "durationSeconds": round(time.perf_counter() - started, 3),
        }
    except subprocess.TimeoutExpired:
        return {"status": "timeout", "durationSeconds": round(time.perf_counter() - started, 3)}


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--model-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--timeout", type=int, default=120)
    args = parser.parse_args()
    if args.timeout < 1 or args.timeout > 3600:
        raise ValueError("Timeout outside allowed range")
    corpus = args.corpus.resolve()
    manifest = corpus / "corpus.reference.json"
    payload = json.loads(manifest.read_text(encoding="utf-8"))
    if payload["schemaVersion"] != 1 or len(payload["sources"]) > 1000:
        raise ValueError("Unsupported corpus")
    rows = []
    for item in payload["sources"]:
        source = (corpus / item["file"]).resolve()
        if not source.is_relative_to(corpus) or sha256_file(source) != item["sha256"]:
            raise ValueError("Corpus source differs from manifest")
        reference = "\f".join(page["expectedText"] for page in item["pages"])
        for language in ("eslav", "cyrillic"):
            result = run_case(source, reference, args.model_root.resolve(), language, args.timeout)
            rows.append({"sourceSha256": item["sha256"], "sourceGroup": item.get("sourceGroup"),
                         "severity": item.get("severity"), "candidate": language, **result})
    aggregates = {}
    for language in ("eslav", "cyrillic"):
        group = [row for row in rows if row["candidate"] == language]
        completed = [row for row in group if row["status"] == "ok"]
        characters = sum(row["referenceCharacters"] for row in completed)
        words = sum(row["referenceWords"] for row in completed)
        durations = sorted(row["durationSeconds"] for row in completed)
        aggregates[language] = {
            "cases": len(group), "completed": len(completed),
            "cer": sum(row["characterEdits"] for row in completed) / characters if len(completed) == len(group) and characters else None,
            "wer": sum(row["wordEdits"] for row in completed) / words if len(completed) == len(group) and words else None,
            "p95Seconds": durations[max(0, (95 * len(durations) + 99) // 100 - 1)] if durations else None,
        }
    report = {
        "scope": "synthetic-diagnostic-only", "corpusSha256": sha256_file(manifest),
        "adapterSha256": sha256_file(ADAPTER), "modelLockSha256": sha256_file(MODEL_LOCK),
        "candidates": aggregates, "cases": rows,
    }
    args.output.parent.mkdir(parents=True, exist_ok=True)
    args.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(aggregates, ensure_ascii=False))


if __name__ == "__main__":
    main()
