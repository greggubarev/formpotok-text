# Repository rules

## Windows release

- Treat source builds, UI tests, ZIP verification and local smoke runs as separate evidence. None alone establishes a shippable Windows release. Use the repo-local `formpotok-windows-release` skill for the repeatable procedure.
- Build a release candidate from a clean, committed app tree. The portable ZIP and installer must identify the same app commit and `VERSION`. A reused OCR runtime may have an earlier build commit only when its OCR source fingerprint, executable hash and original provenance are verified; never promote a `developmentOnly` package or use `-AllowDevelopment` as a release gate.
- Follow `docs/VERSIONING.md`: `VERSION` is the only product version, platform belongs in artifact names, and a published version/tag/assets must never be repurposed for different bytes. Run `scripts/check-version.ps1 -RequireTag` before publication.
- Before publication, test the exact ZIP and installer on clean Windows x64 without Python, .NET SDK or network access. Archive verification is not an execution test. Keep user documents, model caches, keys and recognition outputs out of Git and release materials.
