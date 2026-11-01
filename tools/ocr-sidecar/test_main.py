from __future__ import annotations

import ast
import builtins
import io
import importlib.util
import json
import os
import sys
import subprocess
import struct
import tempfile
import unittest
import zlib
from contextlib import contextmanager, redirect_stderr
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import Mock, patch

from PIL import Image
from pypdf import PdfWriter
from pypdf.generic import DecodedStreamObject, DictionaryObject, NameObject


ROOT = Path(__file__).resolve().parent
MODULE_PATH = ROOT / "main.py"
SPEC = importlib.util.spec_from_file_location("ocr_sidecar_main", MODULE_PATH)
assert SPEC is not None and SPEC.loader is not None
MODULE = importlib.util.module_from_spec(SPEC)
sys.modules[SPEC.name] = MODULE
SPEC.loader.exec_module(MODULE)


def block(text: str, confidence: float, x: float, y: float) -> dict[str, object]:
    return {
        "Text": text,
        "Confidence": confidence,
        "Polygon": [
            {"X": x, "Y": y},
            {"X": x + 100.0, "Y": y},
            {"X": x + 100.0, "Y": y + 20.0},
            {"X": x, "Y": y + 20.0},
        ],
    }


def document_options(root: Path, **overrides) -> dict[str, object]:
    options = {
        "input_path": root / "sample-document.pdf",
        "output_path": root / "envelope.json",
        "artifacts_dir": root / "artifacts",
        "engine": "paddle",
        "paddle_profile": "mobile-cpu",
        "paddle_lang": "ru",
        "paddle_det_model_dir": None,
        "paddle_rec_model_dir": None,
        "paddle_rec_model_name": None,
        "tesseract_path": None,
        "tessdata_dir": None,
        "tesseract_languages": "rus+eng",
    }
    options.update(overrides)
    return options


def produce_text_only_fixture() -> dict[str, object]:
    # Relative producer paths keep the contract fixture independent of the checkout.
    previous_directory = Path.cwd()
    with tempfile.TemporaryDirectory() as temp_dir:
        try:
            MODULE.os.chdir(temp_dir)
            options = document_options(Path("."), text_only=True)
            options["input_path"].write_bytes(b"%PDF-1.4\nsample-document\n%%EOF\n")

            def render_pages(pdf_path, render_dir):
                render_dir.mkdir(parents=True)
                paths = []
                for page_number, size in enumerate(((1600, 1200), (1200, 1600)), start=1):
                    path = render_dir / f"page-{page_number:04d}.png"
                    Image.new("RGB", size, "white").save(path)
                    paths.append(path)
                return paths

            def predict(image_path):
                path = Path(image_path)
                first_page = path.stem.startswith("page-0001")
                scores = (
                    [0.94, 0.70] if path.stem.endswith("-rot90") else [0.10, 0.10]
                ) if first_page else [0.96, 0.93]
                texts = (
                    ["Заявление на выдачу справки", "Адрес: Самара, улица Мира, 10"]
                    if first_page else ["Список приложений", "Копия договора № 42"]
                )
                return [{
                    "rec_texts": texts,
                    "rec_scores": scores,
                    "rec_polys": [
                        [[40, 80], [240, 80], [240, 100], [40, 100]],
                        [[40, 120], [340, 120], [340, 140], [40, 140]],
                    ],
                }]

            model = Mock()
            model.predict.side_effect = predict
            with (
                patch.object(MODULE, "extract_pdf_text_layer", return_value=([
                    {"PageNumber": 1, "TextBlocks": []}, {"PageNumber": 2, "TextBlocks": []},
                ], [], 0)),
                patch.object(MODULE, "render_pdf_pages", side_effect=render_pages),
                patch.object(MODULE, "create_paddle_ocr", return_value=model),
                patch.object(MODULE, "distribution_version", return_value="1.0"),
                patch.object(MODULE, "assess_image_quality", return_value={
                    "width": 1200.0, "height": 1600.0,
                    "focus_score": 100.0, "contrast_score": 50.0, "bright_ratio": 0.0,
                }),
                patch.object(MODULE, "print_json", return_value=0),
            ):
                assert MODULE.extract_document(**options) == 0
            return json.loads(options["output_path"].read_text(encoding="utf-8"))
        finally:
            MODULE.os.chdir(previous_directory)


class OcrSidecarTextOnlyTests(unittest.TestCase):
    def test_cli_defaults_to_legacy_and_propagates_text_only(self) -> None:
        for flags, expected in (([], False), (["--text-only"], True)):
            with (
                self.subTest(text_only=expected),
                patch.object(sys, "argv", ["sidecar", "extract", "--input", "source.png", "--output", "envelope.json", *flags]),
                patch.object(MODULE, "resolve_tesseract_path", return_value=None),
                patch.object(MODULE, "resolve_tessdata_dir", return_value=None),
                patch.object(MODULE, "extract_document", return_value=0) as extract,
            ):
                self.assertEqual(expected, MODULE.parse_args().text_only)
                self.assertEqual(0, MODULE.main())
                self.assertEqual(expected, extract.call_args.kwargs["text_only"])

    def test_text_only_skips_transport_callbacks_but_legacy_runs_them(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            input_path = root / "source.png"
            Image.new("RGB", (1200, 1600), "white").save(input_path)
            for mode in (None, False, True):
                with self.subTest(text_only=mode):
                    model = Mock()
                    model.predict.return_value = [{
                        "rec_texts": ["E960CT156"], "rec_scores": [0.9],
                        "rec_polys": [[[40, 80], [240, 80], [240, 100], [40, 100]]],
                    }]
                    with (
                        patch.object(MODULE, "create_paddle_ocr", return_value=model),
                        patch.object(MODULE, "should_retry_zone", return_value=True),
                        patch.object(MODULE, "cmr_number_zone_requires_retry", return_value=True),
                        patch.object(MODULE, "resolve_zone_bounds", wraps=MODULE.resolve_zone_bounds) as zones,
                        patch.object(MODULE, "build_zone_crop_image", wraps=MODULE.build_zone_crop_image) as crops,
                        patch.object(MODULE, "build_field_specific_crop_candidates", wraps=MODULE.build_field_specific_crop_candidates) as fields,
                        patch.object(MODULE, "build_handwritten_weight_crop_candidates", return_value=([
                            {"FieldKey": "transport_weight_tons", "NormalizedValue": "20", "PageNumber": 1, "SourceType": "zone-second-pass-handwritten-weight"}
                        ], [])) as handwriting,
                    ):
                        result = MODULE.run_paddle_extract(
                            input_path, [input_path], root / str(mode), "ru", "mobile-cpu", [],
                            None, None, None, **({} if mode is None else {"text_only": mode}),
                        )
                    for callback in (zones, crops, fields, handwriting):
                        if mode:
                            callback.assert_not_called()
                        else:
                            self.assertGreater(callback.call_count, 0)
                    self.assertEqual(1, len(result["Pages"]))
                    page = result["Pages"][0]
                    self.assertEqual("E960CT156", page["TextBlocks"][0]["Text"])
                    self.assertEqual("original-pixels", page["CoordinateFrame"])
                    self.assertEqual("original", page["SelectedVariant"])
                    self.assertEqual((1200, 1600), (page["Width"], page["Height"]))
                    self.assertIn(str(input_path), result["Artifacts"])
                    if mode:
                        self.assertEqual([], result["Candidates"])
                        self.assertEqual(1, model.predict.call_count)
                        self.assertFalse(any("zones" in Path(path).parts for path in result["Artifacts"]))
                    else:
                        self.assertTrue(result["Candidates"])
                        self.assertGreater(model.predict.call_count, 1)

    def test_document_propagates_mode_through_auto_comparison_and_fallback(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (1200, 1600), "white").save(source)
            for text_only in (False, True):
                for fail_paddle in (False, True):
                    with self.subTest(text_only=text_only, fail_paddle=fail_paddle):
                        def payload(name):
                            return {"EngineName": name, "Pages": [{"PageNumber": 1, "TextBlocks": [block("Sample text", 0.9, 0, 0)]}], "Candidates": [], "Artifacts": [], "Warnings": []}

                        with (
                            patch.object(MODULE, "run_paddle_extract", return_value=payload("PaddleOCR"), side_effect=RuntimeError("unavailable") if fail_paddle else None) as paddle,
                            patch.object(MODULE, "should_try_tesseract_after_paddle", return_value=True),
                            patch.object(MODULE, "run_tesseract_extract", return_value=payload("Tesseract")) as tesseract,
                            patch.object(MODULE, "print_json", return_value=0),
                        ):
                            self.assertEqual(0, MODULE.extract_document(**document_options(
                                root, input_path=source, engine="auto", tesseract_path="tesseract.exe", text_only=text_only,
                            )))
                        self.assertEqual(text_only, paddle.call_args.kwargs["text_only"])
                        self.assertEqual(text_only, tesseract.call_args.kwargs["text_only"])
                        result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
                        self.assertEqual([], result["Candidates"])
                        self.assertEqual(MODULE.file_sha256(source), result["SourceContentHash"])

    def test_valid_text_layer_keeps_geometry_and_adds_only_text_mode_previews(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "sample-document.pdf"
            source.write_bytes(b"sample document")
            pages = [{"PageNumber": 1, "Width": 600, "Height": 800, "TextBlocks": [
                block("Заявление о выдаче справки. Адрес заявителя: Самара, улица Мира, дом 10. Список приложений: копия договора.", 1.0, 10, 20)
            ], "TableBlocks": []}]

            def render_pages(pdf_path, render_dir):
                render_dir.mkdir(parents=True)
                preview = render_dir / "page-0001.png"
                Image.new("RGB", (1200, 1600), "white").save(preview)
                return [preview]

            results = []
            for text_only in (False, True):
                with (
                    patch.object(MODULE, "extract_pdf_text_layer", return_value=(pages, ["text-layer/page-0001.txt"], 120)),
                    patch.object(MODULE, "render_pdf_pages", side_effect=render_pages) as previews,
                    patch.object(MODULE, "ensure_image_inputs") as image_inputs,
                    patch.object(MODULE, "run_paddle_extract") as paddle,
                    patch.object(MODULE, "run_tesseract_extract") as tesseract,
                    patch.object(MODULE, "print_json", return_value=0),
                ):
                    self.assertEqual(0, MODULE.extract_document(**document_options(root, text_only=text_only)))
                for callback in (image_inputs, paddle, tesseract):
                    callback.assert_not_called()
                if text_only:
                    previews.assert_called_once_with(source, root / "artifacts" / "rendered")
                else:
                    previews.assert_not_called()
                results.append(json.loads((root / "envelope.json").read_text(encoding="utf-8")))
            preview_path = root / "artifacts" / "rendered" / "page-0001.png"
            self.assertTrue(preview_path.exists())
            self.assertEqual(results[0]["Artifacts"] + [str(preview_path)], results[1]["Artifacts"])
            results[1]["Artifacts"] = results[0]["Artifacts"]
            self.assertEqual(results[0], results[1])
            self.assertEqual("PdfTextLayer", results[1]["EngineName"])
            self.assertEqual([], results[1]["Candidates"])
            self.assertEqual(MODULE.file_sha256(source), results[1]["Pages"][0]["SourceContentHash"])

    def test_tesseract_retains_text_artifacts_and_warnings_without_candidates(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (1200, 1600), "white").save(source)

            def run(command, **kwargs):
                Path(command[2]).with_suffix(".tsv").write_text(
                    "level\tleft\ttop\twidth\theight\tconf\ttext\n"
                    "5\t40\t80\t200\t20\t70\tЗаявление\n", encoding="utf-8",
                )
                return MODULE.subprocess.CompletedProcess(command, 0, stderr="")

            results = []
            for text_only in (False, True):
                with (
                    patch.object(MODULE, "resolve_tessdata_dir", return_value=None),
                    patch.object(MODULE.subprocess, "run", side_effect=run),
                    patch.object(MODULE, "print_json", return_value=0),
                    patch.object(MODULE, "build_field_specific_crop_candidates") as fields,
                    patch.object(MODULE, "build_handwritten_weight_crop_candidates") as handwriting,
                ):
                    self.assertEqual(0, MODULE.extract_document(**document_options(
                        root, input_path=source, engine="tesseract", tesseract_path="tesseract.exe", text_only=text_only,
                    )))
                fields.assert_not_called()
                handwriting.assert_not_called()
                result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
                self.assertEqual([], result["Candidates"])
                self.assertEqual("Заявление", result["Pages"][0]["TextBlocks"][0]["Text"])
                self.assertEqual({"X": 40.0, "Y": 80.0}, result["Pages"][0]["TextBlocks"][0]["Polygon"][0])
                self.assertEqual("TESSERACT_LOW_CONFIDENCE", result["Warnings"][0]["Code"])
                self.assertTrue(all(Path(path).exists() for path in result["Artifacts"]))
                self.assertEqual(result["SourceContentHash"], result["Pages"][0]["SourceContentHash"])
                results.append(result)
            self.assertEqual(results[0], results[1])

    def test_text_only_fixture_matches_actual_producer(self) -> None:
        result = produce_text_only_fixture()
        fixture_path = ROOT.parents[1] / "tests" / "fixtures" / "local-text-extraction-envelope.json"
        self.assertEqual(json.loads(fixture_path.read_text(encoding="utf-8")), result)
        self.assertEqual([], result["Candidates"])
        self.assertEqual(2, len(result["Pages"]))
        self.assertEqual("rot90", result["Pages"][0]["SelectedVariant"])
        self.assertEqual("original", result["Pages"][1]["SelectedVariant"])
        self.assertEqual([1600, 1200], [result["Pages"][0]["Width"], result["Pages"][0]["Height"]])
        self.assertEqual({"X": 1520.0, "Y": 40.0}, result["Pages"][0]["TextBlocks"][0]["Polygon"][0])
        self.assertTrue(all(len(page["TextBlocks"]) == 2 for page in result["Pages"]))
        self.assertTrue(all(page["SourceContentHash"] == result["SourceContentHash"] for page in result["Pages"]))
        self.assertIn("PADDLE_LOW_CONFIDENCE", [warning["Code"] for warning in result["Warnings"]])
        self.assertFalse(any("zones" in Path(path).parts for path in result["Artifacts"]))


def write_test_pdf(path: Path, texts: list[str | None], width: float = 300, height: float = 400) -> None:
    writer = PdfWriter()
    for index, text in enumerate(texts):
        if text is None:
            scan = path.parent / f"scan-{index}.pdf"
            with Image.new("RGB", (300, 400), "white") as image:
                image.paste("black", (20, 20, 200, 30))
                image.save(scan, format="PDF")
            writer.add_page(MODULE.PdfReader(str(scan)).pages[0])
            continue
        page = writer.add_blank_page(width=width, height=height)
        if text:
            page[NameObject("/Resources")] = DictionaryObject({
                NameObject("/Font"): DictionaryObject({
                    NameObject("/F1"): DictionaryObject({
                        NameObject("/Type"): NameObject("/Font"),
                        NameObject("/Subtype"): NameObject("/Type1"),
                        NameObject("/BaseFont"): NameObject("/Helvetica"),
                    }),
                }),
            })
            content = DecodedStreamObject()
            content.set_data(f"BT /F1 12 Tf 10 350 Td ({text}) Tj ET".encode("ascii"))
            page[NameObject("/Contents")] = writer._add_object(content)
    with path.open("wb") as stream:
        writer.write(stream)


@contextmanager
def temporary_pdf_directory():
    documents = []
    open_document = MODULE.pdfium.PdfDocument

    def track_document(*args, **kwargs):
        document = open_document(*args, **kwargs)
        documents.append(document)
        return document

    with (
        tempfile.TemporaryDirectory() as temp_dir,
        patch.object(MODULE.pdfium, "PdfDocument", side_effect=track_document),
    ):
        try:
            yield Path(temp_dir)
        finally:
            # Keep legacy resource lifetime unchanged while releasing test PDFs on Windows.
            for document in documents:
                document.close()


class OcrSidecarTextOnlyPdfSafetyTests(unittest.TestCase):
    READABLE_TEXT = "Application for a certificate. Applicant address and supporting documents are included. " * 2

    def test_real_mixed_pdfs_ocr_every_page_in_text_mode_but_keep_legacy_shortcut(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            for second_page in ("", None):
                write_test_pdf(source, [self.READABLE_TEXT, second_page])
                for text_only in (False, True):
                    with self.subTest(scanned=second_page is None, text_only=text_only):
                        def predict(path):
                            return [{"rec_texts": [f"Текст страницы {Path(path).stem[-1]}"], "rec_scores": [0.95]}]

                        model = Mock()
                        model.predict.side_effect = predict
                        with (
                            patch.object(MODULE, "create_paddle_ocr", return_value=model) as create_model,
                            patch.object(MODULE, "print_json", return_value=0),
                        ):
                            self.assertEqual(0, MODULE.extract_document(**document_options(root, text_only=text_only)))
                        result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
                        self.assertEqual(2, len(result["Pages"]))
                        if text_only:
                            self.assertEqual(2, model.predict.call_count)
                            self.assertEqual(["Текст страницы 1", "Текст страницы 2"], [
                                page["TextBlocks"][0]["Text"] for page in result["Pages"]
                            ])
                            warning = next(warning for warning in result["Warnings"] if warning["Code"] == "TEXT_LAYER_INCOMPLETE")
                            self.assertIn("[2]", warning["Message"])
                            self.assertIn("every page", warning["Message"])
                            self.assertEqual([], result["Candidates"])
                        else:
                            create_model.assert_not_called()
                            self.assertEqual("PdfTextLayer", result["EngineName"])
                            self.assertEqual([], result["Pages"][1]["TextBlocks"])

    def test_every_real_text_page_can_still_use_text_only_shortcut(self) -> None:
        with temporary_pdf_directory() as root:
            write_test_pdf(root / "sample-document.pdf", [self.READABLE_TEXT, self.READABLE_TEXT])
            for tiled in (False, True):
                with (
                    self.subTest(tiled=tiled),
                    patch.object(MODULE, "create_paddle_ocr") as model,
                    patch.object(MODULE, "run_tile_worker") as worker,
                    patch.object(MODULE, "print_json", return_value=0),
                ):
                    options = document_options(root, text_only=True)
                    if tiled:
                        options.update(offline=True, disable_text_document_unwarping=True,
                                       force_text_original_variant=True, tile_text_page=True)
                    self.assertEqual(0, MODULE.extract_document(**options))
                    model.assert_not_called()
                    worker.assert_not_called()
                    result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
                    self.assertEqual("PdfTextLayer", result["EngineName"])
                    self.assertTrue(all(page["TextBlocks"] for page in result["Pages"]))
                    self.assertEqual(2, sum(Path(path).suffix == ".png" for path in result["Artifacts"]))

    def test_unacceptable_text_on_one_page_cannot_hide_in_document_quality(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "sample-document.pdf"
            source.write_bytes(b"sample document")
            for bad_text in ("Brief footer", "Ð" * 80):
                pages = [
                    {"PageNumber": 1, "TextBlocks": [block(self.READABLE_TEXT * 50, 1.0, 0, 0)]},
                    {"PageNumber": 2, "TextBlocks": [block(bad_text, 1.0, 0, 0)]},
                ]
                self.assertTrue(MODULE.assess_text_layer_quality(pages)[0])
                with (
                    self.subTest(bad_text=bad_text),
                    patch.object(MODULE, "extract_pdf_text_layer", return_value=(pages, [], 4000)),
                    patch.object(MODULE, "render_pdf_pages") as render,
                    patch.object(MODULE, "create_paddle_ocr") as model,
                ):
                    with self.assertRaisesRegex(RuntimeError, r"pages \[2\] lack usable embedded text"):
                        MODULE.extract_document(**document_options(root, engine="text-layer", text_only=True))
                render.assert_not_called()
                model.assert_not_called()

    def test_real_zero_page_pdf_reports_empty_document_before_rendering_or_ocr(self) -> None:
        with temporary_pdf_directory() as root:
            write_test_pdf(root / "sample-document.pdf", [])
            with (
                patch.object(MODULE, "render_pdf_pages") as render,
                patch.object(MODULE, "create_paddle_ocr") as model,
            ):
                with self.assertRaisesRegex(RuntimeError, "TEXT_ONLY_EMPTY_DOCUMENT: PDF contains no pages"):
                    MODULE.extract_document(**document_options(root, text_only=True))
            render.assert_not_called()
            model.assert_not_called()
            self.assertFalse((root / "envelope.json").exists())

    def test_real_blank_page_pdf_falls_back_with_empty_text_diagnostic(self) -> None:
        with temporary_pdf_directory() as root:
            write_test_pdf(root / "sample-document.pdf", [""])
            model = Mock()
            model.predict.return_value = [{}]
            with (
                patch.object(MODULE, "create_paddle_ocr", return_value=model),
                patch.object(MODULE, "print_json", return_value=0),
            ):
                self.assertEqual(0, MODULE.extract_document(**document_options(root, text_only=True)))
            self.assertEqual(1, model.predict.call_count)
            result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
            self.assertEqual(1, len(result["Pages"]))
            self.assertEqual([], result["Pages"][0]["TextBlocks"])
            self.assertIn("TEXT_LAYER_EMPTY", [warning["Code"] for warning in result["Warnings"]])

    def test_pdf_page_cap_is_text_only_and_precedes_pdfium_and_ocr(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            write_test_pdf(source, [""] * 101)
            with patch.object(MODULE.pdfium, "PdfDocument") as pdfium:
                with self.assertRaisesRegex(RuntimeError, "TEXT_ONLY_PAGE_LIMIT.*100-page"):
                    MODULE.extract_document(**document_options(root, text_only=True))
            pdfium.assert_not_called()
            pages, _, _ = MODULE.extract_pdf_text_layer(source, root / "legacy-artifacts")
            self.assertEqual(101, len(pages))
            MODULE.validate_text_only_page_count(100, "PDF")

    def test_pdf_pixel_guard_precedes_raster_allocation_and_accounts_for_rounding(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            write_test_pdf(source, [self.READABLE_TEXT], width=2500.5, height=2000)
            with (
                patch.object(MODULE, "render_pdf_pages") as render,
                patch.object(MODULE, "create_paddle_ocr") as model,
            ):
                with self.assertRaisesRegex(RuntimeError, "TEXT_ONLY_PIXEL_LIMIT.*20000000 pixels"):
                    MODULE.extract_document(**document_options(root, text_only=True))
            render.assert_not_called()
            model.assert_not_called()
            pages, _, _ = MODULE.extract_pdf_text_layer(source, root / "legacy-artifacts")
            self.assertEqual(1, len(pages))
            MODULE.validate_text_only_page_pixels(2500, 2000, "PDF page", scale=2.0)

    def test_image_pixel_guard_is_text_only_and_precedes_model_creation(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            source.write_bytes(b"image header")
            image = Mock(width=5001, height=4000)
            image_context = Mock()
            image_context.__enter__ = Mock(return_value=image)
            image_context.__exit__ = Mock(return_value=False)
            with (
                patch.object(MODULE.Image, "open", return_value=image_context),
                patch.object(MODULE, "run_paddle_extract", return_value={"Pages": [], "Warnings": []}) as paddle,
                patch.object(MODULE, "print_json", return_value=0),
            ):
                with self.assertRaisesRegex(RuntimeError, "TEXT_ONLY_PIXEL_LIMIT"):
                    MODULE.extract_document(**document_options(root, input_path=source, text_only=True))
                paddle.assert_not_called()
                self.assertEqual(0, MODULE.extract_document(**document_options(root, input_path=source)))
                paddle.assert_called_once()
            MODULE.validate_text_only_page_pixels(5000, 4000, "Image")


class OcrSidecarOfflineTextTests(unittest.TestCase):
    MODEL_NAMES = (
        "PP-OCRv5_mobile_det", "eslav_PP-OCRv5_mobile_rec",
        "PP-LCNet_x1_0_doc_ori", "UVDoc", "PP-LCNet_x1_0_textline_ori",
    )

    def make_model_pack(self, root: Path, names=None) -> Path:
        cache = root / "models" / "paddlex"
        for name in names or self.MODEL_NAMES:
            directory = cache / "official_models" / name
            directory.mkdir(parents=True, exist_ok=True)
            for filename in ("inference.json", "inference.yml", "inference.pdiparams"):
                (directory / filename).write_bytes(b"fixture")
        return cache

    def write_png_header(self, path: Path, width: int, height: int) -> None:
        stream = io.BytesIO()
        with Image.new("RGB", (1, 1), "white") as image:
            image.save(stream, format="PNG")
        content = bytearray(stream.getvalue())
        content[16:24] = struct.pack(">II", width, height)
        content[29:33] = struct.pack(">I", zlib.crc32(content[12:29]))
        path.write_bytes(content)

    def test_cli_bounds_are_optional_and_propagated_without_changing_defaults(self) -> None:
        base = ["sidecar", "extract", "--input", "source.png", "--output", "envelope.json"]
        with patch.object(sys, "argv", base):
            args = MODULE.parse_args()
        self.assertFalse(args.offline)
        self.assertIsNone(args.max_pages)
        self.assertIsNone(args.max_pixels)
        self.assertEqual("auto", args.engine)
        self.assertEqual("mobile-cpu", args.paddle_profile)
        with (
            patch.object(sys, "argv", base + ["--text-only", "--offline", "--max-pages", "20", "--max-pixels", "20000000"]),
            patch.object(MODULE, "extract_document", return_value=0) as extract,
        ):
            self.assertEqual(0, MODULE.main())
        self.assertTrue(extract.call_args.kwargs["offline"])
        self.assertEqual(20, extract.call_args.kwargs["max_pages"])
        self.assertEqual(20_000_000, extract.call_args.kwargs["max_pixels"])

    def test_dewarp_diagnostic_cli_requires_explicit_offline_text_paddle(self) -> None:
        base = ["sidecar", "extract", "--input", "source.png", "--output", "envelope.json", "--disable-text-document-unwarping"]
        for flags in ([], ["--text-only"], ["--offline", "--engine", "paddle"],
                      ["--text-only", "--offline"], ["--text-only", "--offline", "--engine", "tesseract"]):
            with self.subTest(flags=flags), patch.object(sys, "argv", base + flags), redirect_stderr(io.StringIO()):
                with self.assertRaises(SystemExit) as raised:
                    MODULE.parse_args()
                self.assertEqual(2, raised.exception.code)
        with (
            patch.object(sys, "argv", base + ["--text-only", "--offline", "--engine", "paddle"]),
            patch.object(MODULE, "extract_document", return_value=0) as extract,
        ):
            self.assertEqual(0, MODULE.main())
        self.assertTrue(extract.call_args.kwargs["disable_text_document_unwarping"])

    def test_dewarp_diagnostic_rejects_other_modes_before_files_or_models(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            for text_only, offline, engine in ((False, False, "paddle"), (True, False, "paddle"),
                                              (False, True, "paddle"), (True, True, "auto"), (True, True, "tesseract")):
                with self.subTest(text_only=text_only, offline=offline, engine=engine), patch.object(MODULE, "create_paddle_ocr") as constructor:
                    with self.assertRaisesRegex(ValueError, "^OFFLINE_TEXT_PADDLE_REQUIRED$"):
                        MODULE.extract_document(**document_options(root, text_only=text_only, offline=offline,
                                                                   engine=engine, disable_text_document_unwarping=True))
                    constructor.assert_not_called()
            self.assertFalse((root / "artifacts").exists())
            self.assertFalse((root / "envelope.json").exists())

    def test_original_variant_diagnostic_is_offline_text_only_and_skips_rotation(self) -> None:
        base = ["sidecar", "extract", "--input", "source.jpg", "--output", "envelope.json",
                "--force-text-original-variant"]
        for flags in ([], ["--text-only", "--offline", "--engine", "paddle"],
                      ["--text-only", "--offline", "--engine", "paddle",
                       "--disable-text-document-unwarping"]):
            with self.subTest(flags=flags), patch.object(sys, "argv", base + flags):
                if "--disable-text-document-unwarping" in flags:
                    self.assertTrue(MODULE.parse_args().force_text_original_variant)
                else:
                    with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
                        MODULE.parse_args()
                    self.assertEqual(2, raised.exception.code)
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.jpg"
            Image.new("RGB", (80, 40), "white").save(source)
            variants = MODULE.prepare_paddle_variants(source, root / "variants", original_only=True)
            self.assertEqual([("original", source)], variants)
            self.assertEqual([], list((root / "variants").glob("*.jpg")))
            self.assertEqual(3, len(MODULE.prepare_paddle_variants(source, root / "normal")))
            portrait = root / "portrait.jpg"
            Image.new("RGB", (40, 80), "white").save(portrait)
            self.assertEqual(1, len(MODULE.prepare_paddle_variants(portrait, root / "portrait-normal")))
            self.assertEqual(3, len(MODULE.prepare_paddle_variants(
                portrait, root / "portrait-retry", include_portrait_rotations=True)))
            self.assertTrue(MODULE.should_retry_portrait_rotation(40, 80, False, {"orientation_angle": 270}))
            self.assertFalse(MODULE.should_retry_portrait_rotation(40, 80, True, {"orientation_angle": 270}))
            self.assertFalse(MODULE.should_retry_portrait_rotation(40, 80, False, {"orientation_angle": 0}))
            self.assertFalse(MODULE.should_retry_portrait_rotation(80, 40, False, {"orientation_angle": 90}))
            with patch.object(MODULE, "create_paddle_ocr") as constructor:
                with self.assertRaisesRegex(ValueError, "OFFLINE_TEXT_PADDLE_REQUIRED"):
                    MODULE.extract_document(**document_options(root, input_path=source,
                                                               text_only=True, offline=True, engine="paddle",
                                                               force_text_original_variant=True))
                constructor.assert_not_called()

    def test_source_bound_tile_flag_requires_full_lab_gate(self) -> None:
        base = ["sidecar", "extract", "--input", "source.jpg", "--output", "envelope.json",
                "--tile-text-page"]
        required = ["--text-only", "--offline", "--engine", "paddle",
                    "--disable-text-document-unwarping", "--force-text-original-variant"]
        for missing in (None, "--text-only", "--offline", "paddle",
                        "--disable-text-document-unwarping", "--force-text-original-variant"):
            flags = required if missing is None else [flag for flag in required if flag != missing]
            with self.subTest(missing=missing), patch.object(sys, "argv", base + flags):
                if missing is None:
                    self.assertTrue(MODULE.parse_args().tile_text_page)
                else:
                    with redirect_stderr(io.StringIO()), self.assertRaises(SystemExit) as raised:
                        MODULE.parse_args()
                    self.assertEqual(2, raised.exception.code)

    def test_native_tiles_remap_once_and_assign_seam_to_one_core(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (120, 2481), "white").save(source)
            original_hash = MODULE.file_sha256(source)

            def fake_page_result(*args, **kwargs):
                index = int(args[0].stem.split("-")[-1])
                entries = {0: [("upper", 90), ("seam", 1231)],
                           1: [("seam", 90), ("lower", 850)]}[index]
                blocks = [dict(block(text, 0.9, 10, top), Id=f"tile-{index}-{i}")
                          for i, (text, top) in enumerate(entries)]
                return {"page": {"TextBlocks": blocks}, "elapsed": 1.0}

            with patch.object(MODULE, "run_tile_worker", side_effect=fake_page_result):
                result = MODULE.build_tiled_paddle_page_result(
                    source, root / "tiles", 1, "mobile-cpu", "ru", None, None)
            self.assertEqual(["upper", "seam", "lower"],
                             [item["Text"] for item in result["page"]["TextBlocks"]])
            self.assertEqual([90.0, 1231.0, 1991.0],
                             [item["Polygon"][0]["Y"] for item in result["page"]["TextBlocks"]])
            self.assertEqual((120, 2481), (result["page"]["Width"], result["page"]["Height"]))
            self.assertEqual(2, len(result["tiles"]))
            self.assertEqual(original_hash, MODULE.file_sha256(source))

    def test_native_tile_geometry_and_count_fail_closed(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (100, 100), "white").save(source)
            ocr = Mock()
            ocr.predict.return_value = [{"rec_texts": ["text"], "rec_scores": [0.9],
                                         "rec_polys": []}]
            with self.assertRaisesRegex(MODULE.TextOnlyError, "TEXT_ONLY_TILE_GEOMETRY_INVALID"):
                MODULE.build_paddle_page_result(ocr, source, 1, "mobile-cpu", None, None,
                                                require_polygons=True)
            with patch.object(MODULE, "TEXT_TILE_MAX_TILES", 1):
                Image.new("RGB", (100, 2500), "white").save(source)
                with self.assertRaisesRegex(MODULE.TextOnlyError, "TEXT_ONLY_TILE_LIMIT"):
                    MODULE.build_tiled_paddle_page_result(source, root / "tiles", 1,
                                                          "mobile-cpu", "ru", None, None)
                self.assertEqual([], list((root / "tiles").glob("*.jpg")))

    def test_late_tile_failure_never_publishes_partial_envelope(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (120, 2481), "white").save(source)
            original_hash = MODULE.file_sha256(source)
            result = {"page": {"TextBlocks": []}, "elapsed": 0.1}
            options = document_options(root, input_path=source, text_only=True, offline=True,
                                       disable_text_document_unwarping=True,
                                       force_text_original_variant=True, tile_text_page=True)
            with patch.object(MODULE, "run_tile_worker",
                              side_effect=[result, MODULE.TextOnlyError("TEXT_ONLY_TILE_RUNTIME_FAILED")]):
                with self.assertRaisesRegex(MODULE.TextOnlyError, "TEXT_ONLY_TILE_RUNTIME_FAILED"):
                    MODULE._extract_document(**options)
            self.assertFalse(options["output_path"].exists())
            self.assertEqual(original_hash, MODULE.file_sha256(source))

    def test_invalid_or_legacy_bounds_are_rejected(self) -> None:
        base = ["sidecar", "extract", "--input", "source.png", "--output", "envelope.json"]
        for flags in (
            ["--offline"], ["--max-pages", "20"], ["--max-pixels", "100"],
            *[["--text-only", "--max-pages", value] for value in ("0", "-1", "101", "secret")],
            *[["--text-only", "--max-pixels", value] for value in ("0", "-1", "20000001", "secret")],
        ):
            with self.subTest(flags=flags), patch.object(sys, "argv", base + flags), redirect_stderr(io.StringIO()) as errors:
                with self.assertRaises(SystemExit) as raised:
                    MODULE.parse_args()
                self.assertEqual(2, raised.exception.code)
                self.assertNotIn("secret", errors.getvalue())
        for bad in (0, -1, 101, True, 1.5):
            with self.subTest(value=bad), self.assertRaisesRegex(ValueError, "INVALID_TEXT_ONLY_LIMIT"):
                MODULE.text_only_limit(bad, 100, 20)

    def test_source_cap_precedes_every_decoder_and_parser(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            for suffix in (".pdf", ".png"):
                source = root / ("private-source" + suffix)
                with source.open("wb") as stream:
                    stream.seek(MODULE.TEXT_ONLY_MAX_SOURCE_BYTES)
                    stream.write(b"x")
                with (
                    patch.object(MODULE, "PdfReader") as reader,
                    patch.object(MODULE.pdfium, "PdfDocument") as pdf,
                    patch.object(MODULE.Image, "open") as image,
                    patch.object(MODULE, "create_paddle_ocr") as model,
                ):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_SOURCE_LIMIT$"):
                        MODULE.extract_document(**document_options(root, input_path=source, text_only=True, offline=True))
                for callback in (reader, pdf, image, model):
                    callback.assert_not_called()
                self.assertFalse((root / "artifacts").exists())

    def test_all_pdf_geometry_is_checked_before_text_parsing_or_rendering(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            for page_count, width, error, limit in (
                (21, 300, "TEXT_ONLY_PAGE_LIMIT", None),
                (2, 2500.5, "TEXT_ONLY_PIXEL_LIMIT", None),
                (2, 300, "TEXT_ONLY_PAGE_LIMIT", 1),
            ):
                writer = PdfWriter()
                writer.add_blank_page(width=300, height=400)
                for index in range(1, page_count):
                    writer.add_blank_page(width=width, height=2000 if width > 300 else 400)
                with source.open("wb") as stream:
                    writer.write(stream)
                with (
                    self.subTest(error=error, limit=limit),
                    patch.object(MODULE, "PdfReader") as reader,
                    patch.object(MODULE.pdfium.PdfPage, "render") as render,
                    patch.object(MODULE, "create_paddle_ocr") as model,
                ):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^" + error + "$"):
                        MODULE.extract_document(**document_options(root, text_only=True, offline=True, max_pages=limit))
                for callback in (reader, render, model):
                    callback.assert_not_called()
                self.assertFalse((root / "artifacts").exists())

    def test_image_pixel_guard_precedes_decoding_and_conversion(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            for width, height, limit in ((5001, 4000, None), (11, 10, 100)):
                self.write_png_header(source, width, height)
                with (
                    self.subTest(width=width),
                    patch.object(Image.Image, "load") as decode,
                    patch.object(Image.Image, "convert") as convert,
                    patch.object(MODULE, "create_paddle_ocr") as model,
                    patch.object(MODULE, "ensure_image_inputs") as prepare,
                ):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_PIXEL_LIMIT$"):
                        MODULE.extract_document(**document_options(root, input_path=source, text_only=True, offline=True, max_pixels=limit))
                for callback in (decode, convert, model, prepare):
                    callback.assert_not_called()
            self.write_png_header(source, 5000, 4000)
            MODULE.preflight_text_only_source(source, 20, 20_000_000)

    def test_exact_source_page_and_pdf_pixel_caps_are_accepted(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            write_test_pdf(source, [""] * 20, width=2500, height=2000)
            with source.open("ab") as stream:
                stream.truncate(MODULE.TEXT_ONLY_MAX_SOURCE_BYTES)
            MODULE.preflight_text_only_source(source, 20, 20_000_000)

    def test_unreadable_and_unsupported_sources_fail_without_content(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            for name in ("private.pdf", "private.png", "private.webp", "absent.png"):
                source = root / name
                if name != "absent.png":
                    source.write_bytes(b"private document content")
                with self.subTest(name=name), self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_SOURCE_INVALID$"):
                    MODULE.extract_document(**document_options(root, input_path=source, text_only=True, offline=True))
            with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_SOURCE_INVALID$"):
                write_test_pdf(root / "empty.pdf", [])
                MODULE.extract_document(**document_options(root, input_path=root / "empty.pdf", text_only=True, offline=True))

    def test_every_model_and_required_file_is_checked_before_constructor(self) -> None:
        constructor = Mock()
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            cache = self.make_model_pack(root)
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
            ):
                for name in self.MODEL_NAMES:
                    for filename in ("inference.json", "inference.yml", "inference.pdiparams"):
                        path = cache / "official_models" / name / filename
                        for empty in (False, True):
                            with self.subTest(model=name, file=filename, empty=empty):
                                if empty:
                                    path.write_bytes(b"")
                                else:
                                    path.unlink()
                                with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                                    MODULE.create_paddle_ocr("mobile-cpu", "ru", offline=True)
                                constructor.assert_not_called()
                                path.write_bytes(b"fixture")

    def test_missing_configured_root_never_uses_bundled_or_home_cache(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            cache = self.make_model_pack(root)
            for configured in (None, str(root / "missing"), "models/paddlex"):
                with (
                    self.subTest(configured=configured),
                    patch.dict(MODULE.os.environ, {} if configured is None else {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": configured}, clear=True),
                    patch.object(MODULE, "_BUNDLED_PADDLE_CACHE_HOME", cache),
                    patch.object(MODULE, "resolve_bundled_paddle_cache_home") as fallback,
                    patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=Mock())}),
                ):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                        MODULE.create_paddle_ocr("mobile-cpu", "ru", offline=True)
                    fallback.assert_not_called()

    def test_all_auxiliary_paths_are_explicit_and_mobile_flags_are_unchanged(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            cache = self.make_model_pack(root)
            constructor = Mock()

            def construct(**kwargs):
                self.assertEqual(str(cache.resolve()), MODULE.os.environ["PADDLE_PDX_CACHE_HOME"])
                self.assertEqual("True", MODULE.os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"])
                self.assertEqual("1", MODULE.os.environ["HF_HUB_OFFLINE"])
                return object()

            constructor.side_effect = construct
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache), "PADDLE_PDX_CACHE_HOME": "unrelated-home"}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
            ):
                MODULE.create_paddle_ocr("mobile-cpu", "ru", offline=True)
                self.assertEqual("unrelated-home", MODULE.os.environ["PADDLE_PDX_CACHE_HOME"])
            kwargs = constructor.call_args.kwargs
            for argument, name in zip((
                "text_detection_model_dir", "text_recognition_model_dir", "doc_orientation_classify_model_dir",
                "doc_unwarping_model_dir", "textline_orientation_model_dir",
            ), self.MODEL_NAMES):
                self.assertEqual(str((cache / "official_models" / name).resolve()), kwargs[argument])
            self.assertEqual("PP-OCRv5_mobile_det", kwargs["text_detection_model_name"])
            self.assertEqual("eslav_PP-OCRv5_mobile_rec", kwargs["text_recognition_model_name"])
            self.assertEqual("cpu", kwargs["device"])
            for flag in ("use_doc_orientation_classify", "use_doc_unwarping", "use_textline_orientation"):
                self.assertIs(True, kwargs[flag])

    def test_dewarp_diagnostic_changes_only_unwarping_and_preserves_model_paths(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            cache = self.make_model_pack(Path(temp_dir))
            constructor = Mock()
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
            ):
                MODULE.create_paddle_ocr("mobile-cpu", "ru", offline=True)
                baseline = constructor.call_args.kwargs.copy()
                MODULE.create_paddle_ocr("mobile-cpu", "ru", offline=True, disable_document_unwarping=True)
                candidate = constructor.call_args.kwargs
                self.assertEqual({**baseline, "use_doc_unwarping": False}, candidate)
                self.assertIs(True, baseline["use_doc_unwarping"])

    def test_default_profile_preserves_language_and_override_model_selection(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            cache = self.make_model_pack(Path(temp_dir), self.MODEL_NAMES + ("PP-OCRv5_server_det", "PP-OCRv5_server_rec", "custom-rec"))
            constructor = Mock()
            constructor._get_ocr_model_names.return_value = ("PP-OCRv5_server_det", "eslav_PP-OCRv5_mobile_rec")
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
            ):
                MODULE.create_paddle_ocr("default-cpu", "ru", offline=True)
                kwargs = constructor.call_args.kwargs
                self.assertEqual("eslav_PP-OCRv5_mobile_rec", kwargs["text_recognition_model_name"])
                self.assertTrue(kwargs["text_detection_model_dir"].endswith("PP-OCRv5_server_det"))
                self.assertNotIn("use_doc_unwarping", kwargs)
                MODULE.create_paddle_ocr("default-cpu", "ru", recognition_model_name="custom-rec", offline=True)
                self.assertTrue(constructor.call_args.kwargs["text_recognition_model_dir"].endswith("custom-rec"))
                constructor._get_ocr_model_names.assert_called_once_with(None, "ru", None)

    def test_custom_paths_must_be_complete_and_inside_the_package(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            cache = self.make_model_pack(root, self.MODEL_NAMES + ("custom-rec",))
            custom = str(cache / "official_models" / "custom-rec")
            constructor = Mock()
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
            ):
                MODULE.create_paddle_ocr("mobile-cpu", "ru", recognition_model_dir=custom, recognition_model_name="custom-rec", offline=True)
                self.assertEqual(custom, constructor.call_args.kwargs["text_recognition_model_dir"])
                constructor.reset_mock()
                for path in (str(root), str(cache / "official_models" / "absent")):
                    with self.subTest(path=path), self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                        MODULE.create_paddle_ocr("mobile-cpu", "ru", recognition_model_dir=path, offline=True)
                    constructor.assert_not_called()

    def test_downstream_downloads_are_blocked_and_guards_are_restored(self) -> None:
        import socket
        import urllib.request
        import requests

        original = urllib.request.urlretrieve
        with MODULE.offline_paddle_runtime(True):
            for attempt in (
                lambda: urllib.request.urlretrieve("https://invalid.example/model"),
                lambda: urllib.request.urlopen("https://invalid.example/model"),
                lambda: requests.get("https://invalid.example/model"),
                lambda: socket.create_connection(("invalid.example", 443)),
                lambda: socket.getaddrinfo("invalid.example", 443),
            ):
                with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                    attempt()
            with socket.socket() as connection:
                for connect in (connection.connect, connection.connect_ex):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                        connect(("127.0.0.1", 1))
        self.assertIs(original, urllib.request.urlretrieve)

    def test_offline_environment_and_guards_precede_paddle_import(self) -> None:
        import urllib.request

        with tempfile.TemporaryDirectory() as temp_dir:
            cache = self.make_model_pack(Path(temp_dir))
            original_import = builtins.__import__
            constructor = Mock()
            imported = []

            def import_module(name, *args, **kwargs):
                if name == "paddleocr":
                    imported.append(name)
                    self.assertEqual(str(cache), MODULE.os.environ["PADDLE_PDX_CACHE_HOME"])
                    self.assertEqual("False", MODULE.os.environ["PADDLE_PDX_EAGER_INIT"])
                    for key in ("OMP_NUM_THREADS", "MKL_NUM_THREADS", "OPENBLAS_NUM_THREADS"):
                        self.assertEqual("2", MODULE.os.environ[key])
                    self.assertEqual("True", MODULE.os.environ["PADDLE_PDX_DISABLE_MODEL_SOURCE_CHECK"])
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                        urllib.request.urlretrieve("https://invalid.example/model")
                    return SimpleNamespace(PaddleOCR=constructor)
                return original_import(name, *args, **kwargs)

            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.object(builtins, "__import__", side_effect=import_module),
            ):
                MODULE.create_paddle_ocr("mobile-cpu", "ru", offline=True)
            self.assertEqual(["paddleocr"], imported)
            constructor.assert_called_once()

    def test_download_guard_remains_active_during_prediction(self) -> None:
        import urllib.request

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (30, 40), "white").save(source)
            cache = self.make_model_pack(root)
            model = Mock()
            model.predict.side_effect = lambda *args: urllib.request.urlretrieve("https://invalid.example/model")
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=Mock(return_value=model))}),
                patch.object(MODULE, "run_tesseract_extract") as fallback,
            ):
                with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                    MODULE.extract_document(**document_options(root, input_path=source, engine="auto", text_only=True, offline=True))
            fallback.assert_not_called()

    def test_jpeg_and_animated_png_reject_before_pixel_decoding(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.jpg"
            with Image.new("RGB", (11, 10), "white") as image:
                image.save(source)
                animated = root / "animated.png"
                with Image.new("RGB", (11, 10), "black") as second:
                    image.save(animated, save_all=True, append_images=[second], format="PNG")
            for path, code in ((source, "TEXT_ONLY_PIXEL_LIMIT"), (animated, "TEXT_ONLY_SOURCE_INVALID")):
                with (
                    self.subTest(path=path),
                    patch.object(Image.Image, "load") as decode,
                    patch.object(Image.Image, "convert") as convert,
                ):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^" + code + "$"):
                        MODULE.extract_document(**document_options(root, input_path=path, text_only=True, offline=True, max_pixels=100 if path == source else None))
                decode.assert_not_called()
                convert.assert_not_called()

    def test_offline_text_pdf_needs_no_models_and_mixed_pdf_cannot_omit_pages(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            text = OcrSidecarTextOnlyPdfSafetyTests.READABLE_TEXT
            write_test_pdf(source, [text, text])
            with (
                patch.dict(MODULE.os.environ, {}, clear=True),
                patch.object(MODULE, "create_paddle_ocr") as constructor,
                patch.object(MODULE, "print_json", return_value=0),
            ):
                self.assertEqual(0, MODULE.extract_document(**document_options(root, text_only=True, offline=True)))
            constructor.assert_not_called()
            result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
            self.assertEqual("PdfTextLayer", result["EngineName"])
            self.assertEqual([1, 2], [page["PageNumber"] for page in result["Pages"]])
            cache = self.make_model_pack(root)
            model = Mock()
            model.predict.return_value = [{"rec_texts": ["Page text"], "rec_scores": [0.95]}]
            for tail in ("", None):
                write_test_pdf(source, [text, tail])
                model.reset_mock()
                with (
                    self.subTest(scanned=tail is None),
                    patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                    patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=Mock(return_value=model))}),
                    patch.object(MODULE, "print_json", return_value=0),
                ):
                    self.assertEqual(0, MODULE.extract_document(**document_options(root, text_only=True, offline=True)))
                self.assertEqual(2, model.predict.call_count)
                result = json.loads((root / "envelope.json").read_text(encoding="utf-8"))
                self.assertEqual([1, 2], [page["PageNumber"] for page in result["Pages"]])
                self.assertTrue(all(page["TextBlocks"] for page in result["Pages"]))
                self.assertEqual([], result["Candidates"])

    def test_incomplete_pack_is_not_hidden_by_auto_tesseract_fallback(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (30, 40), "white").save(source)
            cache = self.make_model_pack(root)
            (cache / "official_models" / "UVDoc" / "inference.pdiparams").unlink()
            constructor = Mock()
            with (
                patch.dict(MODULE.os.environ, {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(cache)}, clear=True),
                patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
                patch.object(MODULE, "run_tesseract_extract") as fallback,
            ):
                with self.assertRaisesRegex(MODULE.TextOnlyError, "^OFFLINE_MODELS_MISSING$"):
                    MODULE.extract_document(**document_options(root, input_path=source, engine="auto", tesseract_path="local.exe", text_only=True, offline=True))
            constructor.assert_not_called()
            fallback.assert_not_called()
            self.assertFalse((root / "envelope.json").exists())

    def test_cli_errors_are_structured_code_only_without_tracebacks(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            result = subprocess.run([
                sys.executable, "-B", str(MODULE_PATH), "extract", "--input", str(Path(temp_dir) / "private-source.png"),
                "--output", str(Path(temp_dir) / "result.json"), "--text-only", "--offline",
            ], capture_output=True, text=True, check=False)
        self.assertEqual(1, result.returncode)
        self.assertEqual({"errorCode": "TEXT_ONLY_SOURCE_INVALID"}, json.loads(result.stderr))
        self.assertEqual("", result.stdout)

    def test_source_decode_failure_is_distinct_from_runtime_and_output_failure(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (30, 40), "white").save(source)
            options = document_options(root, input_path=source, text_only=True, offline=True)
            for error in (RuntimeError("private runtime details"), OSError("private runtime details")):
                with (
                    self.subTest(error=type(error).__name__),
                    patch.object(MODULE, "run_paddle_extract", side_effect=error),
                ):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_RUNTIME_FAILED$"):
                        MODULE.extract_document(**options)
            with (
                patch.object(MODULE, "run_paddle_extract", return_value={"Pages": []}),
                patch.object(MODULE, "write_json", side_effect=OSError("private output details")),
            ):
                with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_RUNTIME_FAILED$"):
                    MODULE.extract_document(**options)
            self.write_png_header(source, 30, 40)
            with patch.object(MODULE, "create_paddle_ocr", return_value=Mock()):
                with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_SOURCE_INVALID$"):
                    MODULE.extract_document(**options)

    def test_pdf_reader_and_raster_errors_are_source_invalid(self) -> None:
        with temporary_pdf_directory() as root:
            source = root / "sample-document.pdf"
            write_test_pdf(source, [""])
            for callback, error in (
                ("extract_pdf_text_layer", MODULE.PdfReadError("private PDF details")),
                ("render_pdf_pages", MODULE.pdfium.PdfiumError("private PDF details")),
            ):
                with self.subTest(callback=callback), patch.object(MODULE, callback, side_effect=error):
                    with self.assertRaisesRegex(MODULE.TextOnlyError, "^TEXT_ONLY_SOURCE_INVALID$"):
                        MODULE.extract_document(**document_options(root, text_only=True, offline=True))

    def test_runtime_fallback_warning_omits_exception_content(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            source = root / "source.png"
            Image.new("RGB", (30, 40), "white").save(source)
            with (
                patch.object(MODULE, "run_paddle_extract", side_effect=RuntimeError("private runtime details")),
                patch.object(MODULE, "run_tesseract_extract", return_value={"Pages": [], "Warnings": []}) as fallback,
                patch.object(MODULE, "print_json", return_value=0),
            ):
                self.assertEqual(0, MODULE.extract_document(**document_options(root, input_path=source, engine="auto", text_only=True, offline=True, tesseract_path="local.exe")))
            self.assertNotIn("private", json.dumps(fallback.call_args.kwargs["warnings"]))
            self.assertNotIn("private", (root / "envelope.json").read_text())

    def test_legacy_constructor_has_no_offline_auxiliary_overrides(self) -> None:
        constructor = Mock()
        with (
            patch.dict(MODULE.os.environ, {}, clear=True),
            patch.dict(sys.modules, {"paddleocr": SimpleNamespace(PaddleOCR=constructor)}),
            patch.object(MODULE, "offline_model_root") as validate,
        ):
            MODULE.create_paddle_ocr("mobile-cpu", "ru")
            kwargs = constructor.call_args.kwargs
            self.assertIsNone(kwargs["text_detection_model_dir"])
            self.assertIsNone(kwargs["text_recognition_model_dir"])
            self.assertNotIn("doc_unwarping_model_dir", kwargs)
            MODULE.create_paddle_ocr("default-cpu", "ru")
            self.assertEqual({"device": "cpu", "lang": "ru", "text_detection_model_dir": None, "text_recognition_model_dir": None}, constructor.call_args.kwargs)
        validate.assert_not_called()


class OcrSidecarBundledModelCacheTests(unittest.TestCase):
    def test_frozen_runtime_resolves_model_cache_next_to_sidecar_directory(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            sidecar_dir = Path(temp_dir) / "tools" / "ocr-sidecar"
            runtime_executable = sidecar_dir / "runtime" / "formpotok-ocr-sidecar.exe"
            model_cache = sidecar_dir / "models" / "paddlex"
            runtime_executable.parent.mkdir(parents=True)
            runtime_executable.touch()
            model_cache.mkdir(parents=True)

            with (
                patch.dict(MODULE.os.environ, {}, clear=True),
                patch.object(MODULE.sys, "frozen", True, create=True),
                patch.object(MODULE.sys, "executable", str(runtime_executable)),
            ):
                resolved = MODULE.resolve_bundled_paddle_cache_home()

            self.assertEqual(model_cache.resolve(), resolved)

    def test_configured_model_cache_takes_precedence_for_frozen_runtime(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            configured = Path(temp_dir) / "configured-cache"
            configured.mkdir()

            with (
                patch.dict(
                    MODULE.os.environ,
                    {"FORMPOTOK_OCR_PADDLE_CACHE_HOME": str(configured)},
                    clear=True,
                ),
                patch.object(MODULE.sys, "frozen", True, create=True),
                patch.object(MODULE.sys, "executable", str(Path(temp_dir) / "runtime" / "sidecar.exe")),
            ):
                resolved = MODULE.resolve_bundled_paddle_cache_home()

            self.assertEqual(configured.resolve(), resolved)


class OcrSidecarVehicleCandidateTests(unittest.TestCase):
    def test_cmr_number_second_pass_emits_only_numero_marked_slash_candidate(self) -> None:
        candidates = MODULE.build_field_specific_crop_candidates(
            zone_name="cmr_number",
            blocks=[
                block("CMR", 0.88, 100, 100),
                block("№ 37/6", 0.82, 250, 100),
                block("3775", 0.99, 250, 140),
            ],
            page_number=1,
            page_width=1000,
            page_height=1000,
        )

        self.assertEqual(1, len(candidates))
        self.assertEqual("cmr_number", candidates[0]["FieldKey"])
        self.assertEqual("37/6", candidates[0]["NormalizedValue"])
        self.assertEqual("cmr-header-number-marker", candidates[0]["SelectionReason"])

    def test_cmr_number_second_pass_rejects_unmarked_or_unslashed_numbers(self) -> None:
        candidates = MODULE.build_field_specific_crop_candidates(
            zone_name="cmr_number",
            blocks=[
                block("37/6", 0.95, 100, 100),
                block("№ 3775", 0.95, 100, 140),
            ],
            page_number=1,
            page_width=1000,
            page_height=1000,
        )

        self.assertEqual([], candidates)

    def test_cmr_number_retry_requires_merged_marker_and_ambiguous_suffix(self) -> None:
        self.assertTrue(MODULE.cmr_number_zone_requires_retry([block("CMD№3775", 0.75, 100, 100)]))
        self.assertFalse(MODULE.cmr_number_zone_requires_retry([block("CMR | № 37/6", 0.75, 100, 100)]))
        self.assertFalse(MODULE.cmr_number_zone_requires_retry([block("CMR№3774", 0.75, 100, 100)]))

    def test_scaled_crop_blocks_are_remapped_to_full_page_coordinates(self) -> None:
        remapped = MODULE.remap_crop_blocks_to_page(
            {
                "TextBlocks": [
                    block("№ 37/6", 0.82, 200, 100),
                ],
            },
            offset_x=400,
            offset_y=20,
            full_width=1000,
            full_height=1400,
            zone_name="cmr_number",
            scale=4.0,
        )

        polygon = remapped[0]["Polygon"]
        self.assertEqual({"X": 450.0, "Y": 45.0}, polygon[0])
        self.assertEqual({"X": 475.0, "Y": 50.0}, polygon[2])

    def test_select_field_target_block_uses_geometric_reading_order(self) -> None:
        selected = MODULE.select_field_target_block(
            [
                block("AX121656", 0.99, 100, 140),
                block("E960CT156", 0.93, 100, 100),
            ],
            "truck_plate",
        )

        self.assertIsNotNone(selected)
        assert selected is not None
        self.assertEqual("E960CT156", selected["normalized_value"])
        self.assertEqual("reading-order-first-plate", selected["selection_reason"])
        self.assertEqual(1, selected["block_index"])

    def test_select_field_target_block_uses_last_plate_for_trailer(self) -> None:
        selected = MODULE.select_field_target_block(
            [
                block("BK940456", 0.98, 100, 100),
                block("AX121656", 0.82, 100, 140),
            ],
            "trailer_plate",
        )

        self.assertIsNotNone(selected)
        assert selected is not None
        self.assertEqual("AX121656", selected["normalized_value"])
        self.assertEqual("reading-order-last-plate", selected["selection_reason"])
        self.assertEqual(2, selected["competing_plate_count"])

    def test_load_date_target_selector_prefers_date_token_after_header(self) -> None:
        candidates = MODULE.build_field_specific_crop_candidates(
            zone_name="load_date",
            blocks=[
                block("CMR", 0.98, 100, 100),
                block("4. Место и дата погрузки 13 02 2026", 0.87, 100, 140),
            ],
            page_number=1,
            page_width=1000,
            page_height=1000,
        )

        self.assertEqual(1, len(candidates))
        self.assertEqual("load_date", candidates[0]["FieldKey"])
        self.assertEqual("13.02.2026", candidates[0]["NormalizedValue"])
        self.assertEqual("date-token-high-confidence", candidates[0]["SelectionReason"])
        self.assertEqual(2, candidates[0]["BlockIndex"])

    def test_vehicle_second_pass_emits_field_specific_truck_and_trailer_candidates(self) -> None:
        candidates = MODULE.build_field_specific_crop_candidates(
            zone_name="vehicle",
            blocks=[
                block("E960CT156", 0.93, 100, 100),
                block("AX121656", 0.99, 100, 140),
            ],
            page_number=1,
            page_width=1000,
            page_height=1000,
        )

        by_field = {candidate["FieldKey"]: candidate for candidate in candidates}

        self.assertEqual("E960CT156", by_field["truck_plate"]["NormalizedValue"])
        self.assertEqual("AX121656", by_field["trailer_plate"]["NormalizedValue"])
        self.assertEqual("reading-order-first-plate", by_field["truck_plate"]["SelectionReason"])
        self.assertEqual("reading-order-last-plate", by_field["trailer_plate"]["SelectionReason"])
        self.assertEqual(2, by_field["truck_plate"]["CompetingPlateCount"])
        self.assertEqual(2, by_field["trailer_plate"]["CompetingPlateCount"])

    def test_vehicle_second_pass_keeps_confusable_plate_out_of_strict_candidates(self) -> None:
        candidates = MODULE.build_field_specific_crop_candidates(
            zone_name="vehicle",
            blocks=[
                block("ES60CT156", 0.95, 100, 100),
                block("AX121656", 0.99, 100, 140),
            ],
            page_number=1,
            page_width=1000,
            page_height=1000,
        )

        by_field = {candidate["FieldKey"]: candidate for candidate in candidates}

        self.assertNotIn("truck_plate", by_field)
        self.assertEqual("AX121656", by_field["trailer_plate"]["NormalizedValue"])

    def test_vehicle_second_pass_uses_trailer_hint_over_higher_confidence_truck_like_noise(self) -> None:
        candidates = MODULE.build_field_specific_crop_candidates(
            zone_name="vehicle",
            blocks=[
                block("BB870656", 0.89, 100, 100),
                block("полуприцеп AX121656", 0.84, 100, 140),
            ],
            page_number=1,
            page_width=1000,
            page_height=1000,
        )

        by_field = {candidate["FieldKey"]: candidate for candidate in candidates}

        self.assertEqual("AX121656", by_field["trailer_plate"]["NormalizedValue"])
        self.assertEqual("field-hint", by_field["trailer_plate"]["SelectionReason"])


class OcrSidecarSourceContractTests(unittest.TestCase):
    def test_attach_source_identity_uses_original_input_bytes(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            input_path = Path(temp_dir) / "original.webp"
            input_path.write_bytes(b"original-webp-byte-stream")
            payload = {"Pages": [{"PageNumber": 1}]}
            expected_hash = MODULE.file_sha256(input_path)

            result = MODULE.attach_source_identity(payload, input_path)

        self.assertRegex(result["SourceContentHash"], r"^[0-9a-f]{64}$")
        self.assertEqual(expected_hash, result["SourceContentHash"])
        self.assertEqual(result["SourceContentHash"], result["Pages"][0]["SourceContentHash"])

    def test_critical_route_helpers_have_single_definitions(self) -> None:
        tree = ast.parse(MODULE_PATH.read_text(encoding="utf-8"))
        critical_helpers = {
            "route_line_looks_like_noise",
            "route_line_has_signal",
            "build_route_candidate_snippet",
        }
        definitions = [
            node.name
            for node in ast.walk(tree)
            if isinstance(node, (ast.FunctionDef, ast.AsyncFunctionDef))
            and node.name in critical_helpers
        ]

        self.assertCountEqual(critical_helpers, definitions)


class OcrSidecarCoordinateFrameTests(unittest.TestCase):
    ORIGINAL_SIZE = (6, 4)
    ORIGINAL_CROP_BOX = (1, 1, 4, 3)

    @staticmethod
    def create_pixel_fixture() -> Image.Image:
        image = Image.new("RGB", OcrSidecarCoordinateFrameTests.ORIGINAL_SIZE)
        image.putdata(
            [
                (x * 31, y * 47, (x + y * OcrSidecarCoordinateFrameTests.ORIGINAL_SIZE[0]) * 7)
                for y in range(OcrSidecarCoordinateFrameTests.ORIGINAL_SIZE[1])
                for x in range(OcrSidecarCoordinateFrameTests.ORIGINAL_SIZE[0])
            ]
        )
        return image

    def assert_crop_pixels_match_original(
        self,
        rotation_label: str,
        angle: int,
        selected_crop_box: tuple[int, int, int, int],
        inverse_angle: int,
    ) -> None:
        source = self.create_pixel_fixture()
        selected = source.rotate(angle, expand=True)
        selected_crop = selected.crop(selected_crop_box).rotate(inverse_angle, expand=True)
        original_crop = source.crop(self.ORIGINAL_CROP_BOX)

        selected_bbox = MODULE.normalized_bbox_from_crop_box(
            selected_crop_box,
            selected.width,
            selected.height,
        )
        remapped_bbox = MODULE.remap_normalized_bbox_to_original_frame(
            selected_bbox,
            rotation_label,
            source.width,
            source.height,
        )

        self.assertEqual(original_crop.size, selected_crop.size)
        self.assertEqual(original_crop.tobytes(), selected_crop.tobytes())
        self.assertEqual(
            {
                "X": 0.166667,
                "Y": 0.25,
                "Width": 0.5,
                "Height": 0.5,
            },
            remapped_bbox,
        )

    def test_rot90_bbox_and_crop_pixels_map_to_original(self) -> None:
        self.assert_crop_pixels_match_original(
            rotation_label="rot90",
            angle=90,
            selected_crop_box=(1, 2, 3, 5),
            inverse_angle=270,
        )

    def test_rot270_bbox_and_crop_pixels_map_to_original(self) -> None:
        self.assert_crop_pixels_match_original(
            rotation_label="rot270",
            angle=270,
            selected_crop_box=(1, 1, 3, 4),
            inverse_angle=90,
        )

    def test_page_polygon_is_remapped_to_original_pixel_frame(self) -> None:
        selected_page = {
            "PageNumber": 1,
            "Width": 4,
            "Height": 6,
            "TextBlocks": [
                {
                    "Id": "block-1",
                    "Text": "test",
                    "Confidence": 0.9,
                    "Polygon": [
                        {"X": 1.0, "Y": 2.0},
                        {"X": 3.0, "Y": 2.0},
                        {"X": 3.0, "Y": 5.0},
                        {"X": 1.0, "Y": 5.0},
                    ],
                }
            ],
            "TableBlocks": [],
        }

        result = MODULE.remap_page_to_original_frame(selected_page, "rot90", 6, 4)

        self.assertEqual(6, result["Width"])
        self.assertEqual(4, result["Height"])
        self.assertEqual("original-pixels", result["CoordinateFrame"])
        self.assertEqual("rot90", result["SelectedVariant"])
        self.assertEqual(
            [
                {"X": 4.0, "Y": 1.0},
                {"X": 4.0, "Y": 3.0},
                {"X": 1.0, "Y": 3.0},
                {"X": 1.0, "Y": 1.0},
            ],
            result["TextBlocks"][0]["Polygon"],
        )
        self.assertEqual(4, selected_page["Width"], "The selected-frame input must not be mutated.")

    def test_candidate_uses_its_actual_crop_variant_and_removes_internal_marker(self) -> None:
        candidate = {
            "FieldKey": "transport_weight_tons",
            "bbox_normalized": {
                "X": 0.25,
                "Y": 1.0 / 6.0,
                "Width": 0.5,
                "Height": 0.5,
            },
            "_bbox_variant_label": "rot270",
        }

        result = MODULE.remap_candidate_bbox_to_original_frame(
            candidate,
            selected_variant_label="rot90",
            original_width=6,
            original_height=4,
        )

        self.assertEqual(
            {"X": 0.166667, "Y": 0.25, "Width": 0.5, "Height": 0.5},
            result["bbox_normalized"],
        )
        self.assertEqual("original-pixels", result["bbox_coordinate_frame"])
        self.assertNotIn("_bbox_variant_label", result)

    def test_original_variant_is_a_geometry_noop(self) -> None:
        bbox = {"X": 0.1, "Y": 0.2, "Width": 0.3, "Height": 0.4}

        result = MODULE.remap_normalized_bbox_to_original_frame(bbox, "original", 100, 200)

        self.assertEqual(bbox, result)

    def test_unknown_variant_fails_closed(self) -> None:
        with self.assertRaisesRegex(ValueError, "Unsupported OCR image variant"):
            MODULE.inverse_transform_variant_point(1.0, 2.0, "rot180", 6, 4)

    def test_run_paddle_extract_emits_original_frame_for_each_rotated_variant(self) -> None:
        source = self.create_pixel_fixture()
        selected_boxes = {
            "rot90": (1, 2, 3, 5),
            "rot270": (1, 1, 3, 4),
        }

        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            input_path = root / "source.png"
            source.save(input_path)

            for selected_label, selected_box in selected_boxes.items():
                with self.subTest(selected_label=selected_label):
                    artifacts_dir = root / selected_label

                    def fake_page_result(**kwargs):
                        image_path = kwargs["image_path"]
                        with Image.open(image_path) as selected_image:
                            width, height = selected_image.size
                        label = (
                            "rot90"
                            if image_path.stem.endswith("-rot90")
                            else "rot270"
                            if image_path.stem.endswith("-rot270")
                            else "original"
                        )
                        left, top, right, bottom = selected_box
                        return {
                            "page": {
                                "PageNumber": 1,
                                "Width": width,
                                "Height": height,
                                "TextBlocks": [
                                    {
                                        "Id": "block-1",
                                        "Text": "E960CT156",
                                        "Confidence": 0.99,
                                        "Polygon": [
                                            {"X": left, "Y": top},
                                            {"X": right, "Y": top},
                                            {"X": right, "Y": bottom},
                                            {"X": left, "Y": bottom},
                                        ],
                                    }
                                ],
                                "TableBlocks": [],
                            },
                            "raw_entries": [],
                            "elapsed": 0.01,
                            "low_confidence_count": 0,
                            "metrics": {
                                "score": 1.0 if label == selected_label else 0.0,
                                "block_count": 1.0,
                            },
                            "detection_model_dir": None,
                            "recognition_model_dir": None,
                            "recognition_model_name": "fixture",
                        }

                    def fake_handwritten_candidates(**kwargs):
                        self.assertEqual(selected_label, kwargs["selected_variant_label"])
                        page = kwargs["page"]
                        return (
                            [
                                {
                                    "FieldKey": "transport_weight_tons",
                                    "RawValue": "20000",
                                    "NormalizedValue": "20",
                                    "SourceType": "fixture-crop",
                                    "bbox_normalized": MODULE.normalized_bbox_from_crop_box(
                                        selected_box,
                                        page["Width"],
                                        page["Height"],
                                    ),
                                }
                            ],
                            [],
                        )

                    with (
                        patch.object(MODULE, "create_paddle_ocr", return_value=object()),
                        patch.object(MODULE, "build_paddle_page_result", side_effect=fake_page_result),
                        patch.object(MODULE, "assess_image_quality", return_value={}),
                        patch.object(MODULE, "build_quality_warnings", return_value=[]),
                        patch.object(MODULE, "SECOND_PASS_ZONES", []),
                        patch.object(
                            MODULE,
                            "build_handwritten_weight_crop_candidates",
                            side_effect=fake_handwritten_candidates,
                        ),
                        patch.object(MODULE, "distribution_version", return_value="fixture"),
                    ):
                        result = MODULE.run_paddle_extract(
                            input_path=input_path,
                            image_paths=[input_path],
                            artifacts_dir=artifacts_dir,
                            lang="rus+eng",
                            profile="mobile-cpu",
                            warnings=[],
                            detection_model_dir=None,
                            recognition_model_dir=None,
                            recognition_model_name=None,
                        )

                    page = result["Pages"][0]
                    candidate = result["Candidates"][0]
                    self.assertEqual(self.ORIGINAL_SIZE, (page["Width"], page["Height"]))
                    self.assertEqual("original-pixels", page["CoordinateFrame"])
                    self.assertEqual(selected_label, page["SelectedVariant"])
                    self.assertEqual(
                        {"X": 0.166667, "Y": 0.25, "Width": 0.5, "Height": 0.5},
                        candidate["bbox_normalized"],
                    )
                    self.assertEqual("original-pixels", candidate["bbox_coordinate_frame"])

    def test_two_column_order_is_text_only(self) -> None:
        with tempfile.TemporaryDirectory() as temp_dir:
            root = Path(temp_dir)
            image_path = root / "source.png"
            Image.new("RGB", (110, 90), "white").save(image_path)
            blocks = [
                {
                    "Id": f"{side}{index}",
                    "Text": f"{side}{index}",
                    "Confidence": 0.99,
                    "Polygon": [
                        {"X": x, "Y": index * 9},
                        {"X": x + 35, "Y": index * 9},
                        {"X": x + 35, "Y": index * 9 + 5},
                        {"X": x, "Y": index * 9 + 5},
                    ],
                }
                for index in range(8)
                for side, x in (("L", 5), ("R", 65))
            ]
            page_result = {
                "page": {"PageNumber": 1, "Width": 110, "Height": 90,
                         "TextBlocks": blocks, "TableBlocks": []},
                "raw_entries": [], "elapsed": 0.01, "low_confidence_count": 0,
                "metrics": {"score": 1.0, "block_count": len(blocks)},
                "detection_model_dir": None, "recognition_model_dir": None,
                "recognition_model_name": "fixture",
            }
            with (
                patch.object(MODULE, "create_paddle_ocr", return_value=object()),
                patch.object(MODULE, "prepare_paddle_variants", return_value=[("original", image_path)]),
                patch.object(MODULE, "build_paddle_page_result", return_value=page_result),
                patch.object(MODULE, "assess_image_quality", return_value={}),
                patch.object(MODULE, "build_quality_warnings", return_value=[]),
                patch.object(MODULE, "SECOND_PASS_ZONES", []),
                patch.object(MODULE, "build_handwritten_weight_crop_candidates", return_value=([], [])),
                patch.object(MODULE, "distribution_version", return_value="fixture"),
            ):
                def extract(text_only):
                    return MODULE.run_paddle_extract(
                        input_path=image_path, image_paths=[image_path],
                        artifacts_dir=root / ("text" if text_only else "legacy"),
                        lang="rus+eng", profile="mobile-cpu", warnings=[],
                        detection_model_dir=None, recognition_model_dir=None,
                        recognition_model_name=None, text_only=text_only,
                    )["Pages"][0]["TextBlocks"]

                self.assertEqual(
                    [f"L{i}" for i in range(8)] + [f"R{i}" for i in range(8)],
                    [block["Text"] for block in extract(True)],
                )
                self.assertEqual(
                    [block["Text"] for block in blocks],
                    [block["Text"] for block in extract(False)],
                )


class TextOnlyCliErrorContractTests(unittest.TestCase):
    def test_real_cli_emits_only_structured_size_or_missing_model_failure(self):
        for scenario, expected_code in (("oversized", "TEXT_ONLY_SOURCE_LIMIT"), ("missing-models", "OFFLINE_MODELS_MISSING")):
            with self.subTest(scenario=scenario), tempfile.TemporaryDirectory() as temporary:
                root = Path(temporary)
                source = root / "private-customer-source.png"
                if scenario == "oversized":
                    with source.open("wb") as handle:
                        handle.truncate(MODULE.TEXT_ONLY_MAX_SOURCE_BYTES + 1)
                else:
                    Image.new("RGB", (20, 20), "white").save(source)
                environment = dict(os.environ, FORMPOTOK_OCR_PADDLE_CACHE_HOME=str(root / "absent-models"))
                completed = subprocess.run(
                    [sys.executable, str(MODULE_PATH), "extract", "--input", str(source), "--output", str(root / "result.json"),
                     "--engine", "paddle", "--text-only", "--offline", "--max-pages", "20", "--max-pixels", "20000000"],
                    capture_output=True, text=True, encoding="utf-8", env=environment, timeout=30,
                )
                self.assertEqual(1, completed.returncode)
                self.assertEqual({"errorCode": expected_code}, json.loads(completed.stderr))
                self.assertEqual("", completed.stdout)
                self.assertNotIn(str(source), completed.stderr)
                self.assertFalse((root / "result.json").exists())


if __name__ == "__main__":
    unittest.main()
