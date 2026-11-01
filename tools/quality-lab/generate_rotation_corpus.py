"""Make paired synthetic page rotations for isolated preprocessing diagnostics."""

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image


FAMILIES = ("application", "notice")
VARIANTS = {"clean": 0, "rotate-90": 90, "rotate-180": 180,
            "rotate-270": 270, "skew-2": 2}


def make_corpus(source_root: Path, output: Path) -> dict:
    source_root = source_root.resolve()
    source_manifest = json.loads((source_root / "corpus.reference.json").read_text(encoding="utf-8"))
    references = {entry["sourceGroup"]: entry for entry in source_manifest["sources"]
                  if entry["severity"] == "clean" and entry["sourceGroup"] in FAMILIES}
    if set(references) != set(FAMILIES):
        raise ValueError("Source corpus lacks required clean templates")
    output.mkdir(parents=True, exist_ok=True)
    cases = []
    for family in FAMILIES:
        entry = references[family]
        source = source_root / entry["file"]
        if hashlib.sha256(source.read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError("Source differs from manifest")
        with Image.open(source) as original:
            for variant, degrees in VARIANTS.items():
                page = original.convert("RGB")
                if degrees:
                    page = page.rotate(degrees, expand=degrees != 2,
                                       resample=Image.Resampling.BICUBIC, fillcolor="white")
                name = f"{family}-{variant}.png"
                path = output / name
                page.save(path)
                cases.append({"file": name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
                              "byteLength": path.stat().st_size, "pageCount": 1,
                              "sourceGroup": family, "severity": variant,
                              "pages": entry["pages"]})
    manifest = {"schemaVersion": 1, "purpose": "diagnostic-only",
                "dataPartition": "synthetic-diagnostic", "countsTowardRealHoldoutQuota": False,
                "countsTowardTrainingQuota": False, "sources": cases}
    (output / "corpus.reference.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("source_root", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(f"Generated {len(make_corpus(args.source_root, args.output)['sources'])} pages")
