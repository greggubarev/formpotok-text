---
name: formpotok-windows-release
description: Build, verify, and hand off Windows x64 portable and installer release candidates for FormPotok Text. Use for release preparation or release-workflow audits, not ordinary source builds.
---

# Windows release candidate

Run from the repository root in PowerShell on Windows x64. This prepares and checks a candidate; it does not require publication. Check the current scripts and `docs/DEVELOPMENT.md` if they change.

1. Confirm `git status --porcelain=v1 --untracked-files=all` is empty, record `git rev-parse HEAD`, and run `scripts/check-version.ps1`. Follow `docs/VERSIONING.md`; a candidate needs no tag, but publication requires an annotated `v<VERSION>` tag on the exact commit and `scripts/check-version.ps1 -RequireTag`. Use a fresh `output/` subdirectory for each candidate; never replace published assets for the same version.
2. Run the .NET, Python sidecar and UI smoke tests listed in `docs/DEVELOPMENT.md`, including `tests/Test-ModelLock.ps1`, `tests/Test-RuntimeDependencyLock.ps1` and `tests/test_generate_wheel_lock.py`. Prepare the five required local model directories under `official_models` from the pinned revisions in `packaging/model-lock.json`. The Windows runtime build installs hash-checked binary wheels from `packaging/win-x64-cp310-wheels.lock` and checks the installed snapshot against `packaging/win-x64-runtime-dependencies.lock`; the portable and artifact smoke checks repeat validation. Keep a separate review of dependency licenses and required notices before public distribution.
3. Build a native OCR runtime, or reuse one after `build-windows-portable.ps1 -ValidateRuntimeOnly` accepts its OCR source fingerprint, executable hash and recorded build provenance. Keep the runtime's original `gitCommit`; it need not equal the app commit. Inspect `sourceDirty` and the package's `developmentOnly` field. Example paths below build a new runtime; for reuse, point `$runtimeRoot` at the prior build and skip its build command.

```powershell
$runtimeRoot = 'output/ocr-sidecar-runtime-candidate'
$portableRoot = 'output/portable-candidate'
$models = 'C:\path\to\paddlex-model-cache'
$wheelhouse = 'C:\path\to\win-x64-cp310-wheelhouse'
./scripts/build-ocr-sidecar-runtime.ps1 -RuntimeIdentifier win-x64 -OutputRoot $runtimeRoot -WheelhouseDirectory $wheelhouse
./scripts/build-windows-portable.ps1 -RuntimeDirectory "$runtimeRoot/win-x64/formpotok-ocr-sidecar" -ModelCacheDirectory $models -ValidateRuntimeOnly
./scripts/build-windows-portable.ps1 -RuntimeDirectory "$runtimeRoot/win-x64/formpotok-ocr-sidecar" -ModelCacheDirectory $models -OutputRoot $portableRoot
$version = (Get-Content VERSION -Raw).Trim()
$zip = Join-Path $portableRoot "FormPotok.Text-win-x64-v$version.zip"
./scripts/verify-windows-portable.ps1 -Archive $zip
```

4. Build the installer only after the ZIP passes without `-AllowDevelopment`. The installer script verifies the ZIP and extracts it into a temporary build directory; no separately prepared source folder is needed.

```powershell
./scripts/build-windows-installer.ps1 -PortableArchive $zip -OutputRoot output/installer-candidate
```

5. Run `LocalTextSmoke` against the exact portable package as shown in `docs/DEVELOPMENT.md`; inspect `smoke-summary.json`. Build the self-contained smoke kit with `scripts/build-windows-smoke-kit.ps1` and run it with the exact ZIP and installer on clean offline Windows x64; its artifact report checks package hashes, synthetic OCR, install and uninstall without SDK or Python. `cleanOfflineMachineVerified: false` is deliberate: record that environment gate independently. The synthetic diagnostic verdict does not establish production OCR quality. Also check GUI launch, PDF/PNG/JPEG recognition, review, TXT export and restart, resources and absence of unexpected network activity. Stop promotion if any check fails or tested bytes differ from the candidate.
6. Record the app commit, runtime's original provenance and verified fingerprint, version, SHA-256 for ZIP and installer, model provenance, tested artifact hashes, platform and known limitations with the handoff. Before publication verify the annotated tag, publish preview/rc as prerelease and keep the tag and assets immutable. Both deliverables are unsigned (`signingStatus: unsigned` for ZIP); disclose the Windows unknown-publisher warning if they are distributed.
