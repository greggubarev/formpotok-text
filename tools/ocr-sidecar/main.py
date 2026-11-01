from __future__ import annotations

import argparse
import csv
import hashlib
import importlib.metadata
import json
import math
import os
import re
import shutil
import subprocess
import sys
from contextlib import ExitStack, contextmanager
from pathlib import Path
from time import perf_counter

import pypdfium2 as pdfium
from PIL import Image, ImageFilter, ImageStat
from pypdf import PdfReader
from pypdf.errors import PdfReadError
from reading_order import reorder_four_column_numeric_table, reorder_numeric_table, reorder_two_columns

os.environ.setdefault("PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK", "True")


def resolve_bundled_paddle_cache_home() -> Path | None:
    configured = os.environ.get("FORMPOTOK_OCR_PADDLE_CACHE_HOME")
    if configured:
        return Path(configured).expanduser().resolve()

    candidates: list[Path] = []
    if getattr(sys, "frozen", False):
        candidates.append(Path(sys.executable).resolve().parent.parent / "models" / "paddlex")
    candidates.append(Path(__file__).resolve().parent / "models" / "paddlex")

    return next((candidate for candidate in candidates if candidate.is_dir()), None)


_BUNDLED_PADDLE_CACHE_HOME = resolve_bundled_paddle_cache_home()
if _BUNDLED_PADDLE_CACHE_HOME is not None:
    os.environ.setdefault("PADDLE_PDX_CACHE_HOME", str(_BUNDLED_PADDLE_CACHE_HOME))

if hasattr(sys.stdout, "reconfigure"):
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")

DEFAULT_TESSERACT_PATHS = [
    Path(r"C:\Program Files\Tesseract-OCR\tesseract.exe"),
]
IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".jfif", ".tif", ".tiff", ".bmp", ".webp"}
TEXT_ONLY_MAX_PAGES = 100
TEXT_ONLY_MAX_PIXELS = 20_000_000
TEXT_ONLY_OFFLINE_MAX_PAGES = 20
TEXT_ONLY_MAX_SOURCE_BYTES = 20 * 1024 * 1024
PADDLE_NATIVE_IMAGE_EXTENSIONS = {".png", ".jpg", ".jpeg", ".bmp"}
FIELD_MARKER_MAX_TEXT_LENGTH = 8
SECOND_PASS_ZONES = [
    {
        "name": "cmr_number",
        "field_key": "cmr_number",
        "bounds": (0.60, 0.92, 0.00, 0.14),
        "min_block_count": 1,
        "min_quality_score": 0.70,
        "max_page_number": 1,
        "retry_strategy": "cmr-number-ambiguity",
    },
    {
        "name": "ship_to",
        "field_key": "ship_to",
        "bounds": (0.03, 0.48, 0.23, 0.34),
        "min_block_count": 4,
        "min_quality_score": 0.74,
    },
    {
        "name": "ship_from",
        "field_key": "ship_from",
        "bounds": (0.03, 0.48, 0.30, 0.43),
        "min_block_count": 3,
        "min_quality_score": 0.70,
    },
    {
        "name": "vehicle",
        "bounds": (0.04, 0.72, 0.70, 0.98),
        "min_block_count": 3,
        "min_quality_score": 0.78,
    },
    {
        "name": "driver",
        "bounds": (0.24, 0.82, 0.64, 0.94),
        "min_block_count": 2,
        "min_quality_score": 0.76,
    },
    {
        "name": "transport_weight",
        "field_key": "transport_weight_tons",
        "bounds": (0.66, 0.84, 0.38, 0.52),
        "min_block_count": 1,
        "min_quality_score": 0.72,
    },
]
PLATE_LATIN_LETTERS = "ABEKMHOPCTYX"
CYRILLIC_TO_PLATE_LATIN = str.maketrans(
    {
        "А": "A",
        "В": "B",
        "Д": "D",
        "Е": "E",
        "К": "K",
        "Л": "L",
        "М": "M",
        "Н": "H",
        "О": "O",
        "Р": "P",
        "С": "C",
        "Т": "T",
        "У": "Y",
        "Х": "X",
        "З": "Z",
    }
)
RF_TRUCK_PLATE_REGEX = re.compile(rf"^[{PLATE_LATIN_LETTERS}]\d{{3}}[{PLATE_LATIN_LETTERS}]{{2}}(?P<region>\d{{2,3}})$")
RF_TRAILER_PLATE_REGEX = re.compile(rf"^[{PLATE_LATIN_LETTERS}]{{2}}\d{{4}}(?P<region>\d{{2,3}})$")
KZ_TRUCK_PLATE_REGEX = re.compile(r"^\d{3}[A-Z]{2,3}(0[1-9]|1\d|20)$")
KZ_TRAILER_PLATE_REGEX = re.compile(r"^\d{2}[A-Z]{3}(0[1-9]|1\d|20)$")
LEGACY_TRUCK_PLATE_REGEX = re.compile(rf"^\d{{2}}[{PLATE_LATIN_LETTERS}]{{2}}\d{{3}}$")
VALID_THREE_DIGIT_RF_REGION_CODES = {
    "102", "113", "116", "121", "123", "124", "125", "126", "134", "136", "138", "142",
    "147", "150", "152", "154", "156", "159", "161", "163", "164", "173", "174", "177",
    "178", "186", "190", "193", "196", "197", "198", "199", "702", "716", "750", "761",
    "763", "774", "777", "790", "797", "799", "977", "999",
}
DRIVER_NAME_REGEX = re.compile(
    r"\b[А-ЯЁA-Z][А-ЯЁA-Zа-яёa-z'-]{1,}(?:\s+[А-ЯЁA-Z][А-ЯЁA-Zа-яёa-z'-]{1,}){1,2}\b",
    re.IGNORECASE,
)
WEIGHT_NUMBER_REGEX = re.compile(r"(?<!\d)(\d{1,5}(?:[.,]\d{1,3})?)(?!\d)")
CMR_NUMBER_CANDIDATE_REGEX = re.compile(
    r"(?:№|N(?:[OОEЕº°])?|#)\s*(?P<number>[OО]?\d{2,4}\s*[/\\]\s*[A-ZА-Я0-9]{1,2})",
    re.IGNORECASE,
)
CMR_MERGED_NUMBER_AMBIGUITY_REGEX = re.compile(
    r"C[MМ][RРDДBВPП](?:№|N(?:[OОEЕº°])?|#)?(?P<number>\d{4})",
    re.IGNORECASE,
)
DATE_TOKEN_REGEX = re.compile(r"\b\d{1,2}[\s./-]\d{1,2}[\s./-]\d{2,4}\b")
DRIVER_LINE_HINTS = ("водител", "фио", "driver", "surname", "имя")
DRIVER_LINE_STOP_HINTS = (
    "ооо",
    "тоо",
    "зао",
    "оао",
    "инн",
    "бин",
    "иин",
    "паспорт",
    "контракт",
    "международ",
    "дорожн",
    "перевоз",
    "накладн",
    "отправител",
    "получател",
    "подпис",
    "код",
    "продукц",
    "артикул",
    "складск",
    "картотек",
    "пропис",
    "сопровод",
    "документ",
    "идентифицир",
    "позволяющ",
    "описан",
    "обстоятельств",
    "кратк",
    "счет",
    "счёт",
    "банк",
    "unterschrift",
    "unters",
    "unte",
    "stempel",
    "signature",
    "stamp",
    "consignor",
    "consignee",
    "consign",
    "loading",
    "delivery",
    "оплач",
    "отчисл",
    "заказч",
    "отправител",
    "получател",
)
TRUCK_HINTS = ("тягач", "truck", "а/м", "гос", "госномер", "рег", "a1.1")
TRAILER_HINTS = ("прицеп", "полуприцеп", "trailer", "a1.2")
WEIGHT_HINTS = ("вес", "масса", "брутто", "нетто", "тон", "тн", "кг", "kg")
_ORIGINAL_SUBPROCESS_POPEN = subprocess.Popen
_ORIGINAL_SUBPROCESS_RUN = subprocess.run
_TEXT_RECOGNITION_CACHE: dict[str, object] = {}


def hidden_subprocess_kwargs(kwargs: dict[str, object] | None = None) -> dict[str, object]:
    kwargs = dict(kwargs or {})

    if os.name != "nt":
        return kwargs

    if "startupinfo" not in kwargs:
        startupinfo = subprocess.STARTUPINFO()
        startupinfo.dwFlags |= subprocess.STARTF_USESHOWWINDOW
        startupinfo.wShowWindow = 0
        kwargs["startupinfo"] = startupinfo

    create_no_window = getattr(subprocess, "CREATE_NO_WINDOW", 0)
    kwargs["creationflags"] = int(kwargs.get("creationflags", 0)) | create_no_window
    return kwargs


def _patch_windows_subprocesses() -> None:
    if os.name != "nt":
        return

    class HiddenPopen(_ORIGINAL_SUBPROCESS_POPEN):
        def __init__(self, *args, **kwargs):
            super().__init__(*args, **hidden_subprocess_kwargs(kwargs))

    def hidden_run(*args, **kwargs):
        return _ORIGINAL_SUBPROCESS_RUN(*args, **hidden_subprocess_kwargs(kwargs))

    subprocess.Popen = HiddenPopen
    subprocess.run = hidden_run


_patch_windows_subprocesses()


def distribution_version(name: str, fallback: str = "unknown") -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return fallback


def resolve_existing_path(candidate: str | None) -> str | None:
    if not candidate:
        return None
    path = Path(candidate).expanduser().resolve()
    return str(path) if path.exists() else None


def resolve_tesseract_path(explicit_path: str | None = None) -> str | None:
    if explicit_path:
        candidate = Path(explicit_path)
        if candidate.exists():
            return str(candidate)

    discovered = shutil.which("tesseract")
    if discovered:
        return discovered

    for candidate in DEFAULT_TESSERACT_PATHS:
        if candidate.exists():
            return str(candidate)

    return None


def bundled_tessdata_dir() -> Path:
    return Path(__file__).resolve().parent / "tessdata"


def is_valid_tessdata_dir(candidate: Path) -> bool:
    return candidate.exists() and candidate.is_dir() and any(candidate.glob("*.traineddata"))


def resolve_tessdata_dir(explicit_path: str | None = None) -> str | None:
    if explicit_path:
        candidate = Path(explicit_path).expanduser().resolve()
        if is_valid_tessdata_dir(candidate):
            return str(candidate)

    env_candidate = os.environ.get("TESSDATA_PREFIX")
    if env_candidate:
        candidate = Path(env_candidate).expanduser().resolve()
        if is_valid_tessdata_dir(candidate):
            return str(candidate)

    bundled = bundled_tessdata_dir()
    if is_valid_tessdata_dir(bundled):
        return str(bundled)

    default_path = Path(r"C:\Program Files\Tesseract-OCR\tessdata")
    if is_valid_tessdata_dir(default_path):
        return str(default_path)

    return None


def has_tesseract_tsv_config(tessdata_dir: str | None) -> bool:
    if not tessdata_dir:
        return False
    return Path(tessdata_dir).joinpath("configs", "tsv").exists()


def resolve_ghostscript_path() -> str | None:
    return shutil.which("gswin64c") or shutil.which("gs")


def resolve_ocrmypdf_path() -> str | None:
    return shutil.which("ocrmypdf")


def build_full_page_polygon(width: int, height: int) -> list[dict[str, float]]:
    return [
        {"X": 0.0, "Y": 0.0},
        {"X": float(width), "Y": 0.0},
        {"X": float(width), "Y": float(height)},
        {"X": 0.0, "Y": float(height)},
    ]


def polygon_to_points(polygon: object) -> list[dict[str, float]]:
    points: list[dict[str, float]] = []
    for point in polygon if polygon is not None else []:
        x, y = point
        points.append({"X": float(x), "Y": float(y)})
    return points


def prepare_paddle_variants(image_path: Path, variants_dir: Path, source_errors: bool = False,
                            original_only: bool = False,
                            include_portrait_rotations: bool = False) -> list[tuple[str, Path]]:
    variants_dir.mkdir(parents=True, exist_ok=True)

    with Image.open(image_path) as source_image:
        try:
            normalized = source_image.convert("RGB")
        except (OSError, ValueError):
            if source_errors:
                raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID") from None
            raise
        width, height = normalized.size
        is_landscape = width > height

        if image_path.suffix.lower() in PADDLE_NATIVE_IMAGE_EXTENSIONS:
            original_path = image_path
        else:
            original_path = variants_dir / f"{image_path.stem}-normalized.jpg"
            normalized.save(original_path, format="JPEG", quality=95)

        variants: list[tuple[str, Path]] = [("original", original_path)]

        if (is_landscape or include_portrait_rotations) and not original_only:
            for label, angle in (("rot90", 90), ("rot270", 270)):
                rotated_path = variants_dir / f"{image_path.stem}-{label}.jpg"
                normalized.rotate(angle, expand=True).save(rotated_path, format="JPEG", quality=95)
                variants.append((label, rotated_path))

    return variants


def variant_dimensions(variant_label: str, original_width: int, original_height: int) -> tuple[int, int]:
    if variant_label == "original":
        return original_width, original_height
    if variant_label in {"rot90", "rot270"}:
        return original_height, original_width
    raise ValueError(f"Unsupported OCR image variant: {variant_label}")


def inverse_transform_variant_point(
    x: float,
    y: float,
    variant_label: str,
    original_width: int,
    original_height: int,
) -> dict[str, float]:
    if variant_label == "original":
        original_x, original_y = x, y
    elif variant_label == "rot90":
        original_x, original_y = float(original_width) - y, x
    elif variant_label == "rot270":
        original_x, original_y = y, float(original_height) - x
    else:
        raise ValueError(f"Unsupported OCR image variant: {variant_label}")

    return {
        "X": min(max(float(original_x), 0.0), float(original_width)),
        "Y": min(max(float(original_y), 0.0), float(original_height)),
    }


def remap_normalized_bbox_to_original_frame(
    bbox: dict[str, float] | None,
    variant_label: str,
    original_width: int,
    original_height: int,
) -> dict[str, float] | None:
    if bbox is None:
        return None
    if variant_label == "original":
        return dict(bbox)

    selected_width, selected_height = variant_dimensions(variant_label, original_width, original_height)
    left = float(bbox["X"]) * selected_width
    top = float(bbox["Y"]) * selected_height
    right = left + float(bbox["Width"]) * selected_width
    bottom = top + float(bbox["Height"]) * selected_height
    selected_polygon = [
        {"X": left, "Y": top},
        {"X": right, "Y": top},
        {"X": right, "Y": bottom},
        {"X": left, "Y": bottom},
    ]
    original_polygon = [
        inverse_transform_variant_point(
            float(point["X"]),
            float(point["Y"]),
            variant_label,
            original_width,
            original_height,
        )
        for point in selected_polygon
    ]
    return normalized_bbox_from_blocks(
        [{"Polygon": original_polygon}],
        original_width,
        original_height,
    )


def remap_candidate_bbox_to_original_frame(
    candidate: dict[str, object],
    selected_variant_label: str,
    original_width: int,
    original_height: int,
) -> dict[str, object]:
    remapped = dict(candidate)
    bbox_variant_label = str(remapped.pop("_bbox_variant_label", selected_variant_label))
    bbox = remapped.get("bbox_normalized")
    if isinstance(bbox, dict):
        remapped["bbox_normalized"] = remap_normalized_bbox_to_original_frame(
            bbox,
            bbox_variant_label,
            original_width,
            original_height,
        )
        remapped["bbox_coordinate_frame"] = "original-pixels"
    return remapped


def remap_page_to_original_frame(
    page: dict[str, object],
    selected_variant_label: str,
    original_width: int,
    original_height: int,
) -> dict[str, object]:
    variant_dimensions(selected_variant_label, original_width, original_height)
    remapped_blocks: list[dict[str, object]] = []
    for block in page.get("TextBlocks", []):
        remapped_block = dict(block)
        remapped_block["Polygon"] = [
            inverse_transform_variant_point(
                float(point["X"]),
                float(point["Y"]),
                selected_variant_label,
                original_width,
                original_height,
            )
            for point in block.get("Polygon", [])
        ]
        remapped_blocks.append(remapped_block)

    remapped_page = dict(page)
    remapped_page["Width"] = original_width
    remapped_page["Height"] = original_height
    remapped_page["TextBlocks"] = remapped_blocks
    remapped_page["CoordinateFrame"] = "original-pixels"
    remapped_page["SelectedVariant"] = selected_variant_label
    return remapped_page


def build_paddle_page_result(
    ocr,
    image_path: Path,
    page_number: int,
    profile: str,
    detection_model_dir: str | None,
    recognition_model_dir: str | None,
    recognition_model_name: str | None = None,
    require_polygons: bool = False,
    use_doc_orientation_classify: bool | None = None,
) -> dict[str, object]:
    with Image.open(image_path) as image:
        width, height = image.size

    started_at = perf_counter()
    results = (ocr.predict(str(image_path)) if use_doc_orientation_classify is None
               else ocr.predict(str(image_path), use_doc_orientation_classify=use_doc_orientation_classify))
    elapsed = round(perf_counter() - started_at, 3)
    result = results[0] if results else {}
    preprocessor = result.get("doc_preprocessor_res") or {}
    orientation_angle = preprocessor.get("angle") if isinstance(preprocessor, dict) else None
    if orientation_angle not in (0, 90, 180, 270):
        orientation_angle = None

    texts = result.get("rec_texts", [])
    scores = result.get("rec_scores", [])
    polygons = result.get("rec_polys", result.get("dt_polys", []))
    text_blocks: list[dict[str, object]] = []
    raw_entries: list[dict[str, object]] = []
    low_confidence_count = 0

    for index, text in enumerate(texts, start=1):
        if not text:
            continue

        confidence = float(scores[index - 1]) if index - 1 < len(scores) else 0.0
        polygon = (polygon_to_points(polygons[index - 1]) if index - 1 < len(polygons)
                   else [] if require_polygons else build_full_page_polygon(width, height))
        if require_polygons and (len(polygon) < 3 or any(
            not (math.isfinite(point["X"]) and math.isfinite(point["Y"]))
            or point["X"] < 0 or point["X"] > width
            or point["Y"] < 0 or point["Y"] > height
            for point in polygon
        )):
            raise TextOnlyError("TEXT_ONLY_TILE_GEOMETRY_INVALID")
        text_blocks.append(
            {
                "Id": f"paddle-{page_number:04d}-{index:04d}",
                "Text": text,
                "Confidence": confidence,
                "Polygon": polygon,
            }
        )
        raw_entries.append(
            {
                "Text": text,
                "Confidence": confidence,
                "Polygon": polygon,
            }
        )

        if confidence < 0.75:
            low_confidence_count += 1

    page_payload = {
        "DocumentId": image_path.stem,
        "EngineName": f"PaddleOCR/{profile}",
        "EngineVersion": distribution_version("paddleocr"),
        "Pages": [
            {
                "PageNumber": page_number,
                "Width": width,
                "Height": height,
                "TextBlocks": text_blocks,
                "TableBlocks": [],
            }
        ],
        "Warnings": [],
        "Candidates": [],
        "Artifacts": [str(image_path)],
    }

    return {
        "page": page_payload["Pages"][0],
        "raw_entries": raw_entries,
        "elapsed": elapsed,
        "low_confidence_count": low_confidence_count,
        "metrics": payload_quality(page_payload),
        "orientation_angle": orientation_angle,
        "detection_model_dir": resolve_existing_path(detection_model_dir or os.environ.get("FORMPOTOK_OCR_DET_MODEL_DIR")),
        "recognition_model_dir": resolve_existing_path(recognition_model_dir or os.environ.get("FORMPOTOK_OCR_REC_MODEL_DIR")),
        "recognition_model_name": (recognition_model_name or os.environ.get("FORMPOTOK_OCR_REC_MODEL_NAME") or "").strip() or None,
    }


TEXT_TILE_MAX_CORE_HEIGHT = 1242
TEXT_TILE_OVERLAP = 100
TEXT_TILE_MAX_TILES = 20


def build_tiled_paddle_page_result(
    image_path: Path,
    tile_dir: Path,
    page_number: int,
    profile: str,
    lang: str,
    detection_model_dir: str | None,
    recognition_model_dir: str | None,
    recognition_model_name: str | None = None,
) -> dict[str, object]:
    tile_dir.mkdir(parents=True, exist_ok=True)
    blocks: list[dict[str, object]] = []
    raw_entries: list[dict[str, object]] = []
    tiles: list[dict[str, object]] = []
    elapsed = 0.0
    low_confidence_count = 0
    with Image.open(image_path) as image:
        width, height = image.size
    tile_count = (height + TEXT_TILE_MAX_CORE_HEIGHT - 1) // TEXT_TILE_MAX_CORE_HEIGHT
    if tile_count > TEXT_TILE_MAX_TILES:
        raise TextOnlyError("TEXT_ONLY_TILE_LIMIT")
    core_height = (height + tile_count - 1) // tile_count
    for index, core_top in enumerate(range(0, height, core_height)):
        core_bottom = min(height, core_top + core_height)
        tile_top = max(0, core_top - TEXT_TILE_OVERLAP)
        tile_bottom = min(height, core_bottom + TEXT_TILE_OVERLAP)
        tile_path = tile_dir / f"tile-{index:04d}.jpg"
        with Image.open(image_path) as image:
            image.crop((0, tile_top, width, tile_bottom)).convert("RGB").save(
                tile_path, format="JPEG", quality=95,
            )
        result_path = tile_dir / f"tile-{index:04d}.json"
        result = run_tile_worker(
            tile_path, result_path, page_number, profile, lang,
            detection_model_dir, recognition_model_dir, recognition_model_name,
        )
        elapsed += result["elapsed"]
        for block in result["page"]["TextBlocks"]:
            polygon = [{"X": point["X"], "Y": point["Y"] + tile_top}
                       for point in block["Polygon"]]
            center_y = sum(point["Y"] for point in polygon) / len(polygon)
            if not core_top <= center_y < core_bottom:
                continue
            mapped = dict(block)
            mapped["Id"] = f"paddle-{page_number:04d}-{len(blocks) + 1:04d}"
            mapped["Polygon"] = polygon
            blocks.append(mapped)
            raw_entries.append({key: mapped[key] for key in ("Text", "Confidence", "Polygon")})
            if mapped["Confidence"] < 0.75:
                low_confidence_count += 1
        tiles.append({"ImagePath": str(tile_path), "ResultPath": str(result_path), "TileTop": tile_top,
                      "TileBottom": tile_bottom, "CoreTop": core_top,
                      "CoreBottom": core_bottom})

    return {
        "page": {"PageNumber": page_number, "Width": width, "Height": height,
                 "TextBlocks": blocks, "TableBlocks": []},
        "raw_entries": raw_entries,
        "elapsed": round(elapsed, 3),
        "low_confidence_count": low_confidence_count,
        "metrics": quality_from_blocks(blocks),
        "detection_model_dir": resolve_existing_path(detection_model_dir or os.environ.get("FORMPOTOK_OCR_DET_MODEL_DIR")),
        "recognition_model_dir": resolve_existing_path(recognition_model_dir or os.environ.get("FORMPOTOK_OCR_REC_MODEL_DIR")),
        "recognition_model_name": (recognition_model_name or os.environ.get("FORMPOTOK_OCR_REC_MODEL_NAME") or "").strip() or None,
        "tiles": tiles,
    }


def run_tile_worker(
    image_path: Path,
    result_path: Path,
    page_number: int,
    profile: str,
    lang: str,
    detection_model_dir: str | None,
    recognition_model_dir: str | None,
    recognition_model_name: str | None,
) -> dict[str, object]:
    command = [sys.executable]
    if not getattr(sys, "frozen", False):
        command.append(str(Path(__file__).resolve()))
    command.extend(["tile-worker", "--input", str(image_path), "--output", str(result_path),
                    "--page-number", str(page_number), "--paddle-profile", profile,
                    "--paddle-lang", lang])
    for flag, value in (("--paddle-det-model-dir", detection_model_dir),
                        ("--paddle-rec-model-dir", recognition_model_dir),
                        ("--paddle-rec-model-name", recognition_model_name)):
        if value:
            command.extend([flag, value])
    completed = subprocess.run(command, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                               check=False)
    if completed.returncode != 0 or not result_path.is_file():
        raise TextOnlyError("TEXT_ONLY_TILE_RUNTIME_FAILED")
    return json.loads(result_path.read_text(encoding="utf-8"))


def tile_worker(args: argparse.Namespace) -> int:
    with offline_paddle_runtime(True):
        ocr = create_paddle_ocr(
            args.paddle_profile, args.paddle_lang, args.paddle_det_model_dir,
            args.paddle_rec_model_dir, args.paddle_rec_model_name,
            offline=True, disable_document_unwarping=True,
        )
        result = build_paddle_page_result(
            ocr, Path(args.input), args.page_number, args.paddle_profile,
            args.paddle_det_model_dir, args.paddle_rec_model_dir,
            args.paddle_rec_model_name, require_polygons=True,
        )
        write_json(Path(args.output), result)
    return 0


def write_json(path: Path, payload: object) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(payload, ensure_ascii=False, indent=2), encoding="utf-8")


def print_json(payload: object) -> int:
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0


def all_text_blocks(payload: dict[str, object]) -> list[dict[str, object]]:
    pages = payload.get("Pages", [])
    return [block for page in pages for block in page.get("TextBlocks", [])]


def payload_quality(payload: dict[str, object]) -> dict[str, float]:
    return quality_from_blocks(all_text_blocks(payload))


def should_try_tesseract_after_paddle(payload: dict[str, object]) -> bool:
    metrics = payload_quality(payload)
    return (
        metrics["block_count"] < 12
        or metrics["score"] < 0.62
        or metrics["low_conf_ratio"] > 0.45
        or metrics["multi_char_ratio"] < 0.55
    )


def attach_quality_warning(payload: dict[str, object], code: str, message: str) -> dict[str, object]:
    warnings = list(payload.get("Warnings", []))
    warnings.append({"Code": code, "Message": message})
    payload["Warnings"] = warnings
    return payload


def choose_best_payload(primary: dict[str, object], secondary: dict[str, object], primary_name: str, secondary_name: str) -> dict[str, object]:
    primary_metrics = payload_quality(primary)
    secondary_metrics = payload_quality(secondary)
    if secondary_metrics["score"] > primary_metrics["score"] + 0.04:
        return attach_quality_warning(
            secondary,
            "AUTO_ENGINE_SELECTED",
            f"Selected {secondary_name} over {primary_name} by quality score {secondary_metrics['score']:.3f} > {primary_metrics['score']:.3f}.",
        )

    return attach_quality_warning(
        primary,
        "AUTO_ENGINE_SELECTED",
        f"Kept {primary_name}; quality score {primary_metrics['score']:.3f} vs {secondary_name} {secondary_metrics['score']:.3f}.",
    )


def healthcheck(
    tesseract_path: str | None,
    tessdata_dir: str | None,
    paddle_det_model_dir: str | None,
    paddle_rec_model_dir: str | None,
    paddle_rec_model_name: str | None,
) -> int:
    resolved_tessdata_dir = resolve_tessdata_dir(tessdata_dir)
    payload = {
        "IsHealthy": True,
        "EngineName": "local-ocr-stack",
        "EngineVersion": (
            f"paddleocr={distribution_version('paddleocr')};"
            f"paddlepaddle={distribution_version('paddlepaddle')};"
            f"paddlex={distribution_version('paddlex')}"
        ),
        "PythonVersion": sys.version.split()[0],
        "Device": "cpu",
        "Details": json.dumps(
            {
                "PreferredEngine": "auto",
                "PaddleProfile": "mobile-cpu",
                "PaddleDetectionModelDir": resolve_existing_path(paddle_det_model_dir or os.environ.get("FORMPOTOK_OCR_DET_MODEL_DIR")),
                "PaddleRecognitionModelDir": resolve_existing_path(paddle_rec_model_dir or os.environ.get("FORMPOTOK_OCR_REC_MODEL_DIR")),
                "PaddleRecognitionModelName": (paddle_rec_model_name or os.environ.get("FORMPOTOK_OCR_REC_MODEL_NAME") or "").strip() or None,
                "TesseractPath": resolve_tesseract_path(tesseract_path),
                "TessdataDirectory": resolved_tessdata_dir,
                "HasTsvConfig": has_tesseract_tsv_config(resolved_tessdata_dir),
                "GhostscriptPath": resolve_ghostscript_path(),
                "OcrMyPdfPath": resolve_ocrmypdf_path(),
            },
            ensure_ascii=False,
        ),
    }
    return print_json(payload)


def paddle_dependency_report() -> dict[str, object]:
    try:
        from paddlex.utils import deps

        extra_deps = sorted((deps.EXTRAS.get("ocr") or {}).keys())
        unavailable = [dep for dep in extra_deps if not deps.is_dep_available(dep)]
        return {
            "OcrExtraAvailable": deps.is_extra_available("ocr"),
            "DependencyCount": len(extra_deps),
            "UnavailableDependencies": unavailable,
        }
    except Exception as error:
        return {
            "OcrExtraAvailable": False,
            "DependencyCount": 0,
            "UnavailableDependencies": [],
            "Error": str(error),
        }


def paddle_healthcheck(
    profile: str,
    lang: str,
    detection_model_dir: str | None,
    recognition_model_dir: str | None,
    recognition_model_name: str | None,
    create_pipeline: bool,
) -> int:
    dependency_report = paddle_dependency_report()
    pipeline_created = None
    pipeline_error = None

    if create_pipeline and dependency_report.get("OcrExtraAvailable"):
        try:
            create_paddle_ocr(profile, lang, detection_model_dir, recognition_model_dir, recognition_model_name)
            pipeline_created = True
        except Exception as error:
            pipeline_created = False
            pipeline_error = str(error)

    is_healthy = bool(dependency_report.get("OcrExtraAvailable")) and (pipeline_created is not False)
    payload = {
        "IsHealthy": is_healthy,
        "EngineName": f"PaddleOCR/{profile}",
        "EngineVersion": (
            f"paddleocr={distribution_version('paddleocr')};"
            f"paddlepaddle={distribution_version('paddlepaddle')};"
            f"paddlex={distribution_version('paddlex')}"
        ),
        "PythonVersion": sys.version.split()[0],
        "Device": "cpu",
        "Details": {
            **dependency_report,
            "PaddleProfile": profile,
            "PaddleLanguage": lang,
            "PaddleDetectionModelDir": resolve_existing_path(detection_model_dir or os.environ.get("FORMPOTOK_OCR_DET_MODEL_DIR")),
            "PaddleRecognitionModelDir": resolve_existing_path(recognition_model_dir or os.environ.get("FORMPOTOK_OCR_REC_MODEL_DIR")),
            "PaddleRecognitionModelName": (recognition_model_name or os.environ.get("FORMPOTOK_OCR_REC_MODEL_NAME") or "").strip() or None,
            "PipelineRequested": create_pipeline,
            "PipelineCreated": pipeline_created,
            "PipelineError": pipeline_error,
        },
    }
    print(json.dumps(payload, ensure_ascii=False, indent=2))
    return 0 if is_healthy else 1


def render_pdf_pages(pdf_path: Path, render_dir: Path, scale: float = 2.0) -> list[Path]:
    render_dir.mkdir(parents=True, exist_ok=True)
    pdf = pdfium.PdfDocument(str(pdf_path))
    images: list[Path] = []

    for index in range(len(pdf)):
        page = pdf[index]
        bitmap = page.render(scale=scale)
        image = bitmap.to_pil()
        output_path = render_dir / f"page-{index + 1:04d}.png"
        image.save(output_path)
        images.append(output_path)

    return images


def validate_text_only_page_count(count: int, source: str) -> None:
    if count == 0:
        raise RuntimeError(f"TEXT_ONLY_EMPTY_DOCUMENT: {source} contains no pages.")
    if count > TEXT_ONLY_MAX_PAGES:
        raise RuntimeError(f"TEXT_ONLY_PAGE_LIMIT: {source} exceeds the {TEXT_ONLY_MAX_PAGES}-page limit.")


def validate_text_only_page_pixels(width: float, height: float, source: str, scale: float = 1.0) -> None:
    pixels = math.ceil(width * scale) * math.ceil(height * scale)
    if width <= 0 or height <= 0 or pixels > TEXT_ONLY_MAX_PIXELS:
        raise RuntimeError(f"TEXT_ONLY_PIXEL_LIMIT: {source} must have positive dimensions and at most {TEXT_ONLY_MAX_PIXELS} pixels.")


class TextOnlyError(RuntimeError):
    def __init__(self, code: str):
        self.code = code
        super().__init__(code)


def text_only_limit(value: int | None, cap: int, default: int) -> int:
    if value is None:
        return default
    if type(value) is not int or not 1 <= value <= cap:
        raise ValueError("INVALID_TEXT_ONLY_LIMIT")
    return value


def preflight_text_only_source(input_path: Path, max_pages: int, max_pixels: int) -> None:
    # Only header/page geometry reads are allowed until every page passes.
    if input_path.stat().st_size > TEXT_ONLY_MAX_SOURCE_BYTES:
        raise TextOnlyError("TEXT_ONLY_SOURCE_LIMIT")

    def check_count(count: int) -> None:
        if count <= 0:
            raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID")
        if count > max_pages:
            raise TextOnlyError("TEXT_ONLY_PAGE_LIMIT")

    def check_pixels(width: float, height: float, scale: float = 1.0) -> None:
        if not all(math.isfinite(value) and value > 0 for value in (width, height)):
            raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID")
        if math.ceil(width * scale) * math.ceil(height * scale) > max_pixels:
            raise TextOnlyError("TEXT_ONLY_PIXEL_LIMIT")

    suffix = input_path.suffix.lower()
    if suffix == ".pdf":
        with input_path.open("rb") as stream, pdfium.PdfDocument(stream) as pdf:
            check_count(len(pdf))
            for index in range(len(pdf)):
                page = pdf[index]
                try:
                    check_pixels(page.get_width(), page.get_height(), scale=2.0)
                finally:
                    page.close()
    elif suffix in {".png", ".jpg", ".jpeg", ".jfif"}:
        with Image.open(input_path, formats=["PNG", "JPEG"]) as image:
            if image.format not in {"PNG", "JPEG"}:
                raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID")
            check_pixels(image.width, image.height)
            check_count(getattr(image, "n_frames", 1))
            # Animated images cannot be represented by the single-page image path.
            if getattr(image, "n_frames", 1) != 1:
                raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID")
    else:
        raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID")


@contextmanager
def offline_paddle_runtime(enabled: bool):
    if not enabled:
        yield
        return

    import socket
    import urllib.request

    def blocked_download(*args, **kwargs):
        raise TextOnlyError("OFFLINE_MODELS_MISSING")

    configured = os.environ.get("FORMPOTOK_OCR_PADDLE_CACHE_HOME")
    cache_home = Path(configured).resolve() if configured else Path(__file__).resolve().parent / "models" / "paddlex"
    with ExitStack() as stack:
        def restore_environment(name: str, value: str | None) -> None:
            if value is None:
                os.environ.pop(name, None)
            else:
                os.environ[name] = value

        for name, value in {
            "PADDLE_PDX_CACHE_HOME": str(cache_home),
            "PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK": "True",
            "HF_HUB_OFFLINE": "1",
            "TRANSFORMERS_OFFLINE": "1",
            "PADDLE_PDX_EAGER_INIT": "False",
            "OMP_NUM_THREADS": "2",
            "MKL_NUM_THREADS": "2",
            "OPENBLAS_NUM_THREADS": "2",
        }.items():
            stack.callback(restore_environment, name, os.environ.get(name))
            os.environ[name] = value

        def block(owner, name: str) -> None:
            stack.callback(setattr, owner, name, getattr(owner, name))
            setattr(owner, name, blocked_download)

        # Guard imports as well as inference: downstream providers may try downloads.
        for owner, name in (
            (socket.socket, "connect"), (socket.socket, "connect_ex"),
            (socket, "create_connection"), (socket, "getaddrinfo"),
            (urllib.request, "urlopen"), (urllib.request, "urlretrieve"),
        ):
            block(owner, name)
        try:
            import requests
        except ImportError:
            pass
        else:
            block(requests.sessions.Session, "request")
        yield


def offline_model_root() -> Path:
    configured = os.environ.get("FORMPOTOK_OCR_PADDLE_CACHE_HOME")
    if not configured or not Path(configured).is_absolute() or configured.startswith(("\\\\", "//")):
        raise TextOnlyError("OFFLINE_MODELS_MISSING")
    try:
        root = Path(configured).resolve() / "official_models"
        if root.is_dir():
            return root
    except (OSError, ValueError):
        pass
    raise TextOnlyError("OFFLINE_MODELS_MISSING")


def offline_model_directory(root: Path, model_name: str, explicit: str | None = None) -> str:
    try:
        directory = Path(explicit).resolve() if explicit else (root / model_name).resolve()
        if not directory.is_relative_to(root):
            raise TextOnlyError("OFFLINE_MODELS_MISSING")
        for name in ("inference.json", "inference.yml", "inference.pdiparams"):
            model_file = directory / name
            if not model_file.resolve().is_relative_to(directory) or not model_file.is_file() or model_file.stat().st_size == 0:
                raise TextOnlyError("OFFLINE_MODELS_MISSING")
        return str(directory)
    except (OSError, ValueError):
        raise TextOnlyError("OFFLINE_MODELS_MISSING")


def extract_pdf_text_layer(
    pdf_path: Path, artifacts_dir: Path, text_only: bool = False,
) -> tuple[list[dict[str, object]], list[str], int]:
    reader = PdfReader(str(pdf_path))
    if text_only:
        validate_text_only_page_count(len(reader.pages), "PDF")
    pdf = pdfium.PdfDocument(str(pdf_path))
    pages: list[dict[str, object]] = []
    artifacts: list[str] = []
    total_chars = 0

    for index, page in enumerate(reader.pages, start=1):
        pdf_page = pdf[index - 1]
        if text_only:
            # Preflight the existing 2x preview/OCR raster before allocating its bitmap.
            validate_text_only_page_pixels(pdf_page.get_width(), pdf_page.get_height(), f"PDF page {index}", scale=2.0)
        text = (page.extract_text() or "").strip()
        width = int(pdf_page.get_width())
        height = int(pdf_page.get_height())
        text_file = artifacts_dir / "text-layer" / f"page-{index:04d}.txt"
        text_file.parent.mkdir(parents=True, exist_ok=True)
        text_file.write_text(text, encoding="utf-8")
        artifacts.append(str(text_file))
        total_chars += len(text)

        text_blocks: list[dict[str, object]] = []
        if text:
            lines = [line.strip() for line in text.splitlines() if line.strip()]
            if lines:
                line_height = max(16.0, height / max(len(lines) + 2, 3))
                top_padding = line_height * 0.75
                left = max(width * 0.04, 12.0)
                right = max(left + 12.0, width * 0.96)
                for line_index, line_text in enumerate(lines, start=1):
                    top = min(height - 2.0, top_padding + (line_index - 1) * line_height)
                    bottom = min(height, top + line_height * 0.82)
                    text_blocks.append(
                        {
                            "Id": f"text-layer-{index:04d}-{line_index:04d}",
                            "Text": line_text,
                            "Confidence": 1.0,
                            "Polygon": [
                                {"X": float(left), "Y": float(top)},
                                {"X": float(right), "Y": float(top)},
                                {"X": float(right), "Y": float(bottom)},
                                {"X": float(left), "Y": float(bottom)},
                            ],
                        }
                    )
            else:
                text_blocks = [
                    {
                        "Id": f"text-layer-{index:04d}",
                        "Text": text,
                        "Confidence": 1.0,
                        "Polygon": build_full_page_polygon(width, height),
                    }
                ]

        pages.append(
            {
                "PageNumber": index,
                "Width": width,
                "Height": height,
                "TextBlocks": text_blocks,
                "TableBlocks": [],
            }
        )

    return pages, artifacts, total_chars


def assess_text_layer_quality(pages: list[dict[str, object]]) -> tuple[bool, str | None]:
    text = "\n".join(
        block.get("Text", "")
        for page in pages
        for block in page.get("TextBlocks", [])
        if block.get("Text")
    ).strip()

    if len(text) < 80:
        return False, "TEXT_LAYER_EMPTY"

    letters = [char for char in text if char.isalpha()]
    if not letters:
        return False, "TEXT_LAYER_EMPTY"

    supported_letters = sum(
        1
        for char in letters
        if ("A" <= char <= "Z")
        or ("a" <= char <= "z")
        or ("\u0400" <= char <= "\u04FF")
    )
    supported_ratio = supported_letters / len(letters)
    suspicious_markers = len(re.findall(r"[ÐÑÏðÒÓÕÖ×ØÙÚÛÜÝÞßàáâãäåæçèéêëìíîï]", text))

    if supported_ratio < 0.75 or suspicious_markers >= max(12, len(text) // 80):
        return False, "TEXT_LAYER_SUSPICIOUS_ENCODING"

    return True, None


def should_use_text_layer_fast_path(pages: list[dict[str, object]]) -> tuple[bool, str | None]:
    lines = [
        str(block.get("Text", "")).strip()
        for page in pages
        for block in page.get("TextBlocks", [])
        if str(block.get("Text", "")).strip()
    ]
    if not lines:
        return False, "TEXT_LAYER_EMPTY"

    normalized_lines = [line.upper() for line in lines]
    if any("СОПРОВОДИТЕЛЬНАЯ НАКЛАДНАЯ НА ТОВАРЫ" in line for line in normalized_lines) or any("KZ-SNT-" in line for line in lines):
        return True, None

    line_count = len(lines)
    section_markers = sum(1 for line in lines if re.match(r"^\d{1,2}\.", line))
    transport_markers = sum(
        1
        for line in lines
        if re.search(
            r"(ТРАНСПОРТНАЯ НАКЛАДНАЯ|ГРУЗООТПРАВИТЕЛ|ГРУЗОПОЛУЧАТЕЛ|ПЕРЕВОЗЧИК|ЭКЗЕМПЛЯР|ПРИЛОЖЕНИЕ)",
            line,
            re.IGNORECASE,
        )
    )

    if line_count >= 60 and section_markers >= 6 and transport_markers >= 3:
        return False, "TEXT_LAYER_LAYOUT_AMBIGUOUS"

    return True, None


def quality_from_blocks(blocks: list[dict[str, object]]) -> dict[str, float]:
    if not blocks:
        return {
            "score": 0.0,
            "avg_confidence": 0.0,
            "low_conf_ratio": 1.0,
            "multi_char_ratio": 0.0,
            "char_density": 0.0,
            "block_count": 0.0,
        }

    texts = [str(block.get("Text", "")).strip() for block in blocks]
    confidences = [float(block.get("Confidence", 0.0)) for block in blocks]
    low_conf_ratio = sum(1 for value in confidences if value < 0.75) / len(confidences)
    alpha_num_lengths = [len(re.sub(r"\W+", "", text, flags=re.UNICODE)) for text in texts]
    multi_char_ratio = sum(1 for length in alpha_num_lengths if length >= 2) / len(alpha_num_lengths)
    char_density = min(sum(len(text) for text in texts) / 180.0, 1.0)
    avg_confidence = sum(confidences) / len(confidences)
    score = (
        avg_confidence * 0.45
        + (1.0 - low_conf_ratio) * 0.20
        + multi_char_ratio * 0.20
        + char_density * 0.15
    )
    return {
        "score": round(score, 6),
        "avg_confidence": round(avg_confidence, 6),
        "low_conf_ratio": round(low_conf_ratio, 6),
        "multi_char_ratio": round(multi_char_ratio, 6),
        "char_density": round(char_density, 6),
        "block_count": float(len(blocks)),
    }


def assess_image_quality(image_path: Path) -> dict[str, float]:
    with Image.open(image_path) as source_image:
        grayscale = source_image.convert("L")
        width, height = grayscale.size
        resized = grayscale.copy()
        longest_edge = max(width, height)
        if longest_edge > 1600:
            scale = 1600 / float(longest_edge)
            resized = grayscale.resize((max(1, int(width * scale)), max(1, int(height * scale))))

        stats = ImageStat.Stat(resized)
        edges = resized.filter(ImageFilter.FIND_EDGES)
        edge_stats = ImageStat.Stat(edges)
        histogram = resized.histogram()
        pixel_count = max(1, sum(histogram))
        bright_ratio = sum(histogram[245:]) / pixel_count
        dark_ratio = sum(histogram[:12]) / pixel_count

        focus_score = float(edge_stats.var[0]) if edge_stats.var else 0.0
        contrast_score = float(stats.stddev[0]) if stats.stddev else 0.0
        brightness_mean = float(stats.mean[0]) if stats.mean else 0.0
        resolution_score = min((width * height) / 2_000_000.0, 1.0)
        normalized_focus = min(focus_score / 520.0, 1.0)
        normalized_contrast = min(contrast_score / 72.0, 1.0)
        normalized_exposure = 1.0 - min(abs(brightness_mean - 150.0) / 150.0, 1.0)
        normalized_glare = 1.0 - min(bright_ratio / 0.12, 1.0)
        quality_score = (
            normalized_focus * 0.35
            + normalized_contrast * 0.20
            + normalized_exposure * 0.15
            + normalized_glare * 0.15
            + resolution_score * 0.15
        )

    return {
        "width": float(width),
        "height": float(height),
        "focus_score": round(focus_score, 3),
        "contrast_score": round(contrast_score, 3),
        "brightness_mean": round(brightness_mean, 3),
        "bright_ratio": round(bright_ratio, 6),
        "dark_ratio": round(dark_ratio, 6),
        "resolution_score": round(resolution_score, 6),
        "quality_score": round(quality_score, 6),
    }


def build_quality_warnings(page_number: int, image_metrics: dict[str, float]) -> list[dict[str, str]]:
    warnings: list[dict[str, str]] = []
    if image_metrics["focus_score"] < 24.0:
        warnings.append(
            {
                "Code": "IMAGE_BLUR_RISK",
                "Message": (
                    f"Page {page_number}: low focus score {image_metrics['focus_score']:.1f}. "
                    "The photo looks blurred; route and vehicle fields may need review."
                ),
            }
        )

    if image_metrics["contrast_score"] < 28.0:
        warnings.append(
            {
                "Code": "IMAGE_LOW_CONTRAST",
                "Message": (
                    f"Page {page_number}: low contrast score {image_metrics['contrast_score']:.1f}. "
                    "Weak contrast can lower OCR quality on Russian text."
                ),
            }
        )

    if image_metrics["bright_ratio"] > 0.09:
        warnings.append(
            {
                "Code": "IMAGE_GLARE_RISK",
                "Message": (
                    f"Page {page_number}: glare ratio {image_metrics['bright_ratio']:.3f}. "
                    "Overexposed zones may need a better photo."
                ),
            }
        )

    if min(image_metrics["width"], image_metrics["height"]) < 1100.0:
        warnings.append(
            {
                "Code": "IMAGE_LOW_RESOLUTION",
                "Message": (
                    f"Page {page_number}: source resolution {int(image_metrics['width'])}x{int(image_metrics['height'])} "
                    "is low for reliable OCR on transport forms."
                ),
            }
        )

    return warnings


def block_centroid(block: dict[str, object]) -> tuple[float, float]:
    polygon = block.get("Polygon", [])
    if not polygon:
        return 0.0, 0.0
    return (
        sum(float(point["X"]) for point in polygon) / len(polygon),
        sum(float(point["Y"]) for point in polygon) / len(polygon),
    )


def relative_zone_blocks(page: dict[str, object], bounds: tuple[float, float, float, float]) -> list[dict[str, object]]:
    width = max(float(page.get("Width", 0)), 1.0)
    height = max(float(page.get("Height", 0)), 1.0)
    min_x, max_x, min_y, max_y = bounds
    selected: list[dict[str, object]] = []
    for block in page.get("TextBlocks", []):
        centroid_x, centroid_y = block_centroid(block)
        rel_x = centroid_x / width
        rel_y = centroid_y / height
        if min_x <= rel_x <= max_x and min_y <= rel_y <= max_y:
            selected.append(block)
    return selected


def crop_box_from_bounds(width: int, height: int, bounds: tuple[float, float, float, float]) -> tuple[int, int, int, int] | None:
    min_x, max_x, min_y, max_y = bounds
    left = max(0, min(width - 1, int(width * min_x)))
    top = max(0, min(height - 1, int(height * min_y)))
    right = max(left + 1, min(width, int(width * max_x)))
    bottom = max(top + 1, min(height, int(height * max_y)))
    if right - left < 32 or bottom - top < 24:
        return None
    return left, top, right, bottom


def try_get_field_marker(text: str) -> int | None:
    stripped = re.sub(r"\s+", " ", text).strip()
    if not stripped or len(stripped) > FIELD_MARKER_MAX_TEXT_LENGTH:
        return None

    match = re.match(r"^\s*(\d{1,2})", stripped)
    if not match:
        return None

    try:
        marker = int(match.group(1))
    except ValueError:
        return None

    return marker if 1 <= marker <= 29 else None


def detect_route_markers(page: dict[str, object]) -> list[dict[str, float | int | str]]:
    width = max(float(page.get("Width", 0)), 1.0)
    height = max(float(page.get("Height", 0)), 1.0)
    detected: dict[int, dict[str, float | int | str]] = {}

    for block in page.get("TextBlocks", []):
        polygon = block.get("Polygon", [])
        if not polygon:
            continue

        centroid_x, centroid_y = block_centroid(block)
        rel_x = centroid_x / width
        rel_y = centroid_y / height
        if rel_x > 0.20 or rel_y < 0.18 or rel_y > 0.50:
            continue

        marker = try_get_field_marker(str(block.get("Text", "")))
        if marker is None or marker not in {3, 4, 5, 6}:
            continue

        current = detected.get(marker)
        if current is None or float(current["rel_y"]) > rel_y:
            detected[marker] = {
                "marker": marker,
                "rel_x": rel_x,
                "rel_y": rel_y,
                "text": str(block.get("Text", "")).strip(),
            }

    return [detected[key] for key in sorted(detected)]


def clamp_relative_y(value: float) -> float:
    return min(0.48, max(0.16, value))


def midpoint(left: float, right: float) -> float:
    return (left + right) / 2.0


def build_dynamic_route_bounds(
    page: dict[str, object],
    field_key: str,
    fallback_bounds: tuple[float, float, float, float],
) -> tuple[tuple[float, float, float, float], dict[str, object]]:
    markers = detect_route_markers(page)
    if not markers:
        return fallback_bounds, {"mode": "fixed", "reason": "route-markers-missing"}

    by_marker = {int(item["marker"]): item for item in markers}
    target_marker = 4 if field_key == "ship_from" else 3
    target = by_marker.get(target_marker)
    previous = by_marker.get(target_marker - 1)
    next_marker = by_marker.get(target_marker + 1) or by_marker.get(target_marker + 2)

    min_x, max_x, fallback_min_y, fallback_max_y = fallback_bounds

    if target is not None:
        target_y = float(target["rel_y"])
        start_y = clamp_relative_y(
            midpoint(float(previous["rel_y"]), target_y) if previous is not None else target_y - 0.055
        )
        end_y = clamp_relative_y(
            midpoint(target_y, float(next_marker["rel_y"])) if next_marker is not None else target_y + 0.060
        )
        if end_y <= start_y + 0.028:
            end_y = clamp_relative_y(start_y + 0.055)

        return (
            (min_x, max(max_x, 0.50), start_y, end_y),
            {
                "mode": "dynamic",
                "field_key": field_key,
                "markers": markers,
                "target_marker": target_marker,
            },
        )

    if field_key == "ship_to" and by_marker.get(3) is not None and next_marker is not None:
        marker3_y = float(by_marker[3]["rel_y"])
        next_y = float(next_marker["rel_y"])
        start_y = clamp_relative_y(midpoint(marker3_y, next_y) - 0.006)
        end_y = clamp_relative_y(next_y - 0.006)
        if end_y > start_y + 0.028:
            return (
                (min_x, max(max_x, 0.50), start_y, end_y),
                {
                    "mode": "dynamic-inferred",
                    "field_key": field_key,
                    "markers": markers,
                    "target_marker": target_marker,
                },
            )

    return fallback_bounds, {"mode": "fixed", "reason": "route-target-marker-missing", "markers": markers}


def resolve_zone_bounds(
    page: dict[str, object],
    zone: dict[str, object],
) -> tuple[tuple[float, float, float, float], dict[str, object]]:
    fallback_bounds = zone["bounds"]
    field_key = str(zone.get("field_key") or "")
    if field_key not in {"ship_from", "ship_to"}:
        return fallback_bounds, {"mode": "fixed"}

    return build_dynamic_route_bounds(page, field_key, fallback_bounds)


def should_retry_zone(
    page_metrics: dict[str, float],
    zone_metrics: dict[str, float],
    zone: dict[str, object],
) -> bool:
    return (
        page_metrics["quality_score"] < 0.62
        or zone_metrics["block_count"] < float(zone["min_block_count"])
        or zone_metrics["score"] < float(zone["min_quality_score"])
        or zone_metrics["low_conf_ratio"] > 0.22
    )


def cmr_number_zone_requires_retry(blocks: list[dict[str, object]]) -> bool:
    for block in blocks:
        compact = re.sub(r"\s+", "", str(block.get("Text", "")).upper())
        match = CMR_MERGED_NUMBER_AMBIGUITY_REGEX.search(compact)
        if match is not None and match.group("number").endswith(("5", "6")):
            return True

    return False


def remap_crop_blocks_to_page(
    crop_page: dict[str, object],
    offset_x: int,
    offset_y: int,
    full_width: int,
    full_height: int,
    zone_name: str,
    scale: float = 1.0,
) -> list[dict[str, object]]:
    coordinate_scale = max(float(scale), 1e-9)
    remapped: list[dict[str, object]] = []
    for index, block in enumerate(crop_page.get("TextBlocks", []), start=1):
        remapped.append(
            {
                "Id": f"zone-{zone_name}-{index:04d}",
                "Text": block.get("Text", ""),
                "Confidence": float(block.get("Confidence", 0.0)),
                "Polygon": [
                    {
                        "X": min(max(float(point["X"]) / coordinate_scale + offset_x, 0.0), float(full_width)),
                        "Y": min(max(float(point["Y"]) / coordinate_scale + offset_y, 0.0), float(full_height)),
                    }
                    for point in block.get("Polygon", [])
                ],
            }
        )
    return remapped


def zone_candidate_has_route_signal(text: str) -> bool:
    normalized = text.upper()
    return bool(
        re.search(
            r"(КАЗАХ|РОСС|ШЫМК|АКТОБ|ШАЛКАР|КОСТАН|САЛАВ|ОРСК|ОРЕНБУР|\bГ\.\b|\bУЛ\.\b|\b\d{5,6}\b)",
            normalized,
        )
    )

def _legacy_route_line_looks_like_noise(text: str) -> bool:
    normalized = text.upper()
    if len(re.sub(r"\W+", "", normalized, flags=re.UNICODE)) < 3:
        return True

    return bool(
        re.search(
            r"(РџРђРЎРџРћР Рў|РљРђР§Р•РЎРўР’|Р”Р•РљР›РђР |РЎР§Р•Рў|Р‘РРќ|РРќРќ|РўРћРћ|РћРћРћ|NR|РИД|ЕAЭС|EAЭС|\bOT\s+\d{2}\.\d{2}\.\d{4}\b)",
            normalized,
        )
    )


def _legacy_route_line_has_signal(text: str) -> bool:
    normalized = text.upper()
    return bool(
        re.search(
            r"(Р Р•РЎРџРЈР‘Р›РРљ|РљРђР—РђРҐРЎРўРђРќ|Р РћРЎРЎРР™РЎРљРђРЇ|Р¤Р•Р”Р•Р РђР¦РРЇ|РћР‘Р›РђРЎРўР¬|Р РђР™РћРќ|Р РђР—РЄР•Р—Р”|\bР“\.\b|\bРЈР›\.\b|\b\d{5,6}\b|РЁРђР›РљРђР |РЁР«РњРљ|РђРљРўРћР‘|РЎРђР›РђР’|РљРћРЎРўРђРќ|РћР РЎРљ|РћР Р•РќР‘РЈР )",
            normalized,
        )
    )


def _legacy_build_route_candidate_snippet(blocks: list[dict[str, object]]) -> str:
    selected: list[str] = []

    for block in blocks[:8]:
        text = str(block.get("Text", "")).strip()
        if not text or route_line_looks_like_noise(text):
            continue

        if route_line_has_signal(text) or (
            selected and re.search(r"(Р Р•РЎРџРЈР‘Р›РРљ|РљРђР—РђРҐРЎРўРђРќ|Р РћРЎРЎРР™РЎРљРђРЇ|Р¤Р•Р”Р•Р РђР¦РРЇ)", text.upper())
        ):
            selected.append(text)

    if not selected:
        return ""

    compact: list[str] = []
    for text in selected:
        if compact and text.upper() in {"Р Р•РЎРџРЈР‘Р›РРљРђ", "РљРђР—РђРҐРЎРўРђРќ"}:
            compact[-1] = f"{compact[-1]} {text}"
            continue
        compact.append(text)

    return " | ".join(compact[:4])


ROUTE_TEXT_HINTS = (
    "республика",
    "казахстан",
    "россий",
    "федерац",
    "область",
    "район",
    "разъезд",
    "г.",
    "город",
    "ул.",
    "улица",
    "просп",
    "шоссе",
)
ROUTE_NOISE_HINTS = (
    "паспорт",
    "декларац",
    "соответств",
    "контракт",
    "счет",
    "счёт",
    "invoice",
    "eaэс",
    "eac",
    "nrid",
    "unterschrift",
    "stempel",
    "signature",
    "stamp",
    "loading",
    "delivery",
    "consignor",
    "consignee",
    "оплач",
    "отчисл",
)


def route_line_looks_like_noise(text: str) -> bool:
    normalized = text.lower().strip()
    if len(re.sub(r"\W+", "", normalized, flags=re.UNICODE)) < 3:
        return True

    if any(hint in normalized for hint in ROUTE_NOISE_HINTS):
        return True

    if re.search(r"\b(?:cmr|nr)\b", normalized):
        return True

    if re.search(r"\b(?:от|ot)\s+\d{2}\.\d{2}\.\d{4}\b", normalized):
        return True

    if re.fullmatch(r"[a-z]{4,}(?:[/-][a-z]{3,})?", normalized):
        return True

    return False


def route_line_has_signal(text: str) -> bool:
    normalized = text.lower().strip()
    return zone_candidate_has_route_signal(text) or any(hint in normalized for hint in ROUTE_TEXT_HINTS)


def build_route_candidate_snippet(blocks: list[dict[str, object]]) -> str:
    selected: list[str] = []
    signal_seen = False

    for block in blocks[:8]:
        text = str(block.get("Text", "")).strip()
        if not text or route_line_looks_like_noise(text):
            continue

        has_signal = route_line_has_signal(text)
        looks_like_continuation = signal_seen and not re.fullmatch(r"[A-Za-z]{4,}(?:[/-][A-Za-z]{3,})?", text)
        if has_signal or looks_like_continuation:
            selected.append(text)
            signal_seen = signal_seen or has_signal

    if not selected:
        return ""

    compact: list[str] = []
    for text in selected:
        lowered = text.lower()
        if compact and lowered in {"казахстан", "федерация"}:
            compact[-1] = f"{compact[-1]} {text}"
            continue
        if compact and compact[-1].casefold() == text.casefold():
            continue
        compact.append(text)

    return " | ".join(compact[:4])


def merge_zone_blocks(
    page: dict[str, object],
    bounds: tuple[float, float, float, float],
    replacement_blocks: list[dict[str, object]],
) -> dict[str, object]:
    width = max(float(page.get("Width", 0)), 1.0)
    height = max(float(page.get("Height", 0)), 1.0)
    min_x, max_x, min_y, max_y = bounds
    retained = []
    for block in page.get("TextBlocks", []):
        centroid_x, centroid_y = block_centroid(block)
        rel_x = centroid_x / width
        rel_y = centroid_y / height
        if not (min_x <= rel_x <= max_x and min_y <= rel_y <= max_y):
            retained.append(block)

    retained.extend(replacement_blocks)
    retained.sort(
        key=lambda block: (
            min((float(point["Y"]) for point in block.get("Polygon", [])), default=0.0),
            min((float(point["X"]) for point in block.get("Polygon", [])), default=0.0),
        )
    )
    page["TextBlocks"] = retained
    return page


def build_zone_crop_image(
    image_path: Path,
    crop_box: tuple[int, int, int, int],
    zone_name: str,
    page_number: int,
    artifacts_dir: Path,
) -> tuple[Path, float]:
    left, top, right, bottom = crop_box
    artifacts_dir.mkdir(parents=True, exist_ok=True)
    output_path = artifacts_dir / f"page-{page_number:04d}-{zone_name}.jpg"

    with Image.open(image_path) as source_image:
        crop = source_image.convert("RGB").crop((left, top, right, bottom))
        crop_width, crop_height = crop.size
        scale = max(1.0, 1200.0 / max(crop_width, crop_height), 520.0 / max(1.0, min(crop_width, crop_height)))
        if scale > 1.05:
            resampling = Image.Resampling.LANCZOS if hasattr(Image, "Resampling") else Image.LANCZOS
            crop = crop.resize((max(1, int(crop_width * scale)), max(1, int(crop_height * scale))), resampling)
        crop.save(output_path, format="JPEG", quality=95)

    return output_path, scale


def sort_blocks_reading_order(blocks: list[dict[str, object]]) -> list[dict[str, object]]:
    return sorted(
        blocks,
        key=lambda block: (
            min((float(point["Y"]) for point in block.get("Polygon", [])), default=0.0),
            min((float(point["X"]) for point in block.get("Polygon", [])), default=0.0),
        ),
    )


def normalize_plate_scan_text(text: str) -> str:
    upper = text.upper().translate(CYRILLIC_TO_PLATE_LATIN)
    return re.sub(r"[^A-Z0-9]+", " ", upper)


def compact_plate_token(token: str) -> str:
    compact = re.sub(r"[^A-Z0-9]+", "", token.upper().translate(CYRILLIC_TO_PLATE_LATIN))
    compact = re.sub(r"^(\d{3}[A-Z]{2,3})O([1-9])$", r"\g<1>0\2", compact)
    compact = re.sub(r"^(\d{2}[A-Z]{3})O([1-9])$", r"\g<1>0\2", compact)
    return compact


def is_likely_rf_region_code(region: str) -> bool:
    if len(region) == 2 and region.isdigit():
        return 1 <= int(region) <= 99
    return len(region) == 3 and region in VALID_THREE_DIGIT_RF_REGION_CODES


def classify_plate_token(compact: str) -> str | None:
    truck_match = RF_TRUCK_PLATE_REGEX.fullmatch(compact)
    if truck_match and is_likely_rf_region_code(truck_match.group("region")):
        return "rf_truck"
    if LEGACY_TRUCK_PLATE_REGEX.fullmatch(compact):
        return "legacy_truck"
    if KZ_TRUCK_PLATE_REGEX.fullmatch(compact):
        return "kz_truck"
    if KZ_TRAILER_PLATE_REGEX.fullmatch(compact):
        return "kz_trailer"
    trailer_match = RF_TRAILER_PLATE_REGEX.fullmatch(compact)
    if trailer_match and is_likely_rf_region_code(trailer_match.group("region")):
        return "rf_trailer"
    return None


def normalize_date_target_token(value: str) -> str:
    return re.sub(r"[\s./-]+", ".", value.strip()).strip(".")


def iter_field_plate_targets(blocks: list[dict[str, object]]) -> list[dict[str, object]]:
    targets: list[dict[str, object]] = []
    for block_index, block in enumerate(sort_blocks_reading_order(blocks), start=1):
        text = str(block.get("Text", "")).strip()
        if not text:
            continue

        line_lower = text.lower()
        confidence = float(block.get("Confidence", 0.0))
        scan_text = normalize_plate_scan_text(text)
        tokens = re.findall(r"[A-Z0-9]+", scan_text)
        seen_compacts: set[str] = set()
        token_index = 0
        for index in range(len(tokens)):
            combos = [
                tokens[index],
                tokens[index] + tokens[index + 1] if index + 1 < len(tokens) else "",
                tokens[index] + tokens[index + 1] + tokens[index + 2] if index + 2 < len(tokens) else "",
                (
                    tokens[index] + tokens[index + 1] + tokens[index + 2] + tokens[index + 3]
                    if index + 3 < len(tokens)
                    else ""
                ),
            ]
            for compact in combos:
                if len(compact) < 7 or len(compact) > 10:
                    continue

                normalized_plate = compact_plate_token(compact)
                if normalized_plate in seen_compacts:
                    continue
                kind = classify_plate_token(normalized_plate)
                if kind is None:
                    continue

                seen_compacts.add(normalized_plate)
                token_index += 1
                field_key = "truck_plate" if kind in {"rf_truck", "legacy_truck", "kz_truck"} else "trailer_plate"
                has_field_hint = (
                    any(hint in line_lower for hint in TRUCK_HINTS)
                    if field_key == "truck_plate"
                    else any(hint in line_lower for hint in TRAILER_HINTS)
                )
                targets.append(
                    {
                        "field_key": field_key,
                        "raw_value": normalized_plate,
                        "normalized_value": normalized_plate,
                        "confidence": confidence,
                        "block": block,
                        "block_index": block_index,
                        "token_index": token_index,
                        "snippet": text,
                        "has_field_hint": has_field_hint,
                        "kind": kind,
                    }
                )
    return targets


def select_field_target_block(blocks: list[dict[str, object]], field_key: str) -> dict[str, object] | None:
    field = str(field_key or "").strip().lower()
    sorted_blocks = sort_blocks_reading_order(blocks)

    if field == "load_date":
        date_targets: list[dict[str, object]] = []
        for block_index, block in enumerate(sorted_blocks, start=1):
            text = str(block.get("Text", "")).strip()
            if not text:
                continue
            confidence = float(block.get("Confidence", 0.0))
            for token_index, match in enumerate(DATE_TOKEN_REGEX.finditer(text), start=1):
                raw_value = normalize_date_target_token(match.group(0))
                date_targets.append(
                    {
                        "field_key": "load_date",
                        "raw_value": raw_value,
                        "normalized_value": raw_value,
                        "confidence": confidence,
                        "block": block,
                        "block_index": block_index,
                        "token_index": token_index,
                        "snippet": text,
                        "selection_reason": "date-token-high-confidence",
                    }
                )
        if not date_targets:
            return None
        return max(
            date_targets,
            key=lambda target: (
                float(target.get("confidence") or 0.0),
                int(target.get("block_index") or 0),
                int(target.get("token_index") or 0),
            ),
        )

    if field not in {"truck_plate", "trailer_plate"}:
        return None

    all_plate_targets = iter_field_plate_targets(sorted_blocks)
    plate_targets = [target for target in all_plate_targets if target["field_key"] == field]
    if not plate_targets:
        return None

    hinted_targets = [target for target in plate_targets if bool(target.get("has_field_hint"))]
    if hinted_targets:
        selected = max(
            hinted_targets,
            key=lambda target: (
                float(target.get("confidence") or 0.0),
                int(target.get("block_index") or 0),
                int(target.get("token_index") or 0),
            ),
        )
        selected["selection_reason"] = "field-hint"
    elif field == "trailer_plate":
        selected = max(
            plate_targets,
            key=lambda target: (
                int(target.get("block_index") or 0),
                int(target.get("token_index") or 0),
                float(target.get("confidence") or 0.0),
            ),
        )
        selected["selection_reason"] = "reading-order-last-plate"
    else:
        selected = min(
            plate_targets,
            key=lambda target: (
                int(target.get("block_index") or 0),
                int(target.get("token_index") or 0),
                -float(target.get("confidence") or 0.0),
            ),
        )
        selected["selection_reason"] = "reading-order-first-plate"

    selected["competing_plate_count"] = len(all_plate_targets)
    return selected


def normalize_driver_name(value: str) -> str:
    cleaned = re.sub(r"\s+", " ", value).strip(" ,;:.")
    return " ".join(part[:1].upper() + part[1:].lower() if len(part) > 1 else part.upper() for part in cleaned.split())


def parse_weight_tons(value: str, line_hint: str) -> tuple[float, str] | None:
    raw_value = value.strip()
    normalized = value.replace(",", ".")
    try:
        numeric = float(normalized)
    except ValueError:
        return None

    if numeric <= 0:
        return None

    hint = line_hint.lower()
    if "kg" in hint or "кг" in hint or numeric > 200:
        if numeric <= 80 and re.fullmatch(r"\d{1,2}[.,]\d{3}", raw_value):
            formatted = f"{numeric:.3f}".rstrip("0").rstrip(".")
            return numeric, formatted
        if numeric > 80_000:
            return None
        numeric = numeric / 1000.0

    if numeric < 0.3 or numeric > 80:
        return None

    formatted = f"{numeric:.3f}".rstrip("0").rstrip(".")
    return numeric, formatted


def normalized_bbox_from_blocks(
    blocks: list[dict[str, object]],
    page_width: int,
    page_height: int,
) -> dict[str, float] | None:
    all_points = [
        point
        for block in blocks
        for point in block.get("Polygon", [])
        if isinstance(point, dict) and "X" in point and "Y" in point
    ]
    if not all_points:
        return None

    min_x = min(float(point["X"]) for point in all_points)
    max_x = max(float(point["X"]) for point in all_points)
    min_y = min(float(point["Y"]) for point in all_points)
    max_y = max(float(point["Y"]) for point in all_points)
    width = max(1.0, float(page_width))
    height = max(1.0, float(page_height))

    x = min(max(min_x / width, 0.0), 1.0)
    y = min(max(min_y / height, 0.0), 1.0)
    w = min(max((max_x - min_x) / width, 0.0), 1.0 - x)
    h = min(max((max_y - min_y) / height, 0.0), 1.0 - y)
    if w <= 0.0 or h <= 0.0:
        return None

    return {
        "X": round(x, 6),
        "Y": round(y, 6),
        "Width": round(w, 6),
        "Height": round(h, 6),
    }


def normalized_bbox_from_crop_box(
    crop_box: tuple[int, int, int, int],
    page_width: int,
    page_height: int,
) -> dict[str, float] | None:
    left, top, right, bottom = crop_box
    width = max(1.0, float(page_width))
    height = max(1.0, float(page_height))
    x = min(max(left / width, 0.0), 1.0)
    y = min(max(top / height, 0.0), 1.0)
    w = min(max((right - left) / width, 0.0), 1.0 - x)
    h = min(max((bottom - top) / height, 0.0), 1.0 - y)
    if w <= 0.0 or h <= 0.0:
        return None

    return {
        "X": round(x, 6),
        "Y": round(y, 6),
        "Width": round(w, 6),
        "Height": round(h, 6),
    }


def block_xyxy(block: dict[str, object]) -> tuple[float, float, float, float] | None:
    points = [
        point
        for point in block.get("Polygon", [])
        if isinstance(point, dict) and "X" in point and "Y" in point
    ]
    if not points:
        return None

    return (
        min(float(point["X"]) for point in points),
        min(float(point["Y"]) for point in points),
        max(float(point["X"]) for point in points),
        max(float(point["Y"]) for point in points),
    )


def find_transport_invoice_weight_marker_bounds(page: dict[str, object]) -> tuple[float, float] | None:
    marker_box: tuple[float, float, float, float] | None = None
    for block in page.get("TextBlocks", []):
        text = str(block.get("Text", "")).lower()
        if "масса груза брутто" not in text:
            continue

        box = block_xyxy(block)
        if box is None:
            continue
        marker_box = box
        break

    if marker_box is None:
        return None

    _, marker_top, _, marker_bottom = marker_box
    bottom = marker_bottom
    for block in page.get("TextBlocks", []):
        box = block_xyxy(block)
        if box is None:
            continue
        _, top, _, block_bottom = box
        if marker_top <= top <= marker_top + 140:
            text = str(block.get("Text", "")).lower()
            if "масса" in text or "взвешив" in text or "расчет" in text:
                bottom = max(bottom, block_bottom)

    return marker_top, bottom


def get_text_recognizer(model_name: str):
    recognizer = _TEXT_RECOGNITION_CACHE.get(model_name)
    if recognizer is not None:
        return recognizer

    from paddleocr import TextRecognition

    recognizer = TextRecognition(model_name=model_name, device="cpu")
    _TEXT_RECOGNITION_CACHE[model_name] = recognizer
    return recognizer


def recognize_weight_crop(crop_path: Path) -> tuple[str, float] | None:
    model_name = os.environ.get("FORMPOTOK_OCR_WEIGHT_REC_MODEL", "PP-OCRv5_server_rec")
    try:
        recognizer = get_text_recognizer(model_name)
        results = list(recognizer.predict(str(crop_path)))
    except Exception:
        return None

    if not results:
        return None

    result = results[0]
    text = str(result.get("rec_text", "")).strip()
    score = float(result.get("rec_score", 0.0))
    if not text:
        return None

    return text, score


def normalize_weight_recognition_text(value: str) -> str:
    return (
        value.upper()
        .replace("О", "0")
        .replace("O", "0")
        .replace("Q", "0")
        .replace("I", "1")
        .replace("L", "1")
        .replace("|", "1")
    )


def enumerate_weight_values_from_recognition(text: str) -> list[str]:
    normalized = normalize_weight_recognition_text(text)
    values = [match.group(1) for match in WEIGHT_NUMBER_REGEX.finditer(normalized)]
    digits = re.sub(r"\D", "", normalized)
    if 4 <= len(digits) <= 5:
        values.append(digits)
    return list(dict.fromkeys(values))


def build_handwritten_weight_crop_candidates(
    selected_path: Path,
    selected_variant_label: str,
    page: dict[str, object],
    page_number: int,
    artifacts_dir: Path,
) -> tuple[list[dict[str, object]], list[str]]:
    marker_bounds = find_transport_invoice_weight_marker_bounds(page)
    if marker_bounds is None:
        return [], []

    page_width = int(page["Width"])
    page_height = int(page["Height"])
    marker_top, marker_bottom = marker_bounds
    bounds = [
        # In the current TN form the handwritten kg value is usually in the row
        # above the "mass gross/net" label, not below that label.
        (0.430, 0.690, (marker_top - 180) / page_height, (marker_top + 20) / page_height),
        (0.470, 0.735, (marker_top - 160) / page_height, (marker_top + 30) / page_height),
        (0.500, 0.765, (marker_top - 145) / page_height, (marker_top + 40) / page_height),
        (0.450, 0.562, marker_bottom / page_height + 0.010, marker_bottom / page_height + 0.033),
        (0.440, 0.575, marker_bottom / page_height + 0.006, marker_bottom / page_height + 0.035),
        (0.430, 0.600, marker_bottom / page_height + 0.005, marker_bottom / page_height + 0.038),
    ]

    zone_dir = artifacts_dir / "paddle" / "zones"
    zone_dir.mkdir(parents=True, exist_ok=True)
    artifacts: list[str] = []
    best_candidate: tuple[float, dict[str, object]] | None = None
    source_paths = [(selected_variant_label, selected_path)]
    if selected_variant_label == "rot90":
        opposite_path = selected_path.with_name(selected_path.name.replace("-rot90", "-rot270"))
        if opposite_path.exists():
            source_paths.append(("rot270", opposite_path))
    elif selected_variant_label == "rot270":
        opposite_path = selected_path.with_name(selected_path.name.replace("-rot270", "-rot90"))
        if opposite_path.exists():
            source_paths.append(("rot90", opposite_path))

    for source_index, (source_variant_label, source_path) in enumerate(source_paths, start=1):
        with Image.open(source_path) as source_image:
            image = source_image.convert("RGB")
            for index, bound in enumerate(bounds, start=1):
                crop_box = crop_box_from_bounds(page_width, page_height, bound)
                if crop_box is None:
                    continue

                crop_path = zone_dir / f"page-{page_number:04d}-transport_weight-handwritten-{source_index}-{index}.jpg"
                image.crop(crop_box).save(crop_path, format="JPEG", quality=95)
                artifacts.append(str(crop_path))

                recognized = recognize_weight_crop(crop_path)
                if recognized is None:
                    continue

                recognized_text, recognized_score = recognized
                for raw_value in enumerate_weight_values_from_recognition(recognized_text):
                    parsed = parse_weight_tons(raw_value, f"{recognized_text} kg handwritten gross weight")
                    if parsed is None:
                        continue

                    numeric, formatted_value = parsed
                    if numeric < 10 or numeric > 45:
                        continue

                    score = recognized_score + (0.25 if len(re.sub(r"\D", "", raw_value)) == 5 else 0.0)
                    candidate = {
                        "FieldKey": "transport_weight_tons",
                        "RawValue": raw_value,
                        "NormalizedValue": formatted_value,
                        "Confidence": round(recognized_score, 6),
                        "PageNumber": page_number,
                        "Snippet": f"handwritten gross weight crop: {recognized_text}; масса груза брутто кг",
                        "SourceType": "zone-second-pass-handwritten-weight",
                        "RequiresReview": True,
                        "bbox_normalized": normalized_bbox_from_crop_box(crop_box, page_width, page_height),
                        "_bbox_variant_label": source_variant_label,
                    }
                    if best_candidate is None or score > best_candidate[0]:
                        best_candidate = (score, candidate)

    return ([best_candidate[1]] if best_candidate is not None else []), artifacts


def build_field_specific_crop_candidates(
    zone_name: str,
    blocks: list[dict[str, object]],
    page_number: int,
    page_width: int,
    page_height: int,
    source_type: str = "zone-second-pass-crop",
) -> list[dict[str, object]]:
    zone_lower = zone_name.lower()
    sorted_blocks = sort_blocks_reading_order(blocks)
    candidates: list[dict[str, object]] = []

    if zone_lower == "cmr_number":
        best_candidate: tuple[float, dict[str, object]] | None = None
        for block_index, block in enumerate(sorted_blocks, start=1):
            text = str(block.get("Text", "")).strip()
            match = CMR_NUMBER_CANDIDATE_REGEX.search(text)
            if match is None:
                continue

            normalized = re.sub(r"\s+", "", match.group("number")).replace("\\", "/")
            normalized = re.sub(r"^[OО](?=\d)", "", normalized, flags=re.IGNORECASE)
            if re.fullmatch(r"\d{2,4}/[A-ZА-Я0-9]{1,2}", normalized, re.IGNORECASE) is None:
                continue

            confidence = float(block.get("Confidence", 0.0))
            candidate = {
                "FieldKey": "cmr_number",
                "RawValue": match.group("number").strip(),
                "NormalizedValue": normalized,
                "Confidence": round(confidence, 6),
                "PageNumber": page_number,
                "Snippet": text,
                "SourceType": source_type,
                "RequiresReview": True,
                "bbox_normalized": normalized_bbox_from_blocks([block], page_width, page_height),
                "SelectionReason": "cmr-header-number-marker",
                "BlockIndex": block_index,
            }
            if best_candidate is None or confidence > best_candidate[0]:
                best_candidate = (confidence, candidate)

        if best_candidate is not None:
            candidates.append(best_candidate[1])

    if zone_lower == "driver":
        best_driver: dict[str, object] | None = None
        for block in sorted_blocks:
            text = str(block.get("Text", "")).strip()
            if not text:
                continue

            line_lower = text.lower()
            if any(stop in line_lower for stop in DRIVER_LINE_STOP_HINTS):
                continue
            has_driver_hint = any(hint in line_lower for hint in DRIVER_LINE_HINTS)
            has_standalone_driver_shape = (
                DRIVER_NAME_REGEX.search(text) is not None
                and re.search(r"[\u0400-\u04FF]", text) is not None
            )
            if not has_driver_hint and not has_standalone_driver_shape:
                continue

            cleaned = re.sub(r"\b(?:водитель|фио|driver)\b[:\s-]*", "", text, flags=re.IGNORECASE).strip()
            if not cleaned:
                continue

            for match in DRIVER_NAME_REGEX.finditer(cleaned):
                value = normalize_driver_name(match.group(0))
                token_count = len(value.split())
                if token_count < 2:
                    continue
                if not has_driver_hint:
                    if token_count != 3:
                        continue
                    if not re.search(r"[\u0400-\u04FF]", value):
                        continue
                    if re.search(
                        r"\b(?:\u043E\u0431\u043B\u0430\u0441\u0442\u044C|\u0440\u0435\u0441\u043F\u0443\u0431\u043B\u0438\u043A\u0430|\u0440\u0430\u0439\u043E\u043D|\u0433\u043E\u0440\u043E\u0434|\u043E\u0440\u0441\u043A|\u043A\u0430\u0437\u0430\u0445\u0441\u0442\u0430\u043D)\b",
                        line_lower,
                        re.IGNORECASE,
                    ):
                        continue
                    if not re.search(
                        r"(?:\u043E\u0432\u0438\u0447|\u0435\u0432\u0438\u0447|\u0438\u0447|\u0432\u043D\u0430|\u0438\u0447\u043D\u0430|\u0443\u043B\u044B|\u043A\u044B\u0437\u044B)$",
                        value.split()[-1],
                        re.IGNORECASE,
                    ):
                        continue
                    if float(block.get("Confidence", 0.0)) < 0.62:
                        continue
                if re.search(r"\d", value):
                    continue
                if len(value) < 8 or len(value) > 70:
                    continue

                confidence = float(block.get("Confidence", 0.0))
                score = confidence + (0.08 if has_driver_hint else 0.03) + token_count * 0.01
                if best_driver is None or score > float(best_driver["score"]):
                    best_driver = {
                        "score": score,
                        "candidate": {
                            "FieldKey": "driver_name",
                            "RawValue": match.group(0).strip(),
                            "NormalizedValue": value,
                            "Confidence": round(confidence, 6),
                            "PageNumber": page_number,
                            "Snippet": text,
                            "SourceType": source_type,
                            "RequiresReview": True,
                            "bbox_normalized": normalized_bbox_from_blocks([block], page_width, page_height),
                        },
                    }

        if best_driver is not None:
            candidates.append(best_driver["candidate"])

    if zone_lower == "load_date":
        target = select_field_target_block(sorted_blocks, "load_date")
        if target is not None:
            block = target["block"]
            confidence = float(target.get("confidence") or 0.0)
            candidates.append(
                {
                    "FieldKey": "load_date",
                    "RawValue": target["raw_value"],
                    "NormalizedValue": target["normalized_value"],
                    "Confidence": round(confidence, 6),
                    "PageNumber": page_number,
                    "Snippet": target["snippet"],
                    "SourceType": source_type,
                    "RequiresReview": True,
                    "bbox_normalized": normalized_bbox_from_blocks([block], page_width, page_height),
                    "SelectionReason": target.get("selection_reason"),
                    "BlockIndex": target.get("block_index"),
                    "TokenIndex": target.get("token_index"),
                }
            )

    if zone_lower == "vehicle":
        best_by_field: dict[str, tuple[float, dict[str, object]]] = {}

        for field_key in ("truck_plate", "trailer_plate"):
            target = select_field_target_block(sorted_blocks, field_key)
            if target is None:
                continue

            block = target["block"]
            confidence = float(target.get("confidence") or 0.0)
            candidate = {
                "FieldKey": field_key,
                "RawValue": target["raw_value"],
                "NormalizedValue": target["normalized_value"],
                "Confidence": round(confidence, 6),
                "PageNumber": page_number,
                "Snippet": target["snippet"],
                "SourceType": source_type,
                "RequiresReview": True,
                "bbox_normalized": normalized_bbox_from_blocks([block], page_width, page_height),
                "SelectionReason": target.get("selection_reason"),
                "BlockIndex": target.get("block_index"),
                "TokenIndex": target.get("token_index"),
                "CompetingPlateCount": target.get("competing_plate_count"),
            }
            best_by_field[field_key] = (confidence, candidate)

        for block in sorted_blocks:
            text = str(block.get("Text", "")).strip()
            if not text:
                continue

            line_lower = text.lower()
            confidence = float(block.get("Confidence", 0.0))

            if any(hint in line_lower for hint in WEIGHT_HINTS):
                for match in WEIGHT_NUMBER_REGEX.finditer(text):
                    parsed = parse_weight_tons(match.group(1), line_lower)
                    if parsed is None:
                        continue
                    _, formatted_value = parsed
                    score = confidence + 0.1
                    existing = best_by_field.get("transport_weight_tons")
                    candidate = {
                        "FieldKey": "transport_weight_tons",
                        "RawValue": match.group(1),
                        "NormalizedValue": formatted_value,
                        "Confidence": round(confidence, 6),
                        "PageNumber": page_number,
                        "Snippet": text,
                        "SourceType": source_type,
                        "RequiresReview": True,
                        "bbox_normalized": normalized_bbox_from_blocks([block], page_width, page_height),
                    }
                    if existing is None or score > existing[0]:
                        best_by_field["transport_weight_tons"] = (score, candidate)

        for _, candidate in best_by_field.values():
            candidates.append(candidate)

    if zone_lower == "transport_weight":
        best_weight: tuple[float, dict[str, object]] | None = None
        for block in sorted_blocks:
            text = str(block.get("Text", "")).strip()
            if not text:
                continue

            confidence = float(block.get("Confidence", 0.0))
            line_hint = f"{text} кг масса груза брутто"
            for match in WEIGHT_NUMBER_REGEX.finditer(text):
                parsed = parse_weight_tons(match.group(1), line_hint)
                if parsed is None:
                    continue
                numeric, formatted_value = parsed
                if numeric < 5 or numeric > 80:
                    continue

                score = confidence + (0.2 if "," in match.group(1) or "." in match.group(1) else 0.0)
                candidate = {
                    "FieldKey": "transport_weight_tons",
                    "RawValue": match.group(1),
                    "NormalizedValue": formatted_value,
                    "Confidence": round(confidence, 6),
                    "PageNumber": page_number,
                    "Snippet": text,
                    "SourceType": source_type,
                    "RequiresReview": True,
                    "bbox_normalized": normalized_bbox_from_blocks([block], page_width, page_height),
                }
                if best_weight is None or score > best_weight[0]:
                    best_weight = (score, candidate)

        if best_weight is not None:
            candidates.append(best_weight[1])

    return [candidate for candidate in candidates if candidate.get("bbox_normalized") is not None]


def create_paddle_ocr(
    profile: str,
    lang: str,
    detection_model_dir: str | None = None,
    recognition_model_dir: str | None = None,
    recognition_model_name: str | None = None,
    offline: bool = False,
    disable_document_unwarping: bool = False,
):
    det_path = detection_model_dir or os.environ.get("FORMPOTOK_OCR_DET_MODEL_DIR")
    rec_path = recognition_model_dir or os.environ.get("FORMPOTOK_OCR_REC_MODEL_DIR")
    det_dir = resolve_existing_path(det_path) if not offline else det_path
    rec_dir = resolve_existing_path(rec_path) if not offline else rec_path
    rec_name = (recognition_model_name or os.environ.get("FORMPOTOK_OCR_REC_MODEL_NAME") or "").strip() or None

    if profile == "mobile-cpu":
        kwargs = dict(
            device="cpu",
            lang=lang,
            text_detection_model_name="PP-OCRv5_mobile_det",
            text_detection_model_dir=det_dir,
            text_recognition_model_name=rec_name or "eslav_PP-OCRv5_mobile_rec",
            text_recognition_model_dir=rec_dir,
            use_doc_orientation_classify=True,
            use_doc_unwarping=True,
            use_textline_orientation=True,
        )
    elif profile == "default-cpu":
        kwargs = {
            "device": "cpu",
            "lang": lang,
            "text_detection_model_dir": det_dir,
            "text_recognition_model_dir": rec_dir,
        }
        if rec_name:
            kwargs["text_recognition_model_name"] = rec_name
    else:
        raise ValueError(f"Unsupported paddle profile: {profile}")

    if disable_document_unwarping:
        if not offline:
            raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
        kwargs["use_doc_unwarping"] = False

    with offline_paddle_runtime(offline):
        if offline:
            root = offline_model_root()
            for argument, name in (
                ("doc_orientation_classify_model_dir", "PP-LCNet_x1_0_doc_ori"),
                ("doc_unwarping_model_dir", "UVDoc"),
                ("textline_orientation_model_dir", "PP-LCNet_x1_0_textline_ori"),
            ):
                kwargs[argument] = offline_model_directory(root, name)
            if profile == "mobile-cpu":
                kwargs["text_detection_model_dir"] = offline_model_directory(root, "PP-OCRv5_mobile_det", det_path)
                kwargs["text_recognition_model_dir"] = offline_model_directory(root, rec_name or "eslav_PP-OCRv5_mobile_rec", rec_path)

        from paddleocr import PaddleOCR

        if offline and profile == "default-cpu":
            # Match the library's language selection only when legacy would use it.
            det_name, default_rec_name = "PP-OCRv5_server_det", "PP-OCRv5_server_rec"
            if not any((det_path, rec_path, rec_name)):
                det_name, default_rec_name = PaddleOCR._get_ocr_model_names(None, lang, None)
                if not det_name or not default_rec_name:
                    raise TextOnlyError("OFFLINE_MODELS_MISSING")
                kwargs["text_detection_model_name"] = det_name
                kwargs["text_recognition_model_name"] = default_rec_name
            kwargs["text_detection_model_dir"] = offline_model_directory(root, det_name, det_path)
            kwargs["text_recognition_model_dir"] = offline_model_directory(root, rec_name or default_rec_name, rec_path)
        return PaddleOCR(**kwargs)


def has_separated_text_zones(page: dict[str, object]) -> bool:
    width = float(page.get("Width") or 0)
    if width <= 0:
        return False
    left = right = 0
    for block in page.get("TextBlocks", ()):
        polygon = block.get("Polygon") or ()
        if not polygon:
            continue
        xs = [float(point["X"]) for point in polygon]
        left += max(xs) < width * 0.46
        right += min(xs) > width * 0.52
    return left >= 4 and right >= 4


def select_paddle_variant(variants: list[tuple[str, Path, dict[str, object]]]) -> tuple[str, Path, dict[str, object]]:
    best = max(variants, key=lambda item: (item[2]["metrics"]["score"], item[2]["metrics"]["block_count"]))
    original = next((item for item in variants if item[0] == "original"), None)
    if original is None or best[0] == "original":
        return best
    original_result = original[2]
    if (original_result.get("orientation_angle") == 0
            and has_separated_text_zones(original_result["page"])
            and not reorder_numeric_table(original_result["page"])[1]
            and original_result["metrics"]["score"] >= 0.65
            and original_result["metrics"]["block_count"] >= 3
            and best[2]["metrics"]["score"] - original_result["metrics"]["score"] < 0.015):
        return original
    return best


def should_retry_portrait_rotation(width: int, height: int, original_only: bool,
                                   original_result: dict[str, object]) -> bool:
    return (not original_only and width <= height
            and original_result.get("orientation_angle") in (90, 270))


def text_result_coordinate_variant(selected_label: str, orientation_angle: int | None,
                                   numeric_table_detected: bool) -> str:
    if numeric_table_detected and (selected_label == "rot90" and orientation_angle == 270
                                   or selected_label == "rot270" and orientation_angle == 90):
        return "original"
    return selected_label


def should_use_upright_table_fallback(selected: dict[str, object], candidate: dict[str, object]) -> bool:
    return (selected.get("orientation_angle") == 180
            and not reorder_four_column_numeric_table(selected["page"])[1]
            and candidate["metrics"]["score"] >= selected["metrics"]["score"] - 0.08
            and candidate["metrics"]["block_count"] >= selected["metrics"]["block_count"]
            and reorder_four_column_numeric_table(candidate["page"])[1])


def run_paddle_extract(
    input_path: Path,
    image_paths: list[Path],
    artifacts_dir: Path,
    lang: str,
    profile: str,
    warnings: list[dict[str, str]],
    detection_model_dir: str | None,
    recognition_model_dir: str | None,
    recognition_model_name: str | None,
    text_only: bool = False,
    offline: bool = False,
    disable_text_document_unwarping: bool = False,
    force_text_original_variant: bool = False,
    tile_text_page: bool = False,
) -> dict[str, object]:
    if disable_text_document_unwarping and not (text_only and offline):
        raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
    if force_text_original_variant and not (text_only and offline and disable_text_document_unwarping):
        raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
    if tile_text_page and not (text_only and offline and disable_text_document_unwarping
                               and force_text_original_variant):
        raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
    ocr = None if tile_text_page else create_paddle_ocr(
        profile, lang, detection_model_dir, recognition_model_dir, recognition_model_name,
        **({"offline": True} if offline else {}),
        **({"disable_document_unwarping": True} if disable_text_document_unwarping else {}),
    )
    source_content_hash = file_sha256(input_path)
    pages: list[dict[str, object]] = []
    artifacts: list[str] = []
    candidates: list[dict[str, object]] = []
    seen_candidates: set[tuple[str, int, str, str]] = set()

    for page_number, image_path in enumerate(image_paths, start=1):
        page_candidate_start = len(candidates)
        with Image.open(image_path) as original_image:
            original_width, original_height = original_image.size

        variant_dir = artifacts_dir / "paddle" / "variants" / f"page-{page_number:04d}"
        variants = prepare_paddle_variants(
            image_path, variant_dir,
            **({"source_errors": True} if offline else {}),
            **({"original_only": True} if force_text_original_variant else {}),
        )
        variant_results: list[tuple[str, Path, dict[str, object]]] = []

        for label, variant_path in variants:
            if tile_text_page:
                result = build_tiled_paddle_page_result(
                    variant_path, artifacts_dir / "paddle" / "tiles" / f"page-{page_number:04d}",
                    page_number, profile, lang, detection_model_dir, recognition_model_dir,
                    recognition_model_name,
                )
            else:
                result = build_paddle_page_result(
                    ocr=ocr, image_path=variant_path, page_number=page_number,
                    profile=profile, detection_model_dir=detection_model_dir,
                    recognition_model_dir=recognition_model_dir,
                    recognition_model_name=recognition_model_name,
                )
            variant_results.append((label, variant_path, result))

        if should_retry_portrait_rotation(original_width, original_height,
                                          force_text_original_variant, variant_results[0][2]):
            for label, variant_path in prepare_paddle_variants(
                    image_path, variant_dir, source_errors=offline,
                    include_portrait_rotations=True)[1:]:
                result = build_paddle_page_result(
                    ocr=ocr, image_path=variant_path, page_number=page_number,
                    profile=profile, detection_model_dir=detection_model_dir,
                    recognition_model_dir=recognition_model_dir,
                    recognition_model_name=recognition_model_name,
                )
                variant_results.append((label, variant_path, result))

        selected_label, selected_path, selected = select_paddle_variant(variant_results)
        orientation_fallback = False
        orientation_retry_elapsed = 0.0
        if (text_only and not tile_text_page and selected_label == "original"
                and selected.get("orientation_angle") == 180
                and selected["metrics"]["block_count"] >= 20):
            candidate = build_paddle_page_result(
                ocr=ocr, image_path=selected_path, page_number=page_number,
                profile=profile, detection_model_dir=detection_model_dir,
                recognition_model_dir=recognition_model_dir, recognition_model_name=recognition_model_name,
                use_doc_orientation_classify=False,
            )
            orientation_retry_elapsed = candidate["elapsed"]
            if should_use_upright_table_fallback(selected, candidate):
                selected = candidate
                orientation_fallback = True
        page = selected["page"]
        image_metrics = assess_image_quality(selected_path)
        quality_path = artifacts_dir / "paddle" / "quality" / f"page-{page_number:04d}.json"
        write_json(
            quality_path,
            {
                "PageNumber": page_number,
                "ImagePath": str(selected_path),
                "Metrics": image_metrics,
            },
        )
        artifacts.append(str(quality_path))
        warnings.extend(build_quality_warnings(page_number, image_metrics))

        numeric_table_detected = (reorder_numeric_table({**page, "Width": original_width,
                                                         "Height": original_height})[1]
                                  if text_only and selected_label != "original" else False)
        coordinate_variant = (text_result_coordinate_variant(selected_label, selected.get("orientation_angle"),
                                                              numeric_table_detected)
                              if text_only else selected_label)
        raw_path = artifacts_dir / "paddle" / f"page-{page_number:04d}.json"
        write_json(
            raw_path,
            {
                "PageNumber": page_number,
                "LatencySec": round(variant_results[0][2]["elapsed"] + orientation_retry_elapsed, 3)
                if orientation_retry_elapsed else selected["elapsed"],
                "Profile": profile,
                "DetectionModelDir": selected["detection_model_dir"],
                "RecognitionModelDir": selected["recognition_model_dir"],
                "RecognitionModelName": selected["recognition_model_name"],
                "ImagePath": str(selected_path),
                "SourceContentHash": source_content_hash,
                "SelectedVariant": selected_label,
                "OriginalOrientationAngle": variant_results[0][2].get("orientation_angle"),
                "SelectedOrientationAngle": selected.get("orientation_angle"),
                "OrientationFallback": orientation_fallback,
                "OrientationFallbackEvaluated": bool(orientation_retry_elapsed),
                "CoordinateFrame": {
                    "Name": "original-pixels" if coordinate_variant == "original" else "selected-variant-pixels",
                    "Width": original_width if coordinate_variant == "original" else page["Width"],
                    "Height": original_height if coordinate_variant == "original" else page["Height"],
                    "OriginalWidth": original_width,
                    "OriginalHeight": original_height,
                },
                "VariantScores": [
                    {
                        "Variant": label,
                        "ImagePath": str(path),
                        "Metrics": result["metrics"],
                    }
                    for label, path, result in variant_results
                ],
                "Entries": selected["raw_entries"],
                **({"TileLayout": selected["tiles"]} if tile_text_page else {}),
                "ImageQuality": image_metrics,
            },
        )
        artifacts.append(str(raw_path))
        artifacts.extend(
            str(path)
            for label, path, _ in variant_results
            if label != "original" or path != image_path
        )
        if tile_text_page:
            artifacts.extend(tile["ImagePath"] for tile in selected["tiles"])
            artifacts.extend(tile["ResultPath"] for tile in selected["tiles"])

        if selected["low_confidence_count"]:
            warnings.append(
                {
                    "Code": "PADDLE_LOW_CONFIDENCE",
                    "Message": f"Page {page_number}: {selected['low_confidence_count']} blocks below 0.75 confidence.",
                }
            )

        if selected_label != "original":
            warnings.append(
                {
                    "Code": "PADDLE_ROTATION_SELECTED",
                    "Message": f"Page {page_number}: selected variant {selected_label} with quality score {selected['metrics']['score']:.3f}.",
                }
            )

        remapped_page = remap_page_to_original_frame(
            page,
            selected_variant_label=coordinate_variant,
            original_width=original_width,
            original_height=original_height,
        )
        if text_only:
            ordered_blocks, table_detected = reorder_numeric_table(remapped_page)
            if table_detected:
                remapped_page["TextBlocks"] = ordered_blocks
            else:
                ordered_blocks, table_detected = reorder_four_column_numeric_table(remapped_page)
                if table_detected:
                    remapped_page["TextBlocks"] = ordered_blocks
                else:
                    ordered_blocks, changed = reorder_two_columns(remapped_page)
                    if changed:
                        remapped_page["TextBlocks"] = ordered_blocks
            pages.append(remapped_page)
            continue
        pages.append(remapped_page)

        for zone in SECOND_PASS_ZONES:
            max_page_number = int(zone.get("max_page_number") or 0)
            if max_page_number > 0 and page_number > max_page_number:
                continue

            resolved_bounds, zone_bounds_meta = resolve_zone_bounds(page, zone)
            base_zone_blocks = relative_zone_blocks(page, resolved_bounds)
            base_zone_metrics = quality_from_blocks(base_zone_blocks)
            field_key = str(zone.get("field_key") or f"{zone['name']}_zone")
            base_snippet = (
                build_route_candidate_snippet(base_zone_blocks)
                if field_key in {"ship_from", "ship_to"}
                else ""
            )
            base_has_route_signal = field_key in {"ship_from", "ship_to"} and zone_candidate_has_route_signal(base_snippet)
            if (
                field_key in {"ship_from", "ship_to"}
                and base_has_route_signal
                and str(zone_bounds_meta.get("mode", "")).startswith("dynamic")
                and base_zone_metrics["score"] >= 0.70
            ):
                candidates.append(
                    {
                        "FieldKey": field_key,
                        "RawValue": base_snippet,
                        "NormalizedValue": base_snippet,
                        "Confidence": base_zone_metrics["avg_confidence"],
                        "PageNumber": page_number,
                        "Snippet": base_snippet,
                        "SourceType": "zone-baseline-focus",
                        "RequiresReview": True,
                    }
                )
            if str(zone.get("name", "")).lower() in {"driver", "vehicle"}:
                base_field_specific_candidates = build_field_specific_crop_candidates(
                    zone_name=str(zone["name"]),
                    blocks=base_zone_blocks,
                    page_number=page_number,
                    page_width=page["Width"],
                    page_height=page["Height"],
                    source_type="zone-baseline-focus",
                )
                for field_candidate in base_field_specific_candidates:
                    base_field_key = str(field_candidate.get("FieldKey", "")).strip().lower()
                    base_normalized = str(field_candidate.get("NormalizedValue") or field_candidate.get("RawValue") or "").strip().upper()
                    base_source_type = str(field_candidate.get("SourceType", "")).strip().lower()
                    dedupe_key = (base_field_key, page_number, base_normalized, base_source_type)
                    if not base_field_key or not base_normalized or dedupe_key in seen_candidates:
                        continue
                    seen_candidates.add(dedupe_key)
                    candidates.append(field_candidate)
            retry_strategy = str(zone.get("retry_strategy") or "")
            retry_zone = (
                cmr_number_zone_requires_retry(base_zone_blocks)
                if retry_strategy == "cmr-number-ambiguity"
                else should_retry_zone(image_metrics, base_zone_metrics, zone)
            )
            if not retry_zone:
                continue

            crop_box = crop_box_from_bounds(page["Width"], page["Height"], resolved_bounds)
            if crop_box is None:
                continue

            zone_dir = artifacts_dir / "paddle" / "zones"
            crop_path, crop_scale = build_zone_crop_image(selected_path, crop_box, zone["name"], page_number, zone_dir)
            crop_result = build_paddle_page_result(
                ocr=ocr,
                image_path=crop_path,
                page_number=page_number,
                profile=profile,
                detection_model_dir=detection_model_dir,
                recognition_model_dir=recognition_model_dir,
                recognition_model_name=recognition_model_name,
            )
            remapped_blocks = remap_crop_blocks_to_page(
                crop_result["page"],
                offset_x=crop_box[0],
                offset_y=crop_box[1],
                full_width=page["Width"],
                full_height=page["Height"],
                zone_name=zone["name"],
                scale=crop_scale,
            )
            crop_metrics = quality_from_blocks(remapped_blocks)
            field_specific_candidates = build_field_specific_crop_candidates(
                zone_name=str(zone["name"]),
                blocks=remapped_blocks,
                page_number=page_number,
                page_width=page["Width"],
                page_height=page["Height"],
            )
            for field_candidate in field_specific_candidates:
                field_key = str(field_candidate.get("FieldKey", "")).strip().lower()
                normalized = str(field_candidate.get("NormalizedValue") or field_candidate.get("RawValue") or "").strip().upper()
                source_type = str(field_candidate.get("SourceType", "")).strip().lower()
                dedupe_key = (field_key, page_number, normalized, source_type)
                if not field_key or not normalized or dedupe_key in seen_candidates:
                    continue
                seen_candidates.add(dedupe_key)
                candidates.append(field_candidate)

            zone_artifact_path = zone_dir / f"page-{page_number:04d}-{zone['name']}.json"
            write_json(
                zone_artifact_path,
                {
                    "PageNumber": page_number,
                    "Zone": zone["name"],
                    "Bounds": resolved_bounds,
                    "BoundsMeta": zone_bounds_meta,
                    "CropBox": {
                        "Left": crop_box[0],
                        "Top": crop_box[1],
                        "Right": crop_box[2],
                        "Bottom": crop_box[3],
                    },
                    "CoordinateFrame": {
                        "Name": "selected-variant-pixels",
                        "Variant": selected_label,
                        "Width": page["Width"],
                        "Height": page["Height"],
                    },
                    "OriginalSourceRegion": remap_normalized_bbox_to_original_frame(
                        normalized_bbox_from_crop_box(crop_box, page["Width"], page["Height"]),
                        selected_label,
                        original_width,
                        original_height,
                    ),
                    "Scale": round(crop_scale, 3),
                    "ElapsedSeconds": crop_result["elapsed"],
                    "BaseMetrics": base_zone_metrics,
                    "CropMetrics": crop_metrics,
                    "Entries": crop_result["raw_entries"],
                },
            )
            artifacts.extend([str(crop_path), str(zone_artifact_path)])

            if field_key == "cmr_number":
                if field_specific_candidates:
                    warnings.append(
                        {
                            "Code": "PADDLE_CMR_NUMBER_CANDIDATE",
                            "Message": f"Page {page_number}: captured focused CMR header number candidate.",
                        }
                    )
                continue

            snippet = (
                build_route_candidate_snippet(remapped_blocks)
                if field_key in {"ship_from", "ship_to"}
                else " | ".join(
                    str(block.get("Text", "")).strip()
                    for block in remapped_blocks[:6]
                    if str(block.get("Text", "")).strip()
                )
            )
            base_score = base_zone_metrics["score"]
            crop_score = crop_metrics["score"]
            crop_has_more_text = crop_metrics["block_count"] >= base_zone_metrics["block_count"] + 1.0
            crop_has_route_signal = field_key in {"ship_from", "ship_to"} and zone_candidate_has_route_signal(snippet)
            route_dynamic_candidate = (
                field_key in {"ship_from", "ship_to"}
                and crop_has_route_signal
                and str(zone_bounds_meta.get("mode", "")).startswith("dynamic")
            )
            crop_is_better = (
                crop_score > base_score + 0.035
                or (crop_has_more_text and crop_score >= base_score - 0.02)
                or (crop_has_route_signal and crop_score >= base_score - 0.08)
                or route_dynamic_candidate
            )
            if not crop_is_better or not snippet:
                continue

            warnings.append(
                {
                    "Code": "PADDLE_ZONE_CANDIDATE",
                    "Message": (
                        f"Page {page_number}: captured second-pass OCR candidate for {zone['name']} zone "
                        f"({base_score:.3f} -> {crop_score:.3f}) without replacing baseline blocks."
                    ),
                }
            )
            candidates.append(
                {
                    "FieldKey": field_key,
                    "RawValue": snippet,
                    "NormalizedValue": snippet,
                    "Confidence": crop_metrics["avg_confidence"],
                    "PageNumber": page_number,
                    "Snippet": snippet,
                    "SourceType": "zone-second-pass",
                    "RequiresReview": True,
                }
            )

        handwritten_weight_candidates, handwritten_weight_artifacts = build_handwritten_weight_crop_candidates(
            selected_path=selected_path,
            selected_variant_label=selected_label,
            page=page,
            page_number=page_number,
            artifacts_dir=artifacts_dir,
        )
        artifacts.extend(handwritten_weight_artifacts)
        for field_candidate in handwritten_weight_candidates:
            field_key = str(field_candidate.get("FieldKey", "")).strip().lower()
            normalized = str(field_candidate.get("NormalizedValue") or field_candidate.get("RawValue") or "").strip().upper()
            source_type = str(field_candidate.get("SourceType", "")).strip().lower()
            dedupe_key = (field_key, page_number, normalized, source_type)
            if not field_key or not normalized or dedupe_key in seen_candidates:
                continue
            seen_candidates.add(dedupe_key)
            warnings.append(
                {
                    "Code": "PADDLE_HANDWRITTEN_WEIGHT_CANDIDATE",
                    "Message": f"Page {page_number}: captured handwritten gross-weight candidate from focused TN crop.",
                }
            )
            candidates.append(field_candidate)

        candidates[page_candidate_start:] = [
            remap_candidate_bbox_to_original_frame(
                candidate,
                selected_variant_label=selected_label,
                original_width=original_width,
                original_height=original_height,
            )
            for candidate in candidates[page_candidate_start:]
        ]
    return {
        "DocumentId": input_path.stem,
        "EngineName": f"PaddleOCR/{profile}",
        "EngineVersion": distribution_version("paddleocr"),
        "Pages": pages,
        "Warnings": warnings,
        "Candidates": candidates,
        "Artifacts": artifacts + [str(path) for path in image_paths],
    }


def parse_tesseract_tsv(tsv_path: Path, page_number: int, width: int, height: int) -> list[dict[str, object]]:
    blocks: list[dict[str, object]] = []
    with tsv_path.open("r", encoding="utf-8", errors="replace", newline="") as stream:
        reader = csv.DictReader(stream, delimiter="\t")
        for row_index, row in enumerate(reader, start=1):
            text = (row.get("text") or "").strip()
            if not text:
                continue

            try:
                confidence = max(float(row.get("conf") or "0"), 0.0) / 100.0
            except ValueError:
                confidence = 0.0

            try:
                left = float(row.get("left") or "0")
                top = float(row.get("top") or "0")
                cell_width = float(row.get("width") or "0")
                cell_height = float(row.get("height") or "0")
            except ValueError:
                left = 0.0
                top = 0.0
                cell_width = float(width)
                cell_height = float(height)

            polygon = [
                {"X": left, "Y": top},
                {"X": left + cell_width, "Y": top},
                {"X": left + cell_width, "Y": top + cell_height},
                {"X": left, "Y": top + cell_height},
            ]
            blocks.append(
                {
                    "Id": f"tesseract-{page_number:04d}-{row_index:04d}",
                    "Text": text,
                    "Confidence": confidence,
                    "Polygon": polygon,
                }
            )

    return blocks


def run_tesseract_extract(
    input_path: Path,
    image_paths: list[Path],
    artifacts_dir: Path,
    tesseract_path: str,
    tessdata_dir: str | None,
    languages: str,
    warnings: list[dict[str, str]],
    text_only: bool = False,
) -> dict[str, object]:
    pages: list[dict[str, object]] = []
    artifacts: list[str] = []
    resolved_tessdata_dir = resolve_tessdata_dir(tessdata_dir)

    for page_number, image_path in enumerate(image_paths, start=1):
        with Image.open(image_path) as image:
            width, height = image.size

        output_base = artifacts_dir / "tesseract" / f"page-{page_number:04d}"
        output_base.parent.mkdir(parents=True, exist_ok=True)
        command = [tesseract_path, str(image_path), str(output_base), "-l", languages]
        if resolved_tessdata_dir:
            command.extend(["--tessdata-dir", resolved_tessdata_dir])
        command.append("tsv")

        completed = subprocess.run(
            command,
            check=False,
            capture_output=True,
            text=True,
            encoding="utf-8",
            errors="replace",
            **hidden_subprocess_kwargs(),
        )
        if completed.returncode != 0:
            raise RuntimeError(completed.stderr.strip() or "Tesseract returned a non-zero exit code.")

        tsv_path = output_base.with_suffix(".tsv")
        if not tsv_path.exists():
            raise RuntimeError(
                "Tesseract completed without TSV output. "
                f"Resolved tessdata='{resolved_tessdata_dir}', has_tsv_config={has_tesseract_tsv_config(resolved_tessdata_dir)}."
            )
        blocks = parse_tesseract_tsv(tsv_path, page_number, width, height)
        text_path = output_base.with_suffix(".txt")
        text_path.write_text("\n".join(block["Text"] for block in blocks), encoding="utf-8")
        artifacts.extend([str(tsv_path), str(text_path)])

        low_confidence_count = sum(1 for block in blocks if float(block["Confidence"]) < 0.75)
        if low_confidence_count:
            warnings.append(
                {
                    "Code": "TESSERACT_LOW_CONFIDENCE",
                    "Message": f"Page {page_number}: {low_confidence_count} blocks below 0.75 confidence.",
                }
            )

        pages.append(
            {
                "PageNumber": page_number,
                "Width": width,
                "Height": height,
                "TextBlocks": blocks,
                "TableBlocks": [],
            }
        )

    return {
        "DocumentId": input_path.stem,
        "EngineName": f"Tesseract/{languages}",
        "EngineVersion": "5.x",
        "Pages": pages,
        "Warnings": warnings,
        "Candidates": [],
        "Artifacts": artifacts + [str(path) for path in image_paths],
    }


def envelope_for_text_layer(
    input_path: Path,
    pages: list[dict[str, object]],
    artifacts: list[str],
    warnings: list[dict[str, str]],
) -> dict[str, object]:
    return {
        "DocumentId": input_path.stem,
        "EngineName": "PdfTextLayer",
        "EngineVersion": distribution_version("pypdf"),
        "Pages": pages,
        "Warnings": warnings,
        "Candidates": [],
        "Artifacts": artifacts,
    }


def run_paddle_command(subcommand: str, input_path: str, save_path: str, lang: str) -> int:
    save_dir = Path(save_path)
    save_dir.mkdir(parents=True, exist_ok=True)

    command = [
        sys.executable,
        "-m",
        "paddleocr",
        subcommand,
        "--input",
        input_path,
        "--save_path",
        str(save_dir),
        "--device",
        "cpu",
    ]

    if subcommand == "ocr":
        command.extend(["--lang", lang])

    completed = subprocess.run(
        command,
        check=False,
        capture_output=True,
        text=True,
        encoding="utf-8",
        errors="replace",
        **hidden_subprocess_kwargs(),
    )
    if completed.returncode != 0:
        if completed.stderr:
            print(completed.stderr, file=sys.stderr, end="")
        return completed.returncode

    return print_json(
        {
            "Status": "ok",
            "Subcommand": subcommand,
            "InputPath": input_path,
            "SavePath": str(save_dir),
            "Device": "cpu",
        }
    )


def ensure_image_inputs(input_path: Path, artifacts_dir: Path) -> list[Path]:
    if input_path.suffix.lower() == ".pdf":
        return render_pdf_pages(input_path, artifacts_dir / "rendered")

    if input_path.suffix.lower() in IMAGE_EXTENSIONS:
        return [input_path]

    raise ValueError(f"Unsupported input type: {input_path.suffix}")


def file_sha256(path: Path) -> str:
    digest = hashlib.sha256()
    with path.open("rb") as stream:
        for chunk in iter(lambda: stream.read(1024 * 1024), b""):
            digest.update(chunk)
    return digest.hexdigest()


def attach_source_identity(payload: dict[str, object], input_path: Path) -> dict[str, object]:
    source_content_hash = file_sha256(input_path)
    payload["SourceContentHash"] = source_content_hash
    pages = payload.get("Pages")
    if isinstance(pages, list):
        for page in pages:
            if isinstance(page, dict):
                page["SourceContentHash"] = source_content_hash
    return payload


def extract_document(
    input_path: Path,
    output_path: Path,
    artifacts_dir: Path,
    engine: str,
    paddle_profile: str,
    paddle_lang: str,
    paddle_det_model_dir: str | None,
    paddle_rec_model_dir: str | None,
    paddle_rec_model_name: str | None,
    tesseract_path: str | None,
    tessdata_dir: str | None,
    tesseract_languages: str,
    text_only: bool = False,
    offline: bool = False,
    max_pages: int | None = None,
    max_pixels: int | None = None,
    disable_text_document_unwarping: bool = False,
    force_text_original_variant: bool = False,
    tile_text_page: bool = False,
) -> int:
    if disable_text_document_unwarping and not (text_only and offline and engine == "paddle"):
        raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
    if force_text_original_variant and not (text_only and offline and engine == "paddle"
                                            and disable_text_document_unwarping):
        raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
    if tile_text_page and not (text_only and offline and engine == "paddle"
                               and disable_text_document_unwarping and force_text_original_variant):
        raise ValueError("OFFLINE_TEXT_PADDLE_REQUIRED")
    bounded = offline or max_pages is not None or max_pixels is not None
    if bounded and not text_only:
        raise ValueError("TEXT_ONLY_REQUIRED")
    page_limit = text_only_limit(max_pages, TEXT_ONLY_MAX_PAGES, TEXT_ONLY_OFFLINE_MAX_PAGES if offline else TEXT_ONLY_MAX_PAGES)
    pixel_limit = text_only_limit(max_pixels, TEXT_ONLY_MAX_PIXELS, TEXT_ONLY_MAX_PIXELS)
    try:
        with offline_paddle_runtime(offline):
            if bounded:
                try:
                    preflight_text_only_source(input_path, page_limit, pixel_limit)
                except TextOnlyError:
                    raise
                except Exception:
                    raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID") from None
            return _extract_document(
                input_path, output_path, artifacts_dir, engine, paddle_profile, paddle_lang,
                paddle_det_model_dir, paddle_rec_model_dir, paddle_rec_model_name,
                tesseract_path, tessdata_dir, tesseract_languages, text_only, offline,
                disable_text_document_unwarping, force_text_original_variant, tile_text_page,
            )
    except TextOnlyError:
        raise
    except (PdfReadError, pdfium.PdfiumError, Image.UnidentifiedImageError):
        if bounded:
            raise TextOnlyError("TEXT_ONLY_SOURCE_INVALID") from None
        raise
    except Exception:
        if bounded:
            raise TextOnlyError("TEXT_ONLY_RUNTIME_FAILED") from None
        raise


def _extract_document(
    input_path: Path,
    output_path: Path,
    artifacts_dir: Path,
    engine: str,
    paddle_profile: str,
    paddle_lang: str,
    paddle_det_model_dir: str | None,
    paddle_rec_model_dir: str | None,
    paddle_rec_model_name: str | None,
    tesseract_path: str | None,
    tessdata_dir: str | None,
    tesseract_languages: str,
    text_only: bool = False,
    offline: bool = False,
    disable_text_document_unwarping: bool = False,
    force_text_original_variant: bool = False,
    tile_text_page: bool = False,
) -> int:
    warnings: list[dict[str, str]] = []
    artifacts_dir.mkdir(parents=True, exist_ok=True)

    if input_path.suffix.lower() == ".pdf":
        text_pages, text_artifacts, total_chars = extract_pdf_text_layer(input_path, artifacts_dir, text_only=text_only)
        text_layer_ok, text_layer_reason = assess_text_layer_quality(text_pages)
        text_layer_fast_path_ok, text_layer_fast_path_reason = should_use_text_layer_fast_path(text_pages)
        incomplete_text_pages: list[int] = []
        if text_only:
            validate_text_only_page_count(len(text_pages), "PDF")
            incomplete_text_pages = [
                page["PageNumber"] for page in text_pages if not assess_text_layer_quality([page])[0]
            ]
            if incomplete_text_pages:
                text_layer_ok = False
        if total_chars >= 80 and text_layer_ok and text_layer_fast_path_ok:
            if text_only:
                text_artifacts = text_artifacts + [
                    str(path) for path in render_pdf_pages(input_path, artifacts_dir / "rendered")
                ]
            warnings.append(
                {
                    "Code": "TEXT_LAYER_FAST_PATH",
                    "Message": "Used embedded PDF text layer before OCR.",
                }
            )
            payload = attach_source_identity(
                envelope_for_text_layer(input_path, text_pages, text_artifacts, warnings),
                input_path,
            )
            write_json(output_path, payload)
            return print_json(payload)

        if engine == "text-layer":
            if text_only and incomplete_text_pages:
                raise RuntimeError(
                    f"PDF pages {incomplete_text_pages} lack usable embedded text; text-layer-only extraction cannot continue."
                )
            raise RuntimeError("The PDF text layer is empty, so text-layer-only extraction cannot continue.")

        if text_only and incomplete_text_pages and total_chars >= 80:
            warnings.append({
                "Code": "TEXT_LAYER_INCOMPLETE",
                "Message": f"PDF pages {incomplete_text_pages} lack usable embedded text; using OCR for every page.",
            })
        else:
            warnings.append(
                {
                    "Code": text_layer_fast_path_reason or text_layer_reason or "TEXT_LAYER_EMPTY",
                    "Message": "The PDF text layer is empty, suspicious, or unsuitable for structured layout parsing, falling back to OCR.",
                }
            )

    image_paths = ensure_image_inputs(input_path, artifacts_dir)
    if text_only:
        validate_text_only_page_count(len(image_paths), "Document")
        for page_number, image_path in enumerate(image_paths, start=1):
            with Image.open(image_path) as image:
                validate_text_only_page_pixels(image.width, image.height, f"Image page {page_number}")

    paddle_payload: dict[str, object] | None = None
    paddle_error: Exception | None = None

    try:
        if engine in {"auto", "paddle"}:
            paddle_payload = run_paddle_extract(
                input_path=input_path,
                image_paths=image_paths,
                artifacts_dir=artifacts_dir,
                lang=paddle_lang,
                profile=paddle_profile,
                warnings=list(warnings),
                detection_model_dir=paddle_det_model_dir,
                recognition_model_dir=paddle_rec_model_dir,
                recognition_model_name=paddle_rec_model_name,
                text_only=text_only,
                **({"offline": True} if offline else {}),
                **({"disable_text_document_unwarping": True} if disable_text_document_unwarping else {}),
                **({"force_text_original_variant": True} if force_text_original_variant else {}),
                **({"tile_text_page": True} if tile_text_page else {}),
            )
            paddle_payload = attach_source_identity(paddle_payload, input_path)
            if engine == "paddle":
                write_json(output_path, paddle_payload)
                return print_json(paddle_payload)
    except TextOnlyError:
        raise
    except Exception as error:  # noqa: BLE001
        paddle_error = error
        if engine != "auto":
            raise
        warnings.append(
            {
                "Code": "PADDLE_FALLBACK",
                "Message": "Local recognition failed; using the fallback engine." if offline else f"PaddleOCR failed, switching to Tesseract: {error}",
            }
        )

    if engine == "auto" and paddle_payload is not None and not should_try_tesseract_after_paddle(paddle_payload):
        paddle_payload = attach_quality_warning(
            paddle_payload,
            "AUTO_ENGINE_SHORTLIST",
            f"Paddle quality score {payload_quality(paddle_payload)['score']:.3f} is sufficient; Tesseract comparison skipped.",
        )
        write_json(output_path, paddle_payload)
        return print_json(paddle_payload)

    if not tesseract_path:
        if paddle_payload is not None:
            write_json(output_path, paddle_payload)
            return print_json(paddle_payload)
        raise RuntimeError("Tesseract fallback was requested, but no executable path is available.")

    tesseract_payload = run_tesseract_extract(
        input_path=input_path,
        image_paths=image_paths,
        artifacts_dir=artifacts_dir,
        tesseract_path=tesseract_path,
        tessdata_dir=tessdata_dir,
        languages=tesseract_languages,
        warnings=list(warnings),
        text_only=text_only,
    )
    tesseract_payload = attach_source_identity(tesseract_payload, input_path)

    if engine == "auto" and paddle_payload is not None:
        chosen_payload = choose_best_payload(paddle_payload, tesseract_payload, "PaddleOCR", "Tesseract")
        write_json(output_path, chosen_payload)
        return print_json(chosen_payload)

    if paddle_error is not None:
        tesseract_payload = attach_quality_warning(
            tesseract_payload,
            "AUTO_ENGINE_FALLBACK",
            "Local recognition failed; the fallback engine was used." if offline else f"PaddleOCR failed and Tesseract was used instead: {paddle_error}",
        )

    write_json(output_path, tesseract_payload)
    return print_json(tesseract_payload)


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="FormPotok local OCR sidecar wrapper")
    subparsers = parser.add_subparsers(dest="command", required=True)

    shared_runtime = argparse.ArgumentParser(add_help=False)
    shared_runtime.add_argument("--tesseract-path", help="Optional explicit path to tesseract.exe.")
    shared_runtime.add_argument("--tessdata-dir", help="Optional path to a tessdata directory containing rus/eng traineddata.")
    shared_runtime.add_argument("--paddle-det-model-dir", help="Optional path to a custom PaddleOCR detection inference model directory.")
    shared_runtime.add_argument("--paddle-rec-model-dir", help="Optional path to a custom PaddleOCR recognition inference model directory.")
    shared_runtime.add_argument("--paddle-rec-model-name", help="Optional PaddleOCR recognition model name matching the custom recognition directory.")

    subparsers.add_parser("healthcheck", parents=[shared_runtime], help="Validate local OCR runtime availability.")

    paddle_health_parser = subparsers.add_parser("paddle-healthcheck", parents=[shared_runtime], help="Validate PaddleOCR dependency packaging.")
    paddle_health_parser.add_argument("--paddle-profile", default="mobile-cpu", choices=["mobile-cpu", "default-cpu"], help="CPU profile for PaddleOCR.")
    paddle_health_parser.add_argument("--paddle-lang", default="ru", help="PaddleOCR language code, defaults to ru.")
    paddle_health_parser.add_argument("--create-pipeline", action="store_true", help="Instantiate PaddleOCR to catch packaged runtime failures.")

    ocr_parser = subparsers.add_parser("ocr", help="Run text OCR through the official PaddleOCR CLI.")
    ocr_parser.add_argument("--input", required=True, help="Path to the input PDF/image.")
    ocr_parser.add_argument("--save-path", required=True, help="Directory for OCR artifacts.")
    ocr_parser.add_argument("--lang", default="ru", help="Recognition language, defaults to ru.")

    structure_parser = subparsers.add_parser("structure", help="Run PP-StructureV3 through the official PaddleOCR CLI.")
    structure_parser.add_argument("--input", required=True, help="Path to the input PDF/image.")
    structure_parser.add_argument("--save-path", required=True, help="Directory for structure artifacts.")
    structure_parser.add_argument("--lang", default="ru", help="Reserved for symmetry with OCR command.")

    tile_parser = subparsers.add_parser("tile-worker", parents=[shared_runtime], help=argparse.SUPPRESS)
    tile_parser.add_argument("--input", required=True)
    tile_parser.add_argument("--output", required=True)
    tile_parser.add_argument("--page-number", required=True, type=int)
    tile_parser.add_argument("--paddle-profile", default="mobile-cpu", choices=["mobile-cpu", "default-cpu"])
    tile_parser.add_argument("--paddle-lang", default="ru")

    extract_parser = subparsers.add_parser("extract", parents=[shared_runtime], help="Create a normalized ExtractionEnvelope JSON.")
    extract_parser.add_argument("--input", required=True, help="Path to the input PDF/image.")
    extract_parser.add_argument("--output", required=True, help="Path to the extraction envelope JSON file.")
    extract_parser.add_argument("--artifacts-dir", help="Optional path for rendered pages and raw engine artifacts.")
    extract_parser.add_argument("--engine", default="auto", choices=["auto", "text-layer", "paddle", "tesseract"], help="Preferred extraction engine.")
    extract_parser.add_argument("--text-only", action="store_true", help="Extract page text without transport-field candidate processing.")
    extract_parser.add_argument("--offline", action="store_true", help="Use only packaged local models in text-only mode.")
    extract_parser.add_argument("--disable-text-document-unwarping", action="store_true", help="Diagnostic offline Text/Paddle comparison; defaults remain unchanged.")
    extract_parser.add_argument("--force-text-original-variant", action="store_true", help="Lab-only offline Text/Paddle tile comparison; requires disabled document unwarping.")
    extract_parser.add_argument("--tile-text-page", action="store_true", help="Lab-only source-bound native-resolution Text/Paddle tiling.")

    def bounded_integer(cap: int):
        def parse(value: str) -> int:
            try:
                return text_only_limit(int(value), cap, cap)
            except ValueError:
                raise argparse.ArgumentTypeError("INVALID_TEXT_ONLY_LIMIT") from None
        return parse

    extract_parser.add_argument("--max-pages", type=bounded_integer(TEXT_ONLY_MAX_PAGES), help="Optional narrower text-only page limit.")
    extract_parser.add_argument("--max-pixels", type=bounded_integer(TEXT_ONLY_MAX_PIXELS), help="Optional narrower text-only per-page pixel limit.")
    extract_parser.add_argument("--paddle-profile", default="mobile-cpu", choices=["mobile-cpu", "default-cpu"], help="CPU profile for PaddleOCR.")
    extract_parser.add_argument("--paddle-lang", default="ru", help="PaddleOCR language code, defaults to ru.")
    extract_parser.add_argument("--tesseract-lang", default="rus+eng", help="Tesseract language profile, defaults to rus+eng.")

    args = parser.parse_args()
    if args.command == "extract" and args.disable_text_document_unwarping and not (args.text_only and args.offline and args.engine == "paddle"):
        parser.error("OFFLINE_TEXT_PADDLE_REQUIRED")
    if args.command == "extract" and args.force_text_original_variant and not (args.text_only and args.offline
                                                                                 and args.engine == "paddle"
                                                                                 and args.disable_text_document_unwarping):
        parser.error("OFFLINE_TEXT_PADDLE_REQUIRED")
    if args.command == "extract" and args.tile_text_page and not (args.text_only and args.offline
                                                                    and args.engine == "paddle"
                                                                    and args.disable_text_document_unwarping
                                                                    and args.force_text_original_variant):
        parser.error("OFFLINE_TEXT_PADDLE_REQUIRED")
    if args.command == "extract" and (args.offline or args.max_pages is not None or args.max_pixels is not None) and not args.text_only:
        parser.error("TEXT_ONLY_REQUIRED")
    return args


def main() -> int:
    args = parse_args()

    if args.command == "healthcheck":
        return healthcheck(args.tesseract_path, args.tessdata_dir, args.paddle_det_model_dir, args.paddle_rec_model_dir, args.paddle_rec_model_name)

    if args.command == "paddle-healthcheck":
        return paddle_healthcheck(
            args.paddle_profile,
            args.paddle_lang,
            args.paddle_det_model_dir,
            args.paddle_rec_model_dir,
            args.paddle_rec_model_name,
            args.create_pipeline,
        )

    if args.command == "ocr":
        return run_paddle_command("ocr", args.input, args.save_path, args.lang)

    if args.command == "structure":
        return run_paddle_command("pp_structurev3", args.input, args.save_path, args.lang)

    if args.command == "tile-worker":
        return tile_worker(args)

    if args.command == "extract":
        input_path = Path(args.input).resolve()
        output_path = Path(args.output).resolve()
        artifacts_dir = Path(args.artifacts_dir).resolve() if args.artifacts_dir else output_path.parent / f"{output_path.stem}-artifacts"
        resolved_tesseract_path = resolve_tesseract_path(args.tesseract_path)
        resolved_tessdata_dir = resolve_tessdata_dir(args.tessdata_dir)
        return extract_document(
            input_path=input_path,
            output_path=output_path,
            artifacts_dir=artifacts_dir,
            engine=args.engine,
            paddle_profile=args.paddle_profile,
            paddle_lang=args.paddle_lang,
            paddle_det_model_dir=args.paddle_det_model_dir,
            paddle_rec_model_dir=args.paddle_rec_model_dir,
            paddle_rec_model_name=args.paddle_rec_model_name,
            tesseract_path=resolved_tesseract_path,
            tessdata_dir=resolved_tessdata_dir,
            tesseract_languages=args.tesseract_lang,
            text_only=args.text_only,
            offline=args.offline,
            max_pages=args.max_pages,
            max_pixels=args.max_pixels,
            disable_text_document_unwarping=args.disable_text_document_unwarping,
            force_text_original_variant=args.force_text_original_variant,
            tile_text_page=args.tile_text_page,
        )

    raise ValueError(f"Unknown command: {args.command}")


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except TextOnlyError as error:
        print(json.dumps({"errorCode": error.code}), file=sys.stderr)
        raise SystemExit(1) from None
