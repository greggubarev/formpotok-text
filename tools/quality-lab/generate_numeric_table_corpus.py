"""Generate deterministic numeric tables with complete page-level OCR references."""

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


ROW_COUNTS = (4, 12, 24)
VARIANTS = ("clean", "skew", "soft-blur", "low-resolution", "combined")
WIDTH = 1500
FONT_DIR = Path("C:/Windows/Fonts")


def money(kopecks: int) -> str:
    return f"{kopecks // 100},{kopecks % 100:02d}"


def table_reference(row_count: int) -> tuple[list[str], str]:
    lines = [f"ВЕДОМОСТЬ № {400 + row_count}", "№ Кол-во Цена Сумма"]
    total = 0
    for number in range(1, row_count + 1):
        quantity = 1 + number % 9
        price = 1250 + number * 37
        amount = quantity * price
        total += amount
        lines.append(f"{number} {quantity} {money(price)} {money(amount)}")
    marker = f"ИТОГО {money(total)}"
    lines.append(marker)
    return lines, marker


def table_rows(row_count: int) -> list[list[str]]:
    return [line.split(" ") for line in table_reference(row_count)[0][2:-1]]


def render_table(row_count: int, variant: str) -> Image.Image:
    height = 280 + row_count * 54
    image = Image.new("RGB", (WIDTH, height), "white")
    draw = ImageDraw.Draw(image)
    title_font = ImageFont.truetype(str(FONT_DIR / "arialbd.ttf"), 38)
    cell_font = ImageFont.truetype(str(FONT_DIR / "arial.ttf"), 28)
    bold_font = ImageFont.truetype(str(FONT_DIR / "arialbd.ttf"), 28)
    lines, marker = table_reference(row_count)
    draw.text((92, 45), lines[0], font=title_font, fill="#202020")
    columns = ((92, 270), (270, 530), (530, 900), (900, 1390))
    draw.rectangle((72, 120, 1410, 228 + row_count * 54), outline="#555555", width=2)
    for left, _ in columns[1:]:
        draw.line((left, 120, left, 228 + row_count * 54), fill="#777777", width=2)
    for index, line in enumerate(lines[1:-1]):
        top = 120 + index * 54
        if index:
            draw.line((72, top, 1410, top), fill="#aaaaaa", width=1)
        for cell, (left, right) in zip(line.split(" "), columns):
            box = draw.textbbox((0, 0), cell, font=bold_font if index == 0 else cell_font)
            draw.text((right - 24 - (box[2] - box[0]), top + 9), cell,
                      font=bold_font if index == 0 else cell_font, fill="#202020")
    total_top = 120 + (row_count + 1) * 54
    draw.line((72, total_top, 1410, total_top), fill="#555555", width=2)
    draw.text((940, total_top + 9), marker, font=bold_font, fill="#202020")

    if variant in ("skew", "combined"):
        image = image.rotate(1.6, resample=Image.Resampling.BICUBIC, fillcolor="white")
    if variant in ("soft-blur", "combined"):
        image = image.filter(ImageFilter.GaussianBlur(1.0))
    if variant in ("low-resolution", "combined"):
        small = image.resize((int(WIDTH * 0.58), int(height * 0.58)), Image.Resampling.BILINEAR)
        image = small.resize((WIDTH, height), Image.Resampling.BILINEAR)
    return image


def make_corpus(directory: Path) -> dict:
    directory.mkdir(parents=True, exist_ok=True)
    sources = []
    for row_count in ROW_COUNTS:
        lines, marker = table_reference(row_count)
        for variant in VARIANTS:
            name = f"numeric-table-{row_count:02d}-rows-{variant}.png"
            path = directory / name
            render_table(row_count, variant).save(path)
            payload = path.read_bytes()
            sources.append({
                "file": name,
                "sha256": hashlib.sha256(payload).hexdigest(),
                "byteLength": len(payload),
                "pageCount": 1,
                "sourceGroup": "numeric-table",
                "severity": variant,
                "pages": [{"pageNumber": 1, "expectedText": "\n".join(lines), "marker": marker,
                           "numericTable": {"rows": table_rows(row_count)}}],
            })
    manifest = {
        "schemaVersion": 1,
        "purpose": "diagnostic-only",
        "dataPartition": "synthetic-diagnostic",
        "countsTowardRealHoldoutQuota": False,
        "countsTowardTrainingQuota": False,
        "sources": sources,
    }
    (directory / "corpus.reference.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8"
    )
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Generate numeric table cases for offline OCR diagnostics")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    generated = make_corpus(args.output)
    print(f"Generated {len(generated['sources'])} pages in {args.output}")
