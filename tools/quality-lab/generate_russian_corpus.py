"""Create a deterministic, synthetic Russian OCR diagnostic corpus."""

import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image, ImageDraw, ImageFilter, ImageFont


FAMILIES = (
    ("application", "ЗАЯВЛЕНИЕ", "Прошу выдать справку по заявке № {n} от {date}.", "Отдел регистрации: архив № {code}.", "Основание: обращение принято в работу."),
    ("acceptance", "АКТ ПРИЕМА", "Принято оборудование по акту № {n} от {date}.", "Количество: {qty} шт. Серия: РУ-{code}.", "Замечаний к комплектности нет."),
    ("invoice", "СЧЕТ НА ОПЛАТУ", "Счет № {n} от {date}. Позиция: бумага А4.", "Количество: {qty} шт. Цена: {price} руб.", "Итого к оплате: {total} руб."),
    ("notice", "УВЕДОМЛЕНИЕ", "Уведомляем о переносе заседания № {n}.", "Новая дата: {date}. Кабинет: {code}.", "Просим подтвердить получение письма."),
    ("certificate", "СПРАВКА", "Выдана справка № {n} от {date}.", "Запись внесена в реестр под № {code}.", "Справка действительна без печати."),
    ("waybill", "НАКЛАДНАЯ", "Накладная № {n} от {date}.", "Место отгрузки: склад № {code}.", "Передано: {qty} шт. Масса: {weight} кг."),
)
SEVERITIES = ("clean", "gray", "soft-blur", "skew", "low-resolution", "jpeg")


def make_corpus(directory: Path) -> dict:
    directory.mkdir(parents=True, exist_ok=True)
    sources = []
    font_dir = Path("C:/Windows/Fonts")
    for family_index, (family, heading, *body) in enumerate(FAMILIES):
        for severity_index, severity in enumerate(SEVERITIES):
            number = 100 + family_index * 6 + severity_index
            qty = 3 + severity_index
            values = {
                "n": number,
                "date": f"{severity_index + 11:02d}.10.2026",
                "code": 20 + family_index * 7 + severity_index,
                "qty": qty,
                "price": 125,
                "total": qty * 125,
                "weight": qty * 18,
            }
            lines = [heading, *(line.format(**values) for line in body), f"Контрольная отметка: ЁЖ-{number}. После проверки сохранить."]
            image = Image.new("RGB", (1500, 900), "white")
            draw = ImageDraw.Draw(image)
            font_name = "times.ttf" if family_index % 2 else "arial.ttf"
            body_font = ImageFont.truetype(str(font_dir / font_name), 38)
            title_font = ImageFont.truetype(str(font_dir / "arialbd.ttf"), 52)
            color = "#555555" if severity == "gray" else "#222222"
            draw.text((100, 100), lines[0], font=title_font, fill=color)
            for line_index, line in enumerate(lines[1:]):
                draw.text((100, 230 + line_index * 112), line, font=body_font, fill=color)
            if severity == "soft-blur":
                image = image.filter(ImageFilter.GaussianBlur(1.15))
            elif severity == "skew":
                image = image.rotate(2.0, resample=Image.Resampling.BICUBIC, fillcolor="white")
            elif severity == "low-resolution":
                image = image.resize((900, 540), Image.Resampling.BILINEAR).resize((1500, 900), Image.Resampling.BILINEAR)
            extension = ".jpg" if severity == "jpeg" else ".png"
            name = f"{family}-{severity}{extension}"
            path = directory / name
            if extension == ".jpg":
                image.save(path, quality=48, subsampling=2)
            else:
                image.save(path)
            payload = path.read_bytes()
            sources.append({
                "file": name,
                "sha256": hashlib.sha256(payload).hexdigest(),
                "byteLength": len(payload),
                "pageCount": 1,
                "sourceGroup": family,
                "severity": severity,
                "pages": [{"pageNumber": 1, "expectedText": "\n".join(lines), "marker": f"ЁЖ-{number}"}],
            })
    manifest = {
        "schemaVersion": 1,
        "purpose": "diagnostic-only",
        "dataPartition": "synthetic-diagnostic",
        "countsTowardRealHoldoutQuota": False,
        "countsTowardTrainingQuota": False,
        "sources": sources,
    }
    (directory / "corpus.reference.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")
    return manifest


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description="Generate a deterministic local OCR diagnostic corpus")
    parser.add_argument("output", type=Path)
    args = parser.parse_args()
    generated = make_corpus(args.output)
    print(f"Generated {len(generated['sources'])} pages in {args.output}")
