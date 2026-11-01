"""Create a small table with known empty and merged cells."""

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


def make_corpus(output: Path) -> dict:
    output.mkdir(parents=True, exist_ok=True)
    image = Image.new("RGB", (1250, 640), "white")
    draw = ImageDraw.Draw(image)
    font = ImageFont.truetype("C:/Windows/Fonts/arial.ttf", 34)
    title = ImageFont.truetype("C:/Windows/Fonts/arialbd.ttf", 42)
    draw.text((75, 45), "СВОДНАЯ ТАБЛИЦА", font=title, fill="black")
    xs = (75, 285, 515, 805, 1175)
    ys = (140, 220, 300, 385, 470, 555)
    for y in (ys[0], ys[2], ys[3], ys[4], ys[5]):
        draw.line((xs[0], y, xs[-1], y), fill="black", width=3)
    draw.line((xs[1], ys[1], xs[3], ys[1]), fill="black", width=3)
    for x in (xs[0], xs[1], xs[3], xs[4]):
        draw.line((x, ys[0], x, ys[-1]), fill="black", width=3)
    draw.line((xs[2], ys[1], xs[2], ys[-1]), fill="black", width=3)
    labels = (("Код", 100, 195), ("План", 535, 160), ("Статус", 855, 195),
              ("Кол-во", 310, 240), ("Сумма", 555, 240),
              ("A-01", 100, 323), ("5", 310, 323), ("12,50", 555, 323), ("готово", 855, 323),
              ("A-02", 100, 408), ("8,00", 555, 408), ("ожидание", 855, 408),
              ("A-03", 100, 493), ("2", 310, 493), ("проверка", 855, 493))
    for value, x, y in labels:
        draw.text((x, y), value, font=font, fill="black")
    cases = []
    for variant in ("clean", "skew-2", "soft-blur"):
        changed = image
        if variant == "skew-2":
            changed = image.rotate(2, resample=Image.Resampling.BICUBIC, fillcolor="white")
        elif variant == "soft-blur":
            changed = image.filter(ImageFilter.GaussianBlur(1))
        path = output / f"merged-empty-{variant}.png"
        changed.save(path)
        cases.append({"file": path.name, "sha256": hashlib.sha256(path.read_bytes()).hexdigest(),
             "byteLength": path.stat().st_size, "pageCount": 1,
             "sourceGroup": "merged-empty-table", "severity": variant,
             "pages": [{"pageNumber": 1,
                        "expectedText": "СВОДНАЯ ТАБЛИЦА\nКод План Статус\nКол-во Сумма\nA-01 5 12,50 готово\nA-02 8,00 ожидание\nA-03 2 проверка",
                        "marker": "A-03 2 проверка",
                        "tableGrid": {"columns": 4, "dataRows": [
                            ["A-01", "5", "12,50", "готово"],
                            ["A-02", "", "8,00", "ожидание"],
                            ["A-03", "2", "", "проверка"]],
                            "spans": {"План": [1, 2], "Код": [2, 1], "Статус": [2, 1]}}}]})
    manifest = {"schemaVersion": 1, "purpose": "diagnostic-only",
                "dataPartition": "synthetic-diagnostic", "countsTowardRealHoldoutQuota": False,
                "countsTowardTrainingQuota": False, "sources": cases}
    (output / "corpus.reference.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    print(f"Generated {len(make_corpus(args.output)['sources'])} table")
