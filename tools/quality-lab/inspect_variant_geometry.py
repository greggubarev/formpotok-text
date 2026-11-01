"""Inspect OCR line geometry for one local image without saving text."""

import argparse
import os
import sys
from pathlib import Path


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("models", type=Path)
    args = parser.parse_args()
    os.environ["FORMPOTOK_OCR_PADDLE_CACHE_HOME"] = str(args.models.resolve())
    sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "ocr-sidecar"))
    from main import build_paddle_page_result, create_paddle_ocr

    root = args.models.resolve() / "official_models"
    detector = str(root / "PP-OCRv5_mobile_det")
    recognizer = str(root / "eslav_PP-OCRv5_mobile_rec")
    engine = create_paddle_ocr("mobile-cpu", "ru", detector, recognizer, "eslav_PP-OCRv5_mobile_rec", offline=True)
    result = build_paddle_page_result(engine, args.source.resolve(), 1, "mobile-cpu", detector, recognizer,
                                      "eslav_PP-OCRv5_mobile_rec")
    blocks = result["page"]["TextBlocks"]
    horizontal = 0
    for block in blocks:
        polygon = block["Polygon"]
        width = max(point["X"] for point in polygon) - min(point["X"] for point in polygon)
        height = max(point["Y"] for point in polygon) - min(point["Y"] for point in polygon)
        horizontal += width > height * 1.5
    print({"blocks": len(blocks), "horizontalRatio": round(horizontal / max(len(blocks), 1), 3),
           "score": result["metrics"]["score"]})
    raw = list(engine.predict(str(args.source.resolve())))[0]
    preprocessing = raw.get("doc_preprocessor_res") or {}
    print({"preprocessorKeys": list(preprocessing.keys()), "angle": preprocessing.get("angle")})


if __name__ == "__main__":
    main()
