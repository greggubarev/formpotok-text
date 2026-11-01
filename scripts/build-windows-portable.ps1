param(
    [Parameter(Mandatory = $true)][string] $RuntimeDirectory,
    [Parameter(Mandatory = $true)][string] $ModelCacheDirectory,
    [string] $OutputRoot = 'output/portable',
    [switch] $ValidateRuntimeOnly
)

$ErrorActionPreference = 'Stop'
$repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (Get-Content -Raw -LiteralPath (Join-Path $repo 'VERSION')).Trim()
& (Join-Path $PSScriptRoot 'check-version.ps1') -Version $version | Out-Null
$runtime = [IO.Path]::GetFullPath($RuntimeDirectory)
$models = [IO.Path]::GetFullPath($ModelCacheDirectory)
$runtimeExe = Join-Path $runtime 'formpotok-ocr-sidecar.exe'
if (-not (Test-Path -LiteralPath $runtimeExe -PathType Leaf)) { throw "Missing OCR runtime: $runtimeExe" }
$runtimeManifest = Join-Path (Split-Path -Parent $runtime) 'ocr-sidecar-runtime-manifest.json'
if (-not (Test-Path -LiteralPath $runtimeManifest -PathType Leaf)) { throw "Missing OCR runtime manifest: $runtimeManifest" }
$sourceSha = (& git -C $repo rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $sourceSha -notmatch '^[0-9a-f]{40}$') { throw 'Cannot resolve source commit.' }
$sourceDirty = -not [string]::IsNullOrWhiteSpace((& git -C $repo status --porcelain=v1 --untracked-files=all | Out-String))
$runtimeEvidence = Get-Content -Raw -LiteralPath $runtimeManifest | ConvertFrom-Json
if ($runtimeEvidence.schemaVersion -ne '3.0' -or
    $runtimeEvidence.runtimeIdentifier -ne 'win-x64' -or
    $runtimeEvidence.platform -ne 'windows' -or
    $runtimeEvidence.architecture -ne 'x64' -or
    $runtimeEvidence.configuration -ne 'Release' -or
    $runtimeEvidence.gitCommit -notmatch '^[0-9a-f]{40}$' -or
    $runtimeEvidence.executableRelativePath -ne 'formpotok-ocr-sidecar/formpotok-ocr-sidecar.exe' -or
    $runtimeEvidence.executableSha256 -notmatch '^[0-9a-f]{64}$' -or
    $runtimeEvidence.sourceFingerprint.sha256 -notmatch '^[0-9a-f]{64}$') {
    throw 'OCR runtime manifest lacks complete, valid build provenance; rebuild the runtime.'
}
$currentFingerprintJson = & (Join-Path $PSScriptRoot 'build-ocr-sidecar-runtime.ps1') -RuntimeIdentifier win-x64 -FingerprintOnly
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace(($currentFingerprintJson | Out-String))) {
    throw 'Unable to fingerprint the current OCR runtime source.'
}
$currentFingerprint = $currentFingerprintJson | ConvertFrom-Json
if ($runtimeEvidence.sourceDirty -or $runtimeEvidence.sourceFingerprint.sourceDirty -or $currentFingerprint.sourceDirty) {
    throw 'OCR runtime source is dirty; rebuild from clean OCR inputs.'
}
if ($runtimeEvidence.sourceFingerprint.sha256 -ne $currentFingerprint.sha256) {
    throw 'OCR runtime inputs differ from the frozen runtime build.'
}
if ($runtimeEvidence.executableSha256 -ne (Get-FileHash -LiteralPath $runtimeExe -Algorithm SHA256).Hash.ToLowerInvariant()) {
    throw 'OCR runtime executable SHA-256 mismatch.'
}
& (Join-Path $PSScriptRoot 'verify-runtime-dependency-lock.ps1') -ManifestPath $runtimeManifest | Out-Null
$sourceDirty = $sourceDirty -or [bool]$runtimeEvidence.sourceDirty
if ($ValidateRuntimeOnly) {
    Write-Output "OCR runtime validated. Built from $($runtimeEvidence.gitCommit); packaging source is $sourceSha; DevelopmentOnly: $sourceDirty"
    return
}

$requiredModels = @('PP-OCRv5_mobile_det', 'eslav_PP-OCRv5_mobile_rec', 'PP-LCNet_x1_0_doc_ori', 'UVDoc', 'PP-LCNet_x1_0_textline_ori')
foreach ($name in $requiredModels) {
    $dir = Join-Path $models "official_models/$name"
    if (-not (Test-Path -LiteralPath $dir -PathType Container) -or
        -not (Get-ChildItem -LiteralPath $dir -Recurse -File | Select-Object -First 1)) {
        throw "Missing or empty OCR model: $dir"
    }
}
& (Join-Path $PSScriptRoot 'verify-model-lock.ps1') -ModelRoot (Join-Path $models 'official_models') | Out-Null

$output = [IO.Path]::GetFullPath((Join-Path $repo $OutputRoot))
if (-not $output.StartsWith($repo + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'OutputRoot must be inside the repository.'
}
$name = "FormPotok.Text-win-x64-v$version"
$stage = Join-Path $output $name
$archive = Join-Path $output "$name.zip"
if ((Test-Path -LiteralPath $stage) -or (Test-Path -LiteralPath $archive)) {
    throw "Output already exists: $stage or $archive. Choose a fresh OutputRoot."
}
New-Item -ItemType Directory -Path $stage -Force | Out-Null
& dotnet publish (Join-Path $repo 'apps/FormPotok.App/FormPotok.App.csproj') -c Release -r win-x64 --self-contained true -o $stage
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }
if (-not (Test-Path -LiteralPath (Join-Path $stage 'Assets/formpotok-mark.ico') -PathType Leaf)) {
    throw 'Published app is missing the installer icon.'
}
$appVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $stage 'FormPotok.Text.exe'))
$expectedFileVersion = "$($version.Split('-')[0]).0"
if ($appVersion.ProductVersion -notin @($version, "$version+$sourceSha") -or
    $appVersion.FileVersion -ne $expectedFileVersion) {
    throw "Published app version differs from VERSION: product '$($appVersion.ProductVersion)', file '$($appVersion.FileVersion)'."
}

$sidecar = Join-Path $stage 'tools/ocr-sidecar'
New-Item -ItemType Directory -Path $sidecar -Force | Out-Null
$packagedRuntime = Join-Path $sidecar 'runtime'
New-Item -ItemType Directory -Path $packagedRuntime -Force | Out-Null
$headers = Join-Path $runtime '_internal/paddle/include'
& robocopy $runtime $packagedRuntime /E /XD $headers /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw 'Failed to copy the OCR runtime.' }
if (Test-Path -LiteralPath (Join-Path $packagedRuntime '_internal/paddle/include')) {
    throw 'Development headers must not be included in the Windows package.'
}
Copy-Item -LiteralPath $runtimeManifest -Destination (Join-Path $sidecar 'runtime-manifest.json')
Copy-Item -LiteralPath (Join-Path $repo 'packaging/win-x64-runtime-dependencies.lock') -Destination (Join-Path $sidecar 'runtime-dependencies.lock')
Copy-Item -LiteralPath (Join-Path $repo 'packaging/win-x64-cp310-wheels.lock') -Destination (Join-Path $sidecar 'runtime-wheels.lock')
$modelParent = Join-Path $sidecar 'models'
New-Item -ItemType Directory -Path $modelParent -Force | Out-Null
Copy-Item -LiteralPath $models -Destination (Join-Path $modelParent 'paddlex') -Recurse
Copy-Item -LiteralPath (Join-Path $repo 'packaging/model-lock.json') -Destination (Join-Path $sidecar 'model-lock.json')
Copy-Item -LiteralPath (Join-Path $repo 'LICENSE') -Destination (Join-Path $stage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repo 'README.md') -Destination (Join-Path $stage 'README.md')
Copy-Item -LiteralPath (Join-Path $repo 'docs/USER_GUIDE.md') -Destination (Join-Path $stage 'USER_GUIDE.md')
foreach ($format in @('png', 'jpg', 'pdf')) {
    Copy-Item -LiteralPath (Join-Path $repo "examples/sample-letter.$format") -Destination (Join-Path $stage "sample-letter.$format")
}

$longestRelativePath = (Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($stage, $_.FullName).Length
} | Measure-Object -Maximum).Maximum
if ($longestRelativePath -gt 140) {
    throw "Package contains a path too long for a typical Windows installation: $longestRelativePath characters."
}

$files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | Sort-Object FullName | ForEach-Object {
    [pscustomobject]@{
        path = [IO.Path]::GetRelativePath($stage, $_.FullName).Replace('\', '/')
        size = $_.Length
        sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
})
$manifest = [ordered]@{
    schemaVersion = 1
    product = 'FormPotok.Text'
    version = $version
    rid = 'win-x64'
    sourceCommit = $sourceSha
    developmentOnly = $sourceDirty
    signingStatus = 'unsigned'
    files = $files
}
$manifest | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'package-manifest.json') -Encoding utf8
Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $archive -CompressionLevel Optimal
$archiveHash = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
Write-Output "Archive: $archive"
Write-Output "SHA-256: $archiveHash"
Write-Output "DevelopmentOnly: $sourceDirty"
