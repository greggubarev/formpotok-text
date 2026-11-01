"""Isolated RapidOCR candidate for local text-quality comparisons."""

import argparse
import hashlib
import importlib.metadata
import json
import sys
from pathlib import Path
from tempfile import TemporaryDirectory


LOCK = Path(__file__).with_name("rapidocr-models.lock.json")
MODEL_NAMES = {
    "det": "ch_PP-OCRv5_det_mobile.onnx",
    "cls": "ch_ppocr_mobile_v2.0_cls_mobile.onnx",
    "eslav": "eslav_PP-OCRv5_rec_mobile.onnx",
    "cyrillic": "cyrillic_PP-OCRv5_rec_mobile.onnx",
}


def sha256_file(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as source:
        for block in iter(lambda: source.read(1024 * 1024), b""):
            digest.update(block)
    return digest.hexdigest()


def validate_models(root: Path, language: str) -> dict[str, Path]:
    lock = json.loads(LOCK.read_text(encoding="utf-8"))
    for package, key in (("rapidocr", "rapidocrVersion"), ("onnxruntime", "onnxruntimeVersion"),
                         ("pypdfium2", "pypdfium2Version")):
        if importlib.metadata.version(package) != lock[key]:
            raise ValueError(f"Unexpected {package} version")
    selected = {stage: root / MODEL_NAMES[stage] for stage in ("det", "cls", language)}
    for path in selected.values():
        if not path.is_file() or sha256_file(path) != lock["files"][path.name]:
            raise ValueError(f"Missing or changed model: {path.name}")
    return selected


def source_pages(source: Path):
    if source.suffix.lower() in (".png", ".jpg", ".jpeg"):
        yield source
        return
    if source.suffix.lower() != ".pdf":
        raise ValueError("Only PNG, JPEG and PDF sources are supported")
    import pypdfium2 as pdfium

    with TemporaryDirectory(prefix="formpotok-rapidocr-") as scratch:
        pdf = pdfium.PdfDocument(str(source))
        if len(pdf) > 100:
            raise ValueError("PDF exceeds 100 pages")
        for index in range(len(pdf)):
            page = pdf[index]
            path = Path(scratch) / f"page-{index + 1}.png"
            page.render(scale=2.0).to_pil().save(path)
            yield path


def main() -> None:
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8")
        sys.stderr.reconfigure(encoding="utf-8")
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", type=Path, required=True)
    parser.add_argument("--language", choices=("eslav", "cyrillic"), required=True)
    parser.add_argument("source", type=Path)
    args = parser.parse_args()
    selected = validate_models(args.model_root, args.language)
    if not args.source.is_file():
        raise ValueError("Source file not found")

    output = sys.stdout
    sys.stdout = sys.stderr
    try:
        from rapidocr import EngineType, LangDet, LangRec, ModelType, OCRVersion, RapidOCR

        engine = RapidOCR(params={
            "Det.engine_type": EngineType.ONNXRUNTIME,
            "Det.ocr_version": OCRVersion.PPOCRV5,
            "Det.lang_type": LangDet.CH,
            "Det.model_type": ModelType.MOBILE,
            "Det.model_path": str(selected["det"]),
            "Cls.engine_type": EngineType.ONNXRUNTIME,
            "Cls.model_path": str(selected["cls"]),
            "Rec.engine_type": EngineType.ONNXRUNTIME,
            "Rec.ocr_version": OCRVersion.PPOCRV5,
            "Rec.lang_type": LangRec.ESLAV if args.language == "eslav" else LangRec.CYRILLIC,
            "Rec.model_type": ModelType.MOBILE,
            "Rec.model_path": str(selected[args.language]),
        })
        pages = []
        for page in source_pages(args.source):
            result = engine(str(page))
            pages.append("\n".join(result.txts or ()))
    finally:
        sys.stdout = output
    output.write("\f".join(pages))


if __name__ == "__main__":
    main()
