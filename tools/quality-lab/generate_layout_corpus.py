"""Create synthetic Russian pages that stress reading order and small print."""

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


VARIANTS = ("clean", "soft-blur", "skew", "low-resolution")
FAMILIES = ("two-column", "table", "small-print")


def make_corpus(directory: Path) -> dict:
    directory.mkdir(parents=True, exist_ok=True)
    fonts = Path("C:/Windows/Fonts")
    entries = []
    for family in FAMILIES:
        for variant_index, variant in enumerate(VARIANTS):
            number = 201 + FAMILIES.index(family) * 4 + variant_index
            image = Image.new("RGB", (1600, 950), "white")
            draw = ImageDraw.Draw(image)
            title_font = ImageFont.truetype(str(fonts / "arialbd.ttf"), 44)
            body_font = ImageFont.truetype(str(fonts / "arial.ttf"), 27)
            draw.text((90, 75), f"РЕЕСТР № {number}", font=title_font, fill="#222222")
            lines = [f"РЕЕСТР № {number}"]
            if family == "two-column":
                left = [f"Левый раздел, строка {index}: запись {number + index}." for index in range(1, 8)]
                right = [f"Правый раздел, строка {index}: запись {number + index + 10}." for index in range(1, 8)]
                for index, line in enumerate(left):
                    draw.text((90, 190 + index * 92), line, font=body_font, fill="#222222")
                for index, line in enumerate(right):
                    draw.text((820, 190 + index * 92), line, font=body_font, fill="#222222")
                lines.extend(left + right)
                marker = right[-1]
            elif family == "table":
                headers = ("Наименование", "Количество", "Цена, руб.")
                for x, header in zip((90, 800, 1150), headers):
                    draw.text((x, 190), header, font=body_font, fill="#222222")
                lines.append(" ".join(headers))
                for index in range(1, 8):
                    row = (f"Папка № {number + index}", f"{index + 2} шт.", f"{(index + 2) * 125} руб.")
                    for x, cell in zip((90, 800, 1150), row):
                        draw.text((x, 190 + index * 82), cell, font=body_font, fill="#222222")
                    lines.append(" ".join(row))
                marker = f"Папка № {number + 7}"
            else:
                small_font = ImageFont.truetype(str(fonts / "times.ttf"), 17)
                for index in range(1, 11):
                    line = f"Пункт {index}. Сведения о проверке записи № {number + index}: дата 13.10.2026, статус подтверждён."
                    draw.text((90, 175 + index * 60), line, font=small_font, fill="#333333")
                    lines.append(line)
                marker = f"Пункт 10. Сведения о проверке записи № {number + 10}"
            if variant == "soft-blur":
                image = image.filter(ImageFilter.GaussianBlur(1.0))
            elif variant == "skew":
                image = image.rotate(1.7, resample=Image.Resampling.BICUBIC, fillcolor="white")
            elif variant == "low-resolution":
                image = image.resize((960, 570), Image.Resampling.BILINEAR).resize((1600, 950), Image.Resampling.BILINEAR)
            name = f"{family}-{variant}.png"
            path = directory / name
            image.save(path)
            payload = path.read_bytes()
            entries.append({
                "file": name,
                "sha256": hashlib.sha256(payload).hexdigest(),
                "byteLength": len(payload),
                "pageCount": 1,
                "sourceGroup": family,
                "severity": variant,
                "pages": [{"pageNumber": 1, "expectedText": "\n".join(lines), "marker": marker}],
            })
    manifest = {
        "schemaVersion": 1,
        "purpose": "diagnostic-only",
        "dataPartition": "synthetic-diagnostic",
        "countsTowardRealHoldoutQuota": False,
        "countsTowardTrainingQuota": False,
        "sources": entries,
    }
    (directory / "corpus.reference.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Generate synthetic layout cases for offline OCR diagnostics")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    generated = make_corpus(args.output)
    print(f"Generated {len(generated['sources'])} pages in {args.output}")
