"""Exact-cell and exact-row diagnostics for the predeclared numeric table corpus."""

import argparse
import json
import re
import unicodedata
from difflib import SequenceMatcher
from pathlib import Path


REVIEW_SUFFIX = "\nПроверено."


def tokens(text: str) -> list[str]:
    return re.findall(r"\S+", unicodedata.normalize("NFKC", text).casefold())


def score_page(page: dict, prediction: str) -> dict[str, int]:
    rows = page["numericTable"]["rows"]
    if not rows or any(len(row) != 4 or any(len(tokens(cell)) != 1 for cell in row) for row in rows):
        raise ValueError("Numeric table requires nonempty four-cell rows with one token per cell")

    reference = tokens(page["expectedText"])
    expected_rows = [tokens(" ".join(row)) for row in rows]
    positions = []
    cursor = 0
    for row in expected_rows:
        matches = [index for index in range(cursor, len(reference) - 3)
                   if reference[index:index + 4] == row]
        if len(matches) != 1:
            raise ValueError("Declared numeric row is missing or ambiguous in expectedText")
        positions.append(matches[0])
        cursor = matches[0] + 4

    predicted = tokens(prediction)
    aligned = {}
    for block in SequenceMatcher(None, reference, predicted, autojunk=False).get_matching_blocks():
        aligned.update((block.a + offset, block.b + offset) for offset in range(block.size))
    exact_cells = sum(index in aligned for start in positions for index in range(start, start + 4))
    exact_rows = sum(all(start + offset in aligned and aligned[start + offset] == aligned[start] + offset
                         for offset in range(4)) for start in positions if start in aligned)
    return {"exactCells": exact_cells, "referenceCells": len(rows) * 4,
            "exactRows": exact_rows, "referenceRows": len(rows)}


def score_export(page: dict, reviewed_text: str) -> dict[str, int]:
    if not reviewed_text.endswith(REVIEW_SUFFIX):
        raise ValueError("Reviewed export does not end in the smoke review suffix")
    return score_page(page, reviewed_text[:-len(REVIEW_SUFFIX)])


def main() -> None:
    parser = argparse.ArgumentParser(description="Score synthetic numeric table cells and rows")
    parser.add_argument("manifest", type=Path)
    parser.add_argument("source_file")
    parser.add_argument("reviewed_export", type=Path)
    args = parser.parse_args()
    manifest = json.loads(args.manifest.read_text(encoding="utf-8"))
    matches = [entry for entry in manifest["sources"] if entry["file"] == args.source_file]
    if len(matches) != 1 or len(matches[0]["pages"]) != 1:
        raise ValueError("Expected exactly one matching numeric table page")
    page = matches[0]["pages"][0]
    result = score_export(page, args.reviewed_export.read_text(encoding="utf-8"))
    print(json.dumps(result))


if __name__ == "__main__":
    main()
