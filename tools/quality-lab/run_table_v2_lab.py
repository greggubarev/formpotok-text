"""Measure a local Table Recognition v2 pipeline on generated numeric grids."""

import argparse
import hashlib
import importlib.metadata
import json
import os
import time
from html.parser import HTMLParser
from pathlib import Path


MODELS = ("PP-LCNet_x1_0_table_cls", "SLANeXt_wired", "SLANet_plus",
          "RT-DETR-L_wired_table_cell_det", "RT-DETR-L_wireless_table_cell_det",
          "PP-OCRv5_mobile_det", "eslav_PP-OCRv5_mobile_rec",
          "PP-LCNet_x1_0_doc_ori")


def sha256_tree(root: Path) -> str:
    digest = hashlib.sha256()
    for path in sorted(item for item in root.rglob("*") if item.is_file()):
        digest.update(path.relative_to(root).as_posix().encode("utf-8") + b"\0")
        with path.open("rb") as source:
            for block in iter(lambda: source.read(1024 * 1024), b""):
                digest.update(block)
    return digest.hexdigest()


class TableHtml(HTMLParser):
    def __init__(self):
        super().__init__()
        self.rows = []
        self.cell = None
        self.colspan = 1
        self.rowspan = 1

    def handle_starttag(self, tag, attrs):
        if tag == "tr":
            self.rows.append([])
        elif tag in ("td", "th"):
            self.cell = []
            self.colspan = int(dict(attrs).get("colspan", "1"))
            self.rowspan = int(dict(attrs).get("rowspan", "1"))

    def handle_data(self, data):
        if self.cell is not None:
            self.cell.append(data)

    def handle_endtag(self, tag):
        if tag in ("td", "th") and self.cell is not None and self.rows:
            self.rows[-1].append({"text": " ".join("".join(self.cell).split()),
                                  "colspan": self.colspan, "rowspan": self.rowspan})
            self.cell = None


def score_rows(html: str, expected: list[list[str]], spans: dict | None = None) -> dict:
    parser = TableHtml()
    parser.feed(html)
    expected_ids = {row[0] for row in expected}
    predicted = {row[0]["text"]: row for row in parser.rows
                 if row and row[0]["text"] in expected_ids}
    exact_cells = exact_rows = 0
    for row in expected:
        actual = predicted.get(row[0], [])
        exact_cells += sum(index < len(actual) and actual[index]["text"] == cell
                           for index, cell in enumerate(row))
        exact_rows += len(actual) == len(row) and all(
            actual[index]["text"] == cell and actual[index]["colspan"] == 1
            for index, cell in enumerate(row))
    spans = spans or {}
    predicted_spans = {cell["text"]: (cell["rowspan"], cell["colspan"])
                       for row in parser.rows for cell in row if cell["text"] in spans}
    return {"exactCells": exact_cells, "referenceCells": sum(map(len, expected)),
            "exactRows": exact_rows, "referenceRows": len(expected),
            "exactSpans": sum(predicted_spans.get(key) == tuple(span) for key, span in spans.items()),
            "referenceSpans": len(spans),
            "predictedDataColumns": max((sum(cell["colspan"] for cell in row)
                                         for row in predicted.values()), default=0),
            "predictedRows": len(parser.rows)}


def run(args) -> dict:
    os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"] = "True"
    root = args.model_root.resolve()
    for name in MODELS:
        if not (root / name / "inference.yml").is_file():
            raise ValueError(f"Missing local model: {name}")
    model_hashes = {name: sha256_tree(root / name) for name in MODELS}
    from paddleocr import TableRecognitionPipelineV2

    pipeline = TableRecognitionPipelineV2(
        device="cpu", use_doc_orientation_classify=False, use_doc_unwarping=False,
        use_layout_detection=False,
        table_classification_model_dir=str(root / "PP-LCNet_x1_0_table_cls"),
        wired_table_structure_recognition_model_name="SLANeXt_wired",
        wired_table_structure_recognition_model_dir=str(root / "SLANeXt_wired"),
        wireless_table_structure_recognition_model_name="SLANet_plus",
        wireless_table_structure_recognition_model_dir=str(root / "SLANet_plus"),
        wired_table_cells_detection_model_dir=str(root / "RT-DETR-L_wired_table_cell_det"),
        wireless_table_cells_detection_model_dir=str(root / "RT-DETR-L_wireless_table_cell_det"),
        text_detection_model_name="PP-OCRv5_mobile_det",
        text_detection_model_dir=str(root / "PP-OCRv5_mobile_det"),
        text_recognition_model_name="eslav_PP-OCRv5_mobile_rec",
        text_recognition_model_dir=str(root / "eslav_PP-OCRv5_mobile_rec"),
        doc_orientation_classify_model_dir=str(root / "PP-LCNet_x1_0_doc_ori"),
    )
    corpus = args.corpus.resolve()
    manifest_path = corpus / "corpus.reference.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    chosen = {item.strip() for item in args.files.split(",")}
    cases = []
    for entry in manifest["sources"]:
        if entry["file"] not in chosen:
            continue
        source = corpus / entry["file"]
        source_hash = hashlib.sha256(source.read_bytes()).hexdigest()
        if source_hash != entry["sha256"]:
            raise ValueError(f"Source changed: {source.name}")
        started = time.perf_counter()
        result = next(iter(pipeline.predict(str(source))))
        elapsed = time.perf_counter() - started
        tables = result.json["res"].get("table_res_list", [])
        if len(tables) != 1:
            raise ValueError(f"Expected one table in {source.name}, got {len(tables)}")
        page = entry["pages"][0]
        grid = page.get("tableGrid")
        score = score_rows(tables[0]["pred_html"], grid["dataRows"] if grid else page["numericTable"]["rows"],
                           grid["spans"] if grid else None)
        cases.append({"file": source.name, "sourceSha256": source_hash,
                      "durationSeconds": round(elapsed, 3), **score})
    if len(cases) != len(chosen):
        raise ValueError("One or more requested cases were absent")
    return {"scope": "synthetic-diagnostic-only", "corpusSha256": hashlib.sha256(
        manifest_path.read_bytes()).hexdigest(), "paddleocrVersion": importlib.metadata.version("paddleocr"),
        "paddlexVersion": importlib.metadata.version("paddlex"),
        "modelSha256": model_hashes, "cases": cases}


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--corpus", type=Path, required=True)
    parser.add_argument("--model-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--files", default="numeric-table-04-rows-clean.png,numeric-table-04-rows-skew.png,numeric-table-12-rows-clean.png")
    options = parser.parse_args()
    report = run(options)
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(json.dumps(report, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(report, ensure_ascii=False))
