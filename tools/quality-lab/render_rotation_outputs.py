"""Render OCRmyPDF output back to images for an unchanged recognizer A/B."""

import argparse
import hashlib
import json
from pathlib import Path

import pypdfium2 as pdfium


def render_corpus(reference_root: Path, pdf_root: Path, output: Path) -> dict:
    reference = json.loads((reference_root / "corpus.reference.json").read_text(encoding="utf-8"))
    output.mkdir(parents=True, exist_ok=True)
    cases = []
    for entry in reference["sources"]:
        source = reference_root / entry["file"]
        if hashlib.sha256(source.read_bytes()).hexdigest() != entry["sha256"]:
            raise ValueError("Source differs from manifest")
        pdf_path = pdf_root / f"{source.stem}-p10.pdf"
        if not pdf_path.is_file():
            raise ValueError(f"Missing processed PDF: {pdf_path.name}")
        pdf = pdfium.PdfDocument(str(pdf_path))
        if len(pdf) != 1:
            raise ValueError(f"Expected one page: {pdf_path.name}")
        output_path = output / entry["file"]
        pdf[0].render(scale=150 / 72).to_pil().convert("RGB").save(output_path)
        cases.append({**entry, "sha256": hashlib.sha256(output_path.read_bytes()).hexdigest(),
                      "byteLength": output_path.stat().st_size})
    manifest = {**reference, "sources": cases}
    (output / "corpus.reference.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("reference_root", type=Path)
    parser.add_argument("pdf_root", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(f"Rendered {len(render_corpus(args.reference_root, args.pdf_root, args.output)['sources'])} pages")
