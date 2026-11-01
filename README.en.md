<img src="apps/FormPotok.App/Assets/formpotok-mark.png" alt="FormPotok mark" width="64">

# FormPotok: Text

An offline desktop app for printed Russian text in PDF, PNG, and JPEG files. It shows the source page beside the recognized text so you can correct errors before exporting TXT. Documents stay on your computer.

[Русский](README.md)

## Features

- Processes a queue on the CPU and keeps it between sessions.
- Flags low-confidence lines, links them to the source page when coordinates are available, saves review marks, and searches the text.
- Exports a corrected document as TXT or combines several documents in a chosen order with optional headings.
- Can save a reviewed document to a local reference set for CER and WER comparisons between OCR versions.

Review is part of the document workflow, from source page to corrected file. See [how recognition works](docs/RECOGNITION.md) and the [OCR comparisons](docs/OCR_COMPARISONS.md). The published OCR measurements use synthetic pages, not a random sample of user documents.

## Source and setup

The [source snapshot](https://github.com/greggubarev/formpotok-text/releases/tag/source-2026-11-01) is available. There is no verified public installer yet. Building the UI requires Windows x64 and .NET SDK 10; recognition also needs a local OCR runtime and models. Follow the [development guide](docs/DEVELOPMENT.md) for build and test steps.

Start with the [sample document and reference text](examples/README.md). The [user guide](docs/USER_GUIDE.md) covers the queue, review, export, and common problems in Russian.

## Limits

- Up to 20 MiB per file, 20 pages per PDF, and 20 million pixels per page.
- Handwriting, damaged scans, and complex tables need manual review.
- TXT does not reconstruct the original layout. The app does not automatically extract transport-document fields.

Working copies and edits stay under `%LOCALAPPDATA%\FormPotok\Text`. Recognition needs no server. Technical statistics are off by default and remain local if enabled. Do not publish documents or OCR results containing other people's data. See [local data handling](docs/USER_GUIDE.md#данные-и-конфиденциальность).

## Development

Open work is listed in [issues](https://github.com/greggubarev/formpotok-text/issues) and grouped by [milestone](https://github.com/greggubarev/formpotok-text/milestones). For a bug report, include the version, file format, reproduction steps, and error text. Do not attach private documents without the right to publish them.

The code is [MIT licensed](LICENSE). Third-party libraries and models have their own licenses; see [model provenance](docs/MODEL_PROVENANCE.md).
